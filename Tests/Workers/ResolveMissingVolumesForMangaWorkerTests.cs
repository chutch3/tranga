using System.Collections.Concurrent;
using System.IO.Compression;
using API;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
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
    private readonly MangaContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private readonly Mock<IMangaDexVolumeResolver> _mockMangaDexResolver;

    public ResolveMissingVolumesForMangaWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"ResolveForMangaTest_{Guid.NewGuid()}");
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

        _mockMangaDexResolver = new Mock<IMangaDexVolumeResolver>();
        _mockMangaDexResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
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
        new(new ConcurrentQueue<string>([mangaKey]), settings, _mockMangaDexResolver.Object);

    private ResolveMissingVolumesForMangaWorker MakeWorker(TrangaSettings settings, string mangaKey, IMangaDexVolumeResolver resolver) =>
        new(new ConcurrentQueue<string>([mangaKey]), settings, resolver);

    private ResolveMissingVolumesForMangaWorker MakeWorker(TrangaSettings settings, IEnumerable<string> mangaKeys) =>
        new(new ConcurrentQueue<string>(mangaKeys), settings, _mockMangaDexResolver.Object);

    [Fact]
    public async Task DoWork_WhenExactLookupFails_FallsBackToColorHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test No Cover", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test Exact Only", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test Missing File", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var options = new DbContextOptionsBuilder<MangaContext>().UseInMemoryDatabase(dbName).Options;

        FileLibrary library;
        Manga manga;
        using (var setupContext = new MangaContext(options))
        {
            library = new FileLibrary(_testRoot, "Test Library");
            setupContext.FileLibraries.Add(library);
            manga = new Manga("Test Continuation", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
            setupContext.Mangas.Add(manga);
            setupContext.Chapters.Add(new Chapter(manga, "1", 10, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
            setupContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
            await setupContext.SaveChangesAsync();
        }

        using var workerContext = new MangaContext(options);
        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(MangaContext))).Returns(workerContext);
        sp.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_actionsContext);
        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await new ResolveMissingVolumesForMangaWorker(
            new ConcurrentQueue<string>([manga.Key]), settings, _mockMangaDexResolver.Object)
            .DoWork(scope.Object);

        Assert.Equal(11, (await workerContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexReturnsFullMap_AllChaptersGetVolumes()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Manga("Test MangaDex Full", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        await _mangaContext.SaveChangesAsync();

        var resolver = new Mock<IMangaDexVolumeResolver>();
        resolver.Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
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
        var manga = new Manga("Test MangaDex Partial", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga, "3", null, "Title 3") { Downloaded = true, FileName = "chap3.cbz" });
        await _mangaContext.SaveChangesAsync();

        var resolver = new Mock<IMangaDexVolumeResolver>();
        resolver.Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
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
        var manga = new Manga("Test Fallback", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test Consecutive", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var options = new DbContextOptionsBuilder<MangaContext>().UseInMemoryDatabase(dbName).Options;

        FileLibrary library;
        Manga manga;
        using (var setupContext = new MangaContext(options))
        {
            library = new FileLibrary(_testRoot, "Test Library");
            setupContext.FileLibraries.Add(library);
            manga = new Manga("Test Grayscale Continuation", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
            setupContext.Mangas.Add(manga);
            setupContext.Chapters.Add(new Chapter(manga, "1", 10, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
            setupContext.Chapters.Add(new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" });
            await setupContext.SaveChangesAsync();
        }

        using var workerContext = new MangaContext(options);
        var sp = new Mock<IServiceProvider>();
        sp.Setup(x => x.GetService(typeof(MangaContext))).Returns(workerContext);
        sp.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_actionsContext);
        var scope = new Mock<IServiceScope>();
        scope.Setup(x => x.ServiceProvider).Returns(sp.Object);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateGrayscaleCbz(Path.Combine(mangaDir, "chap2.cbz"));

        await new ResolveMissingVolumesForMangaWorker(
            new ConcurrentQueue<string>([manga.Key]), settings, _mockMangaDexResolver.Object)
            .DoWork(scope.Object);

        Assert.Equal(10, (await workerContext.Chapters.FirstAsync(c => c.ChapterNumber == "2")).VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenVolumesUpdated_ReturnsRenameWorkers()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Manga("Test Moves", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));

        var result = await MakeWorker(settings, manga.Key).DoWork(_mockScope.Object);

        Assert.Contains(result, w => w is RenameChapterFileWorker);
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
        var manga = new Manga("Test No Volume Scheme", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test Empty Zip", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test Exact Empty", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test Corrupt Zip", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test Not Downloaded", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga1 = new Manga("Test Multi One", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        var manga2 = new Manga("Test Multi Two", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.AddRange(manga1, manga2);
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
        var manga = new Manga("Test Cover Name", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
        var manga = new Manga("Test Exception Fallback", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title") { Downloaded = true, FileName = "chap1.cbz" });
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);
        CreateColorCbz(Path.Combine(mangaDir, "chap1.cbz"));

        var throwingResolver = new Mock<IMangaDexVolumeResolver>();
        throwingResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
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
        var manga = new Manga("Test No Match Fallback", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);
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
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { ["1"] = 1, ["2"] = 1 });

        await MakeWorker(settings, manga.Key, resolver.Object).DoWork(_mockScope.Object);

        // Mapped=0 → resolvedExact=false → heuristic runs → color cover assigns vol 1
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "50")).VolumeNumber);
        Assert.Equal(1, (await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "51")).VolumeNumber);
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
