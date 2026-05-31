using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

public class CleanupOrphanedFilesWorkerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly SeriesContext _mangaContext;
    private readonly ActionsContext _actionsContext;

    public CleanupOrphanedFilesWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"CleanupTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testRoot);

        var mangaOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _mangaContext = new SeriesContext(mangaOptions);

        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _actionsContext = new ActionsContext(actionsOptions);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(SeriesContext))).Returns(_mangaContext);
        serviceProvider.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_actionsContext);

        _mockScope = new Mock<IServiceScope>();
        _mockScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, true);
        _mangaContext.Dispose();
        _actionsContext.Dispose();
    }

    [Fact]
    public async Task DoWork_RemovesOrphanedCbzFile()
    {
        // Setup library
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        
        // Setup tracked manga and chapter
        var manga = new Series("Tracked Series", "Desc", "http://example.com/cover.jpg", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        
        var chapter = new Chapter(manga, "1", 1) { Downloaded = true, FileName = "tracked.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        // Create files on disk
        string trackedFile = Path.Combine(_testRoot, manga.DirectoryName, "tracked.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(trackedFile)!);
        File.WriteAllText(trackedFile, "tracked content");

        string orphanedFile = Path.Combine(_testRoot, "orphaned.cbz");
        File.WriteAllText(orphanedFile, "orphaned content");

        // Run worker
        var worker = new CleanupOrphanedFilesWorker(dryRun: false);
        await worker.DoWork(_mockScope.Object);

        Assert.True(File.Exists(trackedFile), "Tracked file should still exist");
        Assert.False(File.Exists(orphanedFile), "Orphaned file should be deleted");
    }

    [Fact]
    public async Task DoWork_WhenFileMovedToSubdirectory_RemovesOrphanedOriginalFile()
    {
        // Setup library
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        
        // Setup tracked manga and chapter pointing to a subdirectory
        var manga = new Series("MoveManga", "Desc", "http://example.com/cover.jpg", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        
        string subDir = "Volume 1";
        string fileName = "chapter1.cbz";
        var chapter = new Chapter(manga, "1", 1) { Downloaded = true, FileName = Path.Combine(subDir, fileName) };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        // Create the tracked file in the subdirectory
        string trackedFile = Path.Combine(_testRoot, manga.DirectoryName, subDir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(trackedFile)!);
        File.WriteAllText(trackedFile, "new content");

        // Create the orphaned original file in the root manga directory
        string orphanedFile = Path.Combine(_testRoot, manga.DirectoryName, fileName);
        File.WriteAllText(orphanedFile, "old content");

        // Run worker
        var worker = new CleanupOrphanedFilesWorker(dryRun: false);
        await worker.DoWork(_mockScope.Object);

        Assert.True(File.Exists(trackedFile), "Tracked file in subdirectory should still exist");
        Assert.False(File.Exists(orphanedFile), "Orphaned original file in root directory should be deleted");
    }

    [Fact]
    public void Worker_IsPeriodicWithCorrectInterval()
    {
        var worker = new CleanupOrphanedFilesWorker();
        var periodic = Assert.IsAssignableFrom<IPeriodic>(worker);
        Assert.Equal(TimeSpan.FromDays(1), periodic.Interval);
    }

    [Fact]
    public async Task DoWork_UpdatesLastExecution()
    {
        var worker = new CleanupOrphanedFilesWorker();
        var periodic = (IPeriodic)worker;
        var before = DateTime.UtcNow;
        
        await worker.DoWork(_mockScope.Object);
        
        Assert.True(periodic.LastExecution >= before);
    }
}
