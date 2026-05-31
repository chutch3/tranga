using API;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

[Trait("Category", "Integration")]
public class SyncChapterFileNamesWorkerIntegrationTests : IAsyncLifetime
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"TrangaSyncIntegration_{Guid.NewGuid()}");

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

    // Chapter was downloaded before its volume was known — FileName has no volume prefix.
    // After the worker runs and the queued move executes:
    //   - DB FileName is updated to include the volume subdirectory
    //   - File on disk is moved to the new location
    [Fact]
    public async Task OPM_ChapterWithStaleFileName_MovesFileToVolumeSubdirectory()
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
            setupDb.Series.Add(manga);
            setupDb.Chapters.Add(new Chapter(manga, "1", 5, null)
                { Downloaded = true, FileName = "One-Punch Man - Ch.1.cbz" });
            await setupDb.SaveChangesAsync();
        }

        string mangaDir = Path.Combine(_tempDir, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        File.WriteAllText(Path.Combine(mangaDir, "One-Punch Man - Ch.1.cbz"), "fake cbz content");

        using var workerDb = CreateMangaContext(dbOptions);
        var settings = new TrangaSettings { ChapterNamingScheme = NamingScheme, AppData = _tempDir };
        var syncWorker = new SyncChapterFileNamesWorker(settings);
        var moveWorkers = await syncWorker.DoWork(CreateScope(workerDb));

        foreach (var renamer in moveWorkers.OfType<RenameChapterFileWorker>())
            await renamer.DoWork(CreateScope(workerDb));

        using var queryDb = CreateMangaContext(dbOptions);
        var result = await queryDb.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Equal("One-Punch Man Vol 5/One-Punch Man - Ch.1.cbz", result.FileName);
        Assert.False(File.Exists(Path.Combine(mangaDir, "One-Punch Man - Ch.1.cbz")));
        Assert.True(File.Exists(Path.Combine(mangaDir, "One-Punch Man Vol 5", "One-Punch Man - Ch.1.cbz")));
    }
}
