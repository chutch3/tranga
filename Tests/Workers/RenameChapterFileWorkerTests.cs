using API;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Workers.MaintenanceWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

public class RenameChapterFileWorkerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly SeriesContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private const string NamingScheme = "?V(%M Vol %V/)%M - Ch.%C";

    public RenameChapterFileWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"RenameChapterTest_{Guid.NewGuid()}");
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

    private async Task<(Series manga, Chapter chapter)> SetupAsync(
        string chapterNumber = "1", int? volume = 5, string fileName = "One-Punch Man - Ch.1.cbz")
    {
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("One-Punch Man", "Desc", "url", SeriesReleaseStatus.Continuing,
            [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        var chapter = new Chapter(manga, chapterNumber, volume, null)
            { Downloaded = true, FileName = fileName };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();
        return (manga, chapter);
    }

    [Fact]
    public async Task DoWork_WhenFileExistsAtOldPath_MovesFileAndUpdatesDatabaseFileName()
    {
        var (manga, chapter) = await SetupAsync();
        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        File.WriteAllText(Path.Combine(mangaDir, "One-Punch Man - Ch.1.cbz"), "fake content");

        const string newFileName = "One-Punch Man Vol 5/One-Punch Man - Ch.1.cbz";
        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new RenameChapterFileWorker(chapter.Key, newFileName, settings);
        await worker.DoWork(_mockScope.Object);

        Assert.False(File.Exists(Path.Combine(mangaDir, "One-Punch Man - Ch.1.cbz")));
        Assert.True(File.Exists(Path.Combine(mangaDir, "One-Punch Man Vol 5", "One-Punch Man - Ch.1.cbz")));

        var updated = await _mangaContext.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal(newFileName, updated.FileName);
    }

    [Fact]
    public async Task DoWork_WhenFileDoesNotExistAtOldPath_UpdatesDatabaseFilenameWithoutError()
    {
        var (_, chapter) = await SetupAsync();

        const string newFileName = "One-Punch Man Vol 5/One-Punch Man - Ch.1.cbz";
        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new RenameChapterFileWorker(chapter.Key, newFileName, settings);
        await worker.DoWork(_mockScope.Object);

        var updated = await _mangaContext.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal(newFileName, updated.FileName);
    }

    [Fact]
    public async Task DoWork_WhenDestinationAlreadyExists_DoesNotThrowAndUpdatesDatabaseFileName()
    {
        var (manga, chapter) = await SetupAsync(fileName: "One-Punch Man - Ch.1.cbz");
        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Source file exists at old path
        File.WriteAllText(Path.Combine(mangaDir, "One-Punch Man - Ch.1.cbz"), "fake content");

        // A file already exists at the destination path (simulating a partial previous run or collision)
        string destDir = Path.Combine(mangaDir, "One-Punch Man Vol 5");
        Directory.CreateDirectory(destDir);
        File.WriteAllText(Path.Combine(destDir, "One-Punch Man - Ch.1.cbz"), "existing content");

        const string newFileName = "One-Punch Man Vol 5/One-Punch Man - Ch.1.cbz";
        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new RenameChapterFileWorker(chapter.Key, newFileName, settings);

        var ex = await Record.ExceptionAsync(() => worker.DoWork(_mockScope.Object));

        // Should not throw even though destination exists
        Assert.Null(ex);
        // DB is still updated to the intended filename
        var updated = await _mangaContext.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal(newFileName, updated.FileName);
    }

    [Fact]
    public async Task DoWork_WhenChapterKeyNotFound_CompletesWithoutError()
    {
        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new RenameChapterFileWorker("nonexistent-key", "anything.cbz", settings);
        var ex = await Record.ExceptionAsync(() => worker.DoWork(_mockScope.Object));
        Assert.Null(ex);
    }
}
