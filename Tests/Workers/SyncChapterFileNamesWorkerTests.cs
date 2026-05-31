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

public class SyncChapterFileNamesWorkerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly SeriesContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private const string NamingScheme = "?V(%M Vol %V/)%M - Ch.%C";

    public SyncChapterFileNamesWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SyncFileNamesTest_{Guid.NewGuid()}");
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

    private (FileLibrary library, Series manga) SetupMangaAndLibrary(string mangaName = "One-Punch Man")
    {
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series(mangaName, "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        return (library, manga);
    }

    [Fact]
    public async Task DoWork_WhenFileNameDoesNotMatch_QueuesRenameChapterFileWorker()
    {
        var (_, manga) = SetupMangaAndLibrary();
        var chapter = new Chapter(manga, "1", 5, null) { Downloaded = true, FileName = "One-Punch Man - Ch.1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new SyncChapterFileNamesWorker(settings);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        Assert.Single(newWorkers);
        Assert.IsType<RenameChapterFileWorker>(newWorkers[0]);
    }

    [Fact]
    public async Task DoWork_WhenFileNameAlreadyMatchesNamingScheme_QueuesNoMoveWorker()
    {
        var (_, manga) = SetupMangaAndLibrary();
        var chapter = new Chapter(manga, "1", 5, null)
            { Downloaded = true, FileName = "One-Punch Man Vol 5/One-Punch Man - Ch.1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new SyncChapterFileNamesWorker(settings);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        Assert.Empty(newWorkers);
    }

    [Fact]
    public async Task DoWork_WhenChapterNotDownloaded_SkipsChapter()
    {
        var (_, manga) = SetupMangaAndLibrary();
        var chapter = new Chapter(manga, "1", 5, null) { Downloaded = false, FileName = "One-Punch Man - Ch.1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new SyncChapterFileNamesWorker(settings);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        var unchanged = await _mangaContext.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal("One-Punch Man - Ch.1.cbz", unchanged.FileName);
        Assert.Empty(newWorkers);
    }

    [Fact]
    public async Task DoWork_WhenMultipleChaptersMismatched_QueuesOneRenameWorkerEach()
    {
        var (_, manga) = SetupMangaAndLibrary();
        _mangaContext.Chapters.Add(new Chapter(manga, "1", 5, null) { Downloaded = true, FileName = "One-Punch Man - Ch.1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", 5, null) { Downloaded = true, FileName = "One-Punch Man - Ch.2.cbz" });
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new SyncChapterFileNamesWorker(settings);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        Assert.Equal(2, newWorkers.Length);
        Assert.All(newWorkers, w => Assert.IsType<RenameChapterFileWorker>(w));
    }

    [Fact]
    public async Task DoWork_WhenChapterHasNullFileName_SkipsChapter()
    {
        var (_, manga) = SetupMangaAndLibrary();
        var chapter = new Chapter(manga, "1", 5, null) { Downloaded = true, FileName = null };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new SyncChapterFileNamesWorker(settings);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        Assert.Empty(newWorkers);
    }
}
