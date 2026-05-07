using API;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
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
    private readonly MangaContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private const string NamingScheme = "?V(%M Vol %V/)%M - Ch.%C";

    public SyncChapterFileNamesWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"SyncFileNamesTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testRoot);

        var mangaOptions = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _mangaContext = new MangaContext(mangaOptions);

        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _actionsContext = new ActionsContext(actionsOptions);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(MangaContext))).Returns(_mangaContext);
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

    private (FileLibrary library, Manga manga) SetupMangaAndLibrary(string mangaName = "One-Punch Man")
    {
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Manga(mangaName, "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
        return (library, manga);
    }

    [Fact]
    public async Task DoWork_WhenFileNameDoesNotMatchNamingScheme_UpdatesFileNameInDb()
    {
        var (_, manga) = SetupMangaAndLibrary();
        var chapter = new Chapter(manga, "1", 5, null) { Downloaded = true, FileName = "One-Punch Man - Ch.1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new SyncChapterFileNamesWorker(settings);
        await worker.DoWork(_mockScope.Object);

        var updated = await _mangaContext.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal("One-Punch Man Vol 5/One-Punch Man - Ch.1.cbz", updated.FileName);
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
    public async Task DoWork_WhenFileExistsAtOldPath_MovesFileInlineWithoutRequiringMoveWorker()
    {
        var (_, manga) = SetupMangaAndLibrary();
        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        File.WriteAllText(Path.Combine(mangaDir, "One-Punch Man - Ch.1.cbz"), "fake content");

        var chapter = new Chapter(manga, "1", 5, null) { Downloaded = true, FileName = "One-Punch Man - Ch.1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new SyncChapterFileNamesWorker(settings);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        Assert.Empty(newWorkers);
        Assert.False(File.Exists(Path.Combine(mangaDir, "One-Punch Man - Ch.1.cbz")));
        Assert.True(File.Exists(Path.Combine(mangaDir, "One-Punch Man Vol 5", "One-Punch Man - Ch.1.cbz")));
    }

    [Fact]
    public async Task DoWork_WhenFileDoesNotExistAtOldPath_UpdatesDbWithoutReturningMoveWorker()
    {
        var (_, manga) = SetupMangaAndLibrary();
        var chapter = new Chapter(manga, "1", 5, null) { Downloaded = true, FileName = "One-Punch Man - Ch.1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new SyncChapterFileNamesWorker(settings);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        Assert.Empty(newWorkers);
        var updated = await _mangaContext.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal("One-Punch Man Vol 5/One-Punch Man - Ch.1.cbz", updated.FileName);
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
