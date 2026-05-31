using System.Collections.Concurrent;
using System.Collections.Generic;
using API.Controllers;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Services;
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

    private static SeriesContext CreateMangaContext(DbContextOptions<SeriesContext> options) => new(options);

    private static IServiceScope CreateScope(SeriesContext mangaContext)
    {
        var actionsContext = new ActionsContext(
            new DbContextOptionsBuilder<ActionsContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(SeriesContext))).Returns(mangaContext);
        sp.Setup(x => x.GetService(typeof(ActionsContext))).Returns(actionsContext);
        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);
        return scope.Object;
    }

    private static Mock<IMangaDexSearchService> MakeEmptySearchService()
    {
        var mock = new Mock<IMangaDexSearchService>();
        mock.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MangaDexSearchResult>());
        mock.Setup(s => s.GetChapterToVolumeMapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());
        return mock;
    }

    // Three manga are in the DB with chapters missing volumes. Parallelism is set to 2,
    // so only 2 pool workers are spawned — but they share one queue of 3 items and together
    // drain it completely. All 3 manga must have their volumes resolved.
    [Fact]
    public async Task ThreeMangaWithParallelism2_AllMangaGetVolumesResolved()
    {
        string dbName = Guid.NewGuid().ToString();
        var dbOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName).Options;

        var mangaKeys = new List<string>();
        using (var setupDb = new SeriesContext(dbOptions))
        {
            var library = new FileLibrary(_tempDir, "Integration Library");
            setupDb.FileLibraries.Add(library);
            for (int i = 1; i <= 3; i++)
            {
                var manga = new Series($"Series {i}", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
                manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", $"uuid-{i}", null));
                setupDb.Series.Add(manga);
                setupDb.Chapters.Add(new Chapter(manga, "1", null, null)
                    { Downloaded = true, FileName = $"manga{i}_ch1.cbz" });
                mangaKeys.Add(manga.Key);
            }
            await setupDb.SaveChangesAsync();
        }

        var mockResolver = new Mock<IMangaDexVolumeResolver>();
        mockResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Series>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { ["1"] = 1 });

        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly,
            VolumeResolutionParallelism = 2,
            AppData = _tempDir
        };
        var factory = new ResolveMissingVolumesForMangaWorkerFactory(settings, mockResolver.Object, MakeEmptySearchService().Object);

        // Build the coordinator directly (skip the endpoint for this test)
        using var coordinatorDb = CreateMangaContext(dbOptions);
        var coordinator = new ResolveMissingVolumesWorker(settings, factory);
        var poolWorkers = await coordinator.DoWork(CreateScope(coordinatorDb));

        // Should spawn min(parallelism=2, manga=3) = 2 workers
        Assert.Equal(2, poolWorkers.Length);

        // Run both pool workers — together they drain the 3-item queue
        using var workerDb = CreateMangaContext(dbOptions);
        foreach (var worker in poolWorkers.OfType<ResolveMissingVolumesForMangaWorker>())
            await worker.DoWork(CreateScope(workerDb));

        // All 3 manga must have been processed despite fewer workers than manga
        using var queryDb = CreateMangaContext(dbOptions);
        var chapters = await queryDb.Chapters.ToListAsync();
        Assert.Equal(3, chapters.Count);
        Assert.All(chapters, c => Assert.Equal(1, c.VolumeNumber));
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
        var dbOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName).Options;

        Series manga;
        using (var setupDb = CreateMangaContext(dbOptions))
        {
            var library = new FileLibrary(_tempDir, "Integration Library");
            setupDb.FileLibraries.Add(library);
            manga = new Series("One-Punch Man", "Superhero comedy", "url",
                SeriesReleaseStatus.Continuing, [], [], [], [], library);
            manga.SourceIds.Add(
                new SourceId<Series>(manga, "MangaDex", "some-uuid", null));
            setupDb.Series.Add(manga);
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
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Series>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { ["1"] = 1, ["2"] = 1 });

        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly,
            ChapterNamingScheme = NamingScheme,
            AppData = _tempDir
        };
        var factory = new ResolveMissingVolumesForMangaWorkerFactory(settings, mockResolver.Object, MakeEmptySearchService().Object);

        // Call the endpoint — captures the queued ResolveMissingVolumesWorker (coordinator)
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
        await controller.ResetAndResolveVolumes(mockQueue.Object, settings, factory);

        // Verify volumes were cleared before the resolver ran
        using (var checkDb = CreateMangaContext(dbOptions))
        {
            var cleared = await checkDb.Chapters.ToListAsync();
            Assert.All(cleared, c => Assert.Null(c.VolumeNumber));
        }

        // Run the coordinator — returns pool workers
        using var workerDb = CreateMangaContext(dbOptions);
        var coordinator = Assert.IsType<ResolveMissingVolumesWorker>(capturedWorker);
        var poolWorkers = await coordinator.DoWork(CreateScope(workerDb));

        // Run each pool worker — resolution workers update DB only, never queue file moves
        foreach (var poolWorker in poolWorkers.OfType<ResolveMissingVolumesForMangaWorker>())
        {
            var workers = await poolWorker.DoWork(CreateScope(workerDb));
            Assert.DoesNotContain(workers, w => w is RenameChapterFileWorker);
        }

        using var queryDb = CreateMangaContext(dbOptions);
        var ch1 = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "2");

        Assert.Equal(1, ch1.VolumeNumber);
        Assert.Equal(1, ch2.VolumeNumber);
    }
}
