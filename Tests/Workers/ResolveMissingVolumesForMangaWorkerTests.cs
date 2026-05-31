using System.Collections.Concurrent;
using System.IO.Compression;
using API;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Services;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SixLabors.ImageSharp;
using Xunit;

namespace API.Tests.Workers;

public class ResolveMissingVolumesForMangaWorkerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly SeriesContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private readonly Mock<IMangaDexVolumeResolver> _mockMangaDexResolver;
    private readonly Mock<IMangaDexSearchService> _mockSearchService;

    public ResolveMissingVolumesForMangaWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"ResolveForMangaTest_{Guid.NewGuid()}");
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

        _mockMangaDexResolver = new Mock<IMangaDexVolumeResolver>();
        _mockMangaDexResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Series>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        _mockSearchService = new Mock<IMangaDexSearchService>();
        _mockSearchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MangaDexSearchResult>());
        _mockSearchService
            .Setup(s => s.GetChapterToVolumeMapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, true);
        _mangaContext.Dispose();
        _actionsContext.Dispose();
    }

    private ResolveMissingVolumesForMangaWorker MakeWorker(TrangaSettings settings, string mangaKey) =>
        new(new ConcurrentQueue<string>([mangaKey]), settings, _mockMangaDexResolver.Object, _mockSearchService.Object);

    private ResolveMissingVolumesForMangaWorker MakeWorker(TrangaSettings settings, string mangaKey, IMangaDexVolumeResolver resolver) =>
        new(new ConcurrentQueue<string>([mangaKey]), settings, resolver, _mockSearchService.Object);

    private ResolveMissingVolumesForMangaWorker MakeWorker(TrangaSettings settings, IEnumerable<string> mangaKeys) =>
        new(new ConcurrentQueue<string>(mangaKeys), settings, _mockMangaDexResolver.Object, _mockSearchService.Object);

    [Fact]
    public async Task DoWork_WhenExactLookupFails_FallsBackToColorHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));
        CreateGrayscaleCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.NotNull((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.NotNull((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenFirstChapterNotColor_AbortsHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test No Cover", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateGrayscaleCbz(Path.Combine(mangaDir, "chap1.cbz"));
        CreateColorCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenStrategyExactOnly_DoesNotRunHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Exact Only", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenFileMissing_SkipsGracefullyAndEvaluatesNext()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Missing File", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        // chap1.cbz intentionally absent
        CreateGrayscaleCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaHasExistingVolumes_HeuristicStartsFromMaxVolume()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        string dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SeriesContext>().UseInMemoryDatabase(dbName).Options;

        FileLibrary library;
        Series manga;
        using (var setupContext = new SeriesContext(options))
        {
            library = new FileLibrary(_testRoot, "Test Library");
            setupContext.FileLibraries.Add(library);
            manga = new Series("Test Continuation", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
            setupContext.Series.Add(manga);
            setupContext.Chapters.Add(new Chapter(manga, "1", 10, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
            setupContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
            await setupContext.SaveChangesAsync();
        }

        using var workerContext = new SeriesContext(options);
        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(SeriesContext))).Returns(workerContext);
        sp.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_actionsContext);
        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await new ResolveMissingVolumesForMangaWorker(
            new ConcurrentQueue<string>([manga.Key]), settings, _mockMangaDexResolver.Object, _mockSearchService.Object)
            .DoWork(scope.Object);

        Assert.Equal(11, (await workerContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexReturnsFullMap_AllChaptersGetVolumes()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test MangaDex Full", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        var resolver = new Mock<IMangaDexVolumeResolver>();
        resolver.Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Series>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { { "1", 3 }, { "2", 3 } });

        await MakeWorker(settings, manga.Key, resolver.Object).DoWork(_mockScope.Object);

        Assert.Equal(3, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.Equal(3, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexReturnsPartialMap_UnmappedChaptersRemainNull()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test MangaDex Partial", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "3", null, "Title 3") { Downloaded = true, FileName = "chap3.cbz" });
        await _mangaContext.SaveChangesAsync();

        var resolver = new Mock<IMangaDexVolumeResolver>();
        resolver.Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Series>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { { "1", 1 }, { "2", 1 } });

        await MakeWorker(settings, manga.Key, resolver.Object).DoWork(_mockScope.Object);

        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "3")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexReturnsEmptyMap_FallsBackToHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Fallback", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));
        CreateGrayscaleCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenConsecutiveColorChapters_AbortsHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Consecutive", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));
        CreateColorCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenGrayscaleContinuationAfterExistingVolume_AssignedToCurrentVolume()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        string dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SeriesContext>().UseInMemoryDatabase(dbName).Options;

        FileLibrary library;
        Series manga;
        using (var setupContext = new SeriesContext(options))
        {
            library = new FileLibrary(_testRoot, "Test Library");
            setupContext.FileLibraries.Add(library);
            manga = new Series("Test Grayscale Continuation", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
            setupContext.Series.Add(manga);
            setupContext.Chapters.Add(new Chapter(manga, "1", 10, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
            setupContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
            await setupContext.SaveChangesAsync();
        }

        using var workerContext = new SeriesContext(options);
        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(SeriesContext))).Returns(workerContext);
        sp.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_actionsContext);
        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateGrayscaleCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await new ResolveMissingVolumesForMangaWorker(
            new ConcurrentQueue<string>([manga.Key]), settings, _mockMangaDexResolver.Object, _mockSearchService.Object)
            .DoWork(scope.Object);

        Assert.Equal(10, (await workerContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenVolumesUpdated_ReturnsRenameWorkers()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Moves", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));

        var result = await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        // Resolution workers must NEVER queue file moves
        Assert.DoesNotContain(result, w => w is RenameChapterFileWorker);
        // But the DB update MUST still happen
        Assert.NotNull((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_NeverQueuesRenameWorker_WhenVolumeAssigned()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test No Rename", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));

        var result = await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        // Resolution workers must NEVER queue file moves
        Assert.DoesNotContain(result, w => w is RenameChapterFileWorker);
        // But the DB update MUST still happen
        Assert.NotNull((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenNamingSchemeHasNoVolume_NoRenameWorkerGenerated()
    {
        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess,
            ChapterNamingScheme = "%M - Ch.%C"
        };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test No Volume Scheme", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        var chapter = new Chapter(manga, "1", null, "Title 1") { Downloaded = true };
        chapter.FileName = chapter.GetArchiveFileName(settings.ChapterNamingScheme);
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, chapter.FileName!));

        var result = await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.DoesNotContain(result, w => w is RenameChapterFileWorker);
    }

    [Fact]
    public async Task DoWork_WhenZipHasNoImages_SkipsChapterAndTreatsNextAsFirst()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Empty Zip", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        using (var zip = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");
        CreateColorCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenExactOnlyAndMangaDexReturnsEmpty_ChaptersRemainNull()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Exact Empty", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenCorruptZipAfterVolumeEstablished_AssignsCurrentVolume()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Corrupt Zip", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));
        await File.WriteAllBytesAsync(Path.Combine(mangaDir, "chap2.cbz"), [0x00, 0x01, 0x02, 0x03]);

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenChapterNotDownloaded_IsExcluded()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Not Downloaded", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = false, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WithMultipleMangaInQueue_EachProcessedIndependently()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga1 = new Series("Test Multi One", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var manga2 = new Series("Test Multi Two", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.AddRange(manga1, manga2);
        _mangaContext.Chapters.Add(new Chapter(manga1, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga2, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        foreach (var manga in new[] { manga1, manga2 })
        {
            string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
            Directory.CreateDirectory(mangaDir);
            CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));
        }

        await MakeWorker(settings, [manga1.Key, manga2.Key]).DoWork(_mockScope.Object);

        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ParentMangaId == manga1.Key)).VolumeNumber);
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ParentMangaId == manga2.Key)).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenCoverNamedCoverJpg_ColorCoverDetected()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Cover Name", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        using (var zip = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
        {
            WriteColorImage(zip, "cover.jpg");
            WriteGrayscaleImage(zip, "001.jpg");
        }

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexThrowsException_FallsBackToColorHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Exception Fallback", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));

        var throwingResolver = new Mock<IMangaDexVolumeResolver>();
        throwingResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Series>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("MangaDex unavailable"));

        await MakeWorker(settings, manga.Key, throwingResolver.Object).DoWork(_mockScope.Object);

        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexMapHasNoMatchingChapters_FallsBackToHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test No Match Fallback", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "50", null, "Title") { Downloaded = true, FileName = "chap50.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "51", null, "Title") { Downloaded = true, FileName = "chap51.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap50.cbz"));
        CreateGrayscaleCbz(Path.Combine(mangaDir, "chap51.cbz"));

        // MangaDex map is non-empty but contains chapter numbers that don't match ours
        var resolver = new Mock<IMangaDexVolumeResolver>();
        resolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Series>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { ["1"] = 1, ["2"] = 1 });

        await MakeWorker(settings, manga.Key, resolver.Object).DoWork(_mockScope.Object);

        // Mapped=0 → resolvedExact=false → heuristic runs → color cover assigns vol 1
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "50")).VolumeNumber);
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "51")).VolumeNumber);
    }

    // ─── MetadataConfidence tests ────────────────────────────────────────────

    [Fact]
    public async Task DoWork_WhenStatusConfirmed_AssignsExactConfidenceToChapters()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Exact Confidence", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        manga.MetadataSource!.ExternalId = "confirmed-uuid";
        manga.MetadataSource.Status = MetadataSourceStatus.Confirmed;
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        var resolver = new Mock<IMangaDexVolumeResolver>();
        resolver.Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Series>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { { "1", 1 }, { "2", 1 } });

        await new ResolveMissingVolumesForMangaWorker(
            new ConcurrentQueue<string>([manga.Key]), settings, resolver.Object, _mockSearchService.Object)
            .DoWork(_mockScope.Object);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        Assert.Equal(MetadataConfidence.Exact, ch1.MetadataConfidence);
        Assert.Equal(MetadataConfidence.Exact, ch2.MetadataConfidence);
    }

    [Fact]
    public async Task DoWork_WhenHeuristicUsed_AssignsHeuristicConfidenceToChapters()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test Heuristic Confidence", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        // Status remains Unlinked but search returns nothing → heuristic fallback
        _mockSearchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MangaDexSearchResult>());
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));
        CreateGrayscaleCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        Assert.Equal(MetadataConfidence.Heuristic, ch1.MetadataConfidence);
        Assert.Equal(MetadataConfidence.Heuristic, ch2.MetadataConfidence);
    }

    // ─── Auto-match tests ────────────────────────────────────────────────────

    [Fact]
    public async Task DoWork_WhenStatusUnlinked_AttemptsAutoMatch_AndSetsAutoMatchedOnStrongCandidate()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        // Series with 2 chapters — chapter count matches the search result
        var manga = new Series("Berserk", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        manga.MetadataSource!.Status = MetadataSourceStatus.Unlinked;
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        // Strong candidate: high title similarity, chapter count matches
        _mockSearchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MangaDexSearchResult>
            {
                new() { MangaDexId = "berserk-uuid", Title = "Berserk", Author = null, ChapterCount = 2 }
            });
        _mockSearchService
            .Setup(s => s.GetChapterToVolumeMapAsync("berserk-uuid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { { "1", 1 }, { "2", 1 } });

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        var updatedSource = await _mangaContext.Set<MetadataSource>().FirstAsync(s => s.MangaId == manga.Key);
        Assert.Equal(MetadataSourceStatus.AutoMatched, updatedSource.Status);
        Assert.Equal("berserk-uuid", updatedSource.ExternalId);
        Assert.NotNull(updatedSource.MatchScore);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Equal(1, ch1.VolumeNumber);
        Assert.Equal(MetadataConfidence.Exact, ch1.MetadataConfidence);
    }

    [Fact]
    public async Task DoWork_WhenAutoMatchScoreBelowThreshold_SetsNoMatch()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("XYZ Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        manga.MetadataSource!.Status = MetadataSourceStatus.Unlinked;
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        // Return a candidate with very low title similarity
        _mockSearchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MangaDexSearchResult>
            {
                new() { MangaDexId = "some-uuid", Title = "Completely Different Title ABCDEF", Author = null, ChapterCount = 999 }
            });

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        var updatedSource = await _mangaContext.Set<MetadataSource>().FirstAsync(s => s.MangaId == manga.Key);
        Assert.Equal(MetadataSourceStatus.NoMatch, updatedSource.Status);
        Assert.Null(updatedSource.ExternalId);

        // No volumes should be assigned
        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenAutoMatchAmbiguous_SetsAmbiguous()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        // Use a title that produces similar scores for two candidates
        var manga = new Series("Berserk", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        manga.MetadataSource!.Status = MetadataSourceStatus.Unlinked;
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        // Two candidates with very close scores (both identical title, same chapter count)
        _mockSearchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MangaDexSearchResult>
            {
                new() { MangaDexId = "candidate-a", Title = "Berserk", Author = null, ChapterCount = 1 },
                new() { MangaDexId = "candidate-b", Title = "Berserk", Author = null, ChapterCount = 1 }
            });

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        var updatedSource = await _mangaContext.Set<MetadataSource>().FirstAsync(s => s.MangaId == manga.Key);
        Assert.Equal(MetadataSourceStatus.Ambiguous, updatedSource.Status);
        Assert.Null(updatedSource.ExternalId);
        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenAutoMatchSucceedsButVolumeFetchFails_RollsBackToUnlinked()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Berserk", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        manga.MetadataSource!.Status = MetadataSourceStatus.Unlinked;
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        // Strong match candidate
        _mockSearchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MangaDexSearchResult>
            {
                new() { MangaDexId = "berserk-uuid", Title = "Berserk", Author = null, ChapterCount = 1 }
            });
        // But volume fetch returns empty
        _mockSearchService
            .Setup(s => s.GetChapterToVolumeMapAsync("berserk-uuid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        var updatedSource = await _mangaContext.Set<MetadataSource>().FirstAsync(s => s.MangaId == manga.Key);
        Assert.Equal(MetadataSourceStatus.Unlinked, updatedSource.Status);
        Assert.Null(updatedSource.ExternalId);
        Assert.Null((await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1")).VolumeNumber);
    }

    private static void CreateColorCbz(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteColorImage(zip, "01.jpg");
    }

    private static void CreateGrayscaleCbz(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteGrayscaleImage(zip, "01.jpg");
    }

    private static void WriteColorImage(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
                img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
        img.SaveAsJpeg(stream);
    }

    private static void WriteGrayscaleImage(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
                img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128);
        img.SaveAsJpeg(stream);
    }
}
