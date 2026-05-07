using System.Collections.Generic;
using API.Controllers;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Controllers;

[Trait("Category", "Integration")]
public class MaintenanceControllerIntegrationTests : IAsyncLifetime
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"TrangaMaintenanceIntegration_{Guid.NewGuid()}");

    private const string NamingScheme = "?V(%M Vol %V/)%M - Ch.%C";

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        return Task.CompletedTask;
    }

    private static MangaContext CreateMangaContext(DbContextOptions<MangaContext> options) => new(options);

    private static IServiceScope CreateScope(MangaContext mangaContext)
    {
        var actionsContext = new ActionsContext(
            new DbContextOptionsBuilder<ActionsContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(MangaContext))).Returns(mangaContext);
        sp.Setup(x => x.GetService(typeof(ActionsContext))).Returns(actionsContext);
        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);
        return scope.Object;
    }

    // Chapters had wrong volumes (5) from a previous buggy run. Files sit in the wrong
    // volume subdirectory on disk. After ResetAndResolveVolumes:
    //   - All volumes are cleared then re-resolved via the MangaDex map (1 for both chapters)
    //   - RenameChapterFileWorkers move files from Vol 5 → Vol 1 subdirectory
    //   - DB filenames are updated to reflect the correct paths
    [Fact]
    public async Task ChaptersWithWrongVolumes_AfterResetAndResolve_HaveCorrectVolumesAndFilePaths()
    {
        string dbName = Guid.NewGuid().ToString();
        var dbOptions = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(dbName).Options;

        Manga manga;
        using (var setupDb = CreateMangaContext(dbOptions))
        {
            var library = new FileLibrary(_tempDir, "Integration Library");
            setupDb.FileLibraries.Add(library);
            manga = new Manga("One-Punch Man", "Superhero comedy", "url",
                MangaReleaseStatus.Continuing, [], [], [], [], library);
            manga.MangaConnectorIds.Add(
                new MangaConnectorId<Manga>(manga, "MangaDex", "some-uuid", null));
            setupDb.Mangas.Add(manga);
            setupDb.Chapters.Add(new Chapter(manga, "1", 5, null)
                { Downloaded = true, FileName = "One-Punch Man Vol 5/One-Punch Man - Ch.1.cbz" });
            setupDb.Chapters.Add(new Chapter(manga, "2", 5, null)
                { Downloaded = true, FileName = "One-Punch Man Vol 5/One-Punch Man - Ch.2.cbz" });
            await setupDb.SaveChangesAsync();
        }

        string mangaDir = Path.Combine(_tempDir, manga.DirectoryName);
        string wrongVolDir = Path.Combine(mangaDir, "One-Punch Man Vol 5");
        Directory.CreateDirectory(wrongVolDir);
        File.WriteAllText(Path.Combine(wrongVolDir, "One-Punch Man - Ch.1.cbz"), "fake cbz");
        File.WriteAllText(Path.Combine(wrongVolDir, "One-Punch Man - Ch.2.cbz"), "fake cbz");

        var mockResolver = new Mock<IMangaDexVolumeResolver>();
        mockResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { ["1"] = 1, ["2"] = 1 });

        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly,
            ChapterNamingScheme = NamingScheme,
            AppData = _tempDir
        };

        // Call the endpoint — captures the queued ResolveMissingVolumesWorker
        BaseWorker? capturedWorker = null;
        var mockQueue = new Mock<IWorkerQueue>();
        mockQueue.Setup(q => q.AddWorker(It.IsAny<BaseWorker>()))
            .Callback<BaseWorker>(w => capturedWorker = w);

        using var controllerDb = CreateMangaContext(dbOptions);
        var actionsCtx = new ActionsContext(
            new DbContextOptionsBuilder<ActionsContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var controller = new MaintenanceController(controllerDb, actionsCtx);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        await controller.ResetAndResolveVolumes(mockQueue.Object, settings, mockResolver.Object);

        // Verify volumes were cleared before the resolver ran
        using (var checkDb = CreateMangaContext(dbOptions))
        {
            var cleared = await checkDb.Chapters.ToListAsync();
            Assert.All(cleared, c => Assert.Null(c.VolumeNumber));
        }

        // Run the ResolveMissingVolumesWorker
        using var workerDb = CreateMangaContext(dbOptions);
        var resolveWorker = Assert.IsType<ResolveMissingVolumesWorker>(capturedWorker);
        var renameWorkers = await resolveWorker.DoWork(CreateScope(workerDb));

        // Run the RenameChapterFileWorkers it queued
        foreach (var renamer in renameWorkers.OfType<RenameChapterFileWorker>())
            await renamer.DoWork(CreateScope(workerDb));

        using var queryDb = CreateMangaContext(dbOptions);
        var ch1 = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "2");

        Assert.Equal(1, ch1.VolumeNumber);
        Assert.Equal(1, ch2.VolumeNumber);
        Assert.Equal("One-Punch Man Vol 1/One-Punch Man - Ch.1.cbz", ch1.FileName);
        Assert.Equal("One-Punch Man Vol 1/One-Punch Man - Ch.2.cbz", ch2.FileName);
        Assert.True(File.Exists(Path.Combine(mangaDir, "One-Punch Man Vol 1", "One-Punch Man - Ch.1.cbz")));
        Assert.True(File.Exists(Path.Combine(mangaDir, "One-Punch Man Vol 1", "One-Punch Man - Ch.2.cbz")));
        Assert.False(File.Exists(Path.Combine(wrongVolDir, "One-Punch Man - Ch.1.cbz")));
        Assert.False(File.Exists(Path.Combine(wrongVolDir, "One-Punch Man - Ch.2.cbz")));
    }
}
