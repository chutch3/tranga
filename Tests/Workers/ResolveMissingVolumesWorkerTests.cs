using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using API;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SixLabors.ImageSharp;
using Xunit;
using Chapter = API.Schema.MangaContext.Chapter;
using Manga = API.Schema.MangaContext.Manga;
using MangaConnectorId = API.Schema.MangaContext.MangaConnectorId<API.Schema.MangaContext.Manga>;

namespace API.Tests.Workers;

public class ResolveMissingVolumesWorkerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly MangaContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private readonly Mock<IMangaDexVolumeResolver> _mockMangaDexResolver;

    public ResolveMissingVolumesWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"ResolveVolumesTest_{Guid.NewGuid()}");
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

    [Fact]
    public async Task DoWork_WhenExactLookupFails_FallsBackToColorHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        // Create 2 chapters with missing volumes.
        // Chapter 1
        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        
        // Chapter 2
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        _mangaContext.Chapters.Add(chapter2);

        await _mangaContext.SaveChangesAsync();

        // Create valid cbz files with actual images so the heuristic works
        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Chapter 1: Color image (Volume 1 boundary)
        string chap1Path = Path.Combine(mangaDir, "chap1.cbz");
        using (var zip1 = ZipFile.Open(chap1Path, ZipArchiveMode.Create))
        {
            var entry = zip1.CreateEntry("01_color.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            // Fill with a colorful pattern to ensure variance > 10
            for (int y = 0; y < 10; y++)
            {
                for (int x = 0; x < 10; x++)
                {
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0); // Solid red has high variance against green/blue
                }
            }
            img.SaveAsJpeg(entryStream);
        }

        // Chapter 2: Grayscale image (continuation of Volume 1)
        string chap2Path = Path.Combine(mangaDir, "chap2.cbz");
        using (var zip2 = ZipFile.Open(chap2Path, ZipArchiveMode.Create))
        {
            var entry = zip2.CreateEntry("01_bw.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
            {
                for (int x = 0; x < 10; x++)
                {
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128); // Grayscale has 0 variance
                }
            }
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapter1InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var chapter2InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");

        // If heuristic works, they should get assigned volumes.
        Assert.NotNull(chapter1InDb.VolumeNumber);
        Assert.NotNull(chapter2InDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenFirstChapterNotColor_AbortsHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Manga No Covers", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        _mangaContext.Chapters.Add(chapter2);

        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Chapter 1: Grayscale image (No cover detected on first chapter)
        using (var zip1 = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip1.CreateEntry("01_bw.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128);
            img.SaveAsJpeg(entryStream);
        }

        // Chapter 2: Color image (Simulating a false positive or random color page later)
        using (var zip2 = ZipFile.Open(Path.Combine(mangaDir, "chap2.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip2.CreateEntry("01_color.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapter1InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var chapter2InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");

        Assert.Null(chapter1InDb.VolumeNumber);
        Assert.Null(chapter2InDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenStrategyDisabled_DoesNothing()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.Disabled };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Disabled", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        await _mangaContext.SaveChangesAsync();

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapter1InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Null(chapter1InDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenStrategyExactOnly_DoesNotRunHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Exact Only", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        using (var zip1 = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip1.CreateEntry("01_color.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapter1InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Null(chapter1InDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenFileMissing_SkipsGracefullyAndEvaluatesNext()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Missing File", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        _mangaContext.Chapters.Add(chapter2);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // chap1.cbz is MISSING

        // Chapter 2: Grayscale image (first processed chapter, should abort heuristic)
        using (var zip2 = ZipFile.Open(Path.Combine(mangaDir, "chap2.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip2.CreateEntry("01_bw.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapter1InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var chapter2InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");

        Assert.Null(chapter1InDb.VolumeNumber);
        Assert.Null(chapter2InDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaHasExistingVolumes_HeuristicStartsFromMaxVolume()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        string dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<MangaContext>().UseInMemoryDatabase(dbName).Options;

        FileLibrary library;
        Manga manga;

        // Setup context: save existing data then dispose so the worker gets a fresh context
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

        // Worker context: fresh — only the null-volume chapter is loaded by the worker query,
        // so manga.Chapters navigation property won't include the vol-10 chapter unless we query the DB
        using var workerContext = new MangaContext(options);
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(MangaContext))).Returns(workerContext);
        serviceProvider.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_actionsContext);
        var mockScope = new Mock<IServiceScope>();
        mockScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        using (var zip2 = ZipFile.Open(Path.Combine(mangaDir, "chap2.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip2.CreateEntry("01_color.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(mockScope.Object);

        var missingChapterInDb = await workerContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        // Should increment from vol 10 to vol 11
        Assert.Equal(11, missingChapterInDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexReturnsFullMap_AllChaptersGetVolumes()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test MangaDex Full", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        _mangaContext.Chapters.Add(chapter2);
        await _mangaContext.SaveChangesAsync();

        var mockResolver = new Mock<IMangaDexVolumeResolver>();
        mockResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { { "1", 3 }, { "2", 3 } });

        var worker = new ResolveMissingVolumesWorker(settings, mockResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        Assert.Equal(3, ch1.VolumeNumber);
        Assert.Equal(3, ch2.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexReturnsPartialMap_UnmappedChaptersRemainNull()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test MangaDex Partial", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        var chapter3 = new Chapter(manga, "3", null, "Title 3") { Downloaded = true, FileName = "chap3.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        _mangaContext.Chapters.Add(chapter2);
        _mangaContext.Chapters.Add(chapter3);
        await _mangaContext.SaveChangesAsync();

        // Resolver only knows about chapters 1 and 2; chapter 3 is not in the map
        var mockResolver = new Mock<IMangaDexVolumeResolver>();
        mockResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { { "1", 1 }, { "2", 1 } });

        var worker = new ResolveMissingVolumesWorker(settings, mockResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        var ch3 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "3");
        Assert.Equal(1, ch1.VolumeNumber);
        Assert.Equal(1, ch2.VolumeNumber);
        Assert.Null(ch3.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMangaDexReturnsEmptyMap_FallsBackToHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test MangaDex Fallback", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        _mangaContext.Chapters.Add(chapter2);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Chapter 1: color cover, chapter 2: grayscale continuation
        foreach (var (fileName, isColor) in new[] { ("chap1.cbz", true), ("chap2.cbz", false) })
        {
            using var zip = ZipFile.Open(Path.Combine(mangaDir, fileName), ZipArchiveMode.Create);
            var entry = zip.CreateEntry("01.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            var pixel = isColor
                ? new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0)
                : new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = pixel;
            img.SaveAsJpeg(entryStream);
        }

        var mockResolver = new Mock<IMangaDexVolumeResolver>();
        mockResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var worker = new ResolveMissingVolumesWorker(settings, mockResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        // Heuristic should have run: color cover starts vol 1, grayscale is continuation
        Assert.Equal(1, ch1.VolumeNumber);
        Assert.Equal(1, ch2.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenConsecutiveColorChapters_AbortsHeuristic()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Consecutive Color", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        _mangaContext.Chapters.Add(chapter2);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        foreach (var (fileName, color) in new[] { ("chap1.cbz", true), ("chap2.cbz", true) })
        {
            using var zip = ZipFile.Open(Path.Combine(mangaDir, fileName), ZipArchiveMode.Create);
            var entry = zip.CreateEntry("01.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            var pixel = color
                ? new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0)
                : new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = pixel;
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapter1InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var chapter2InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");

        // Chapter 1 correctly starts volume 1; chapter 2 is a second consecutive color so we can't
        // determine whether the manga is full-color, has missing chapters, or has single-chapter volumes
        Assert.Equal(1, chapter1InDb.VolumeNumber);
        Assert.Null(chapter2InDb.VolumeNumber);
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
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(MangaContext))).Returns(workerContext);
        serviceProvider.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_actionsContext);
        var mockScope = new Mock<IServiceScope>();
        mockScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Missing chapter is grayscale — a continuation of volume 10, not a new volume start
        using (var zip2 = ZipFile.Open(Path.Combine(mangaDir, "chap2.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip2.CreateEntry("01_bw.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(mockScope.Object);

        var missingChapterInDb = await workerContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        // Grayscale means continuation of volume 10, not a new volume
        Assert.Equal(10, missingChapterInDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenVolumesUpdated_ReturnsMoveFileWorkers()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Moves", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        using (var zip1 = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip1.CreateEntry("01_color.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        // Should return a move worker
        Assert.Contains(newWorkers, w => w is RenameChapterFileWorker);
    }

    [Fact]
    public async Task DoWork_WhenNoChaptersMissingVolumes_ReturnsNoJobs()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test All Resolved", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        // Chapter already has a volume — should not be processed
        var chapter = new Chapter(manga, "1", 1, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        Assert.Empty(newWorkers);
    }

    [Fact]
    public async Task DoWork_WhenNamingSchemeHasNoVolume_NoMoveWorkerGenerated()
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

        using (var zip = ZipFile.Open(Path.Combine(mangaDir, chapter.FileName!), ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("01.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        var newWorkers = await worker.DoWork(_mockScope.Object);

        Assert.DoesNotContain(newWorkers, w => w is RenameChapterFileWorker);
    }

    [Fact]
    public async Task DoWork_WhenZipHasNoImages_SkipsChapterAndTreatsNextAsFirst()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Empty Zip", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        _mangaContext.Chapters.Add(chapter2);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Chapter 1: zip with no image entries — skipped by the heuristic
        using (var zip = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
            zip.CreateEntry("readme.txt");

        // Chapter 2: color cover — becomes the effective first chapter
        using (var zip = ZipFile.Open(Path.Combine(mangaDir, "chap2.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("01.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        Assert.Null(ch1.VolumeNumber);
        Assert.Equal(1, ch2.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenExactOnlyAndMangaDexReturnsEmpty_ChaptersRemainNull()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Exact Only Empty", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Color cover — heuristic would assign vol 1 if it ran, but ExactOnly prevents it
        using (var zip = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("01.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        var mockResolver = new Mock<IMangaDexVolumeResolver>();
        mockResolver
            .Setup(r => r.GetChapterToVolumeMapAsync(It.IsAny<Manga>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>());

        var worker = new ResolveMissingVolumesWorker(settings, mockResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapterInDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Null(chapterInDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenCorruptZipAfterVolumeEstablished_AssignsCurrentVolume()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Corrupt Zip", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter1 = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        var chapter2 = new Chapter(manga, "2", null, "Title 2") { Downloaded = true, FileName = "chap2.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        _mangaContext.Chapters.Add(chapter2);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Chapter 1: valid color cover — establishes volume 1
        using (var zip = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("01.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        // Chapter 2: corrupt file — exception handler should assign it to current volume (1)
        await File.WriteAllBytesAsync(Path.Combine(mangaDir, "chap2.cbz"), [0x00, 0x01, 0x02, 0x03]);

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var ch2 = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");
        Assert.Equal(1, ch1.VolumeNumber);
        Assert.Equal(1, ch2.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenChapterNotDownloaded_IsExcluded()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Not Downloaded", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter = new Chapter(manga, "1", null, "Title 1") { Downloaded = false, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapterInDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Null(chapterInDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenMultipleMangaHaveMissingVolumes_EachProcessedIndependently()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga1 = new Manga("Test Multi Manga One", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        var manga2 = new Manga("Test Multi Manga Two", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga1);
        _mangaContext.Mangas.Add(manga2);

        var chapter1 = new Chapter(manga1, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        var chapter2 = new Chapter(manga2, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter1);
        _mangaContext.Chapters.Add(chapter2);
        await _mangaContext.SaveChangesAsync();

        foreach (var manga in new[] { manga1, manga2 })
        {
            string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
            Directory.CreateDirectory(mangaDir);

            using var zip = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create);
            var entry = zip.CreateEntry("01.jpg");
            using var entryStream = entry.Open();
            using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
            for (int y = 0; y < 10; y++)
                for (int x = 0; x < 10; x++)
                    img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
            img.SaveAsJpeg(entryStream);
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var ch1 = await _mangaContext.Chapters.FirstAsync(c => c.ParentMangaId == manga1.Key);
        var ch2 = await _mangaContext.Chapters.FirstAsync(c => c.ParentMangaId == manga2.Key);
        Assert.Equal(1, ch1.VolumeNumber);
        Assert.Equal(1, ch2.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenCoverNamedCoverJpg_ColorCoverDetected()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Cover Name", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // cbz contains "cover.jpg" (color) and "001.jpg" (grayscale).
        // Alphabetically "001.jpg" sorts before "cover.jpg", so without a fix the
        // heuristic checks the grayscale interior page and aborts.
        using (var zip = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
        {
            var coverEntry = zip.CreateEntry("cover.jpg");
            using (var s = coverEntry.Open())
            {
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
                for (int y = 0; y < 10; y++)
                    for (int x = 0; x < 10; x++)
                        img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
                img.SaveAsJpeg(s);
            }

            var pageEntry = zip.CreateEntry("001.jpg");
            using (var s = pageEntry.Open())
            {
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
                for (int y = 0; y < 10; y++)
                    for (int x = 0; x < 10; x++)
                        img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128);
                img.SaveAsJpeg(s);
            }
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapterInDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Equal(1, chapterInDb.VolumeNumber);
    }

    [Fact]
    public async Task DoWork_WhenCoverNamedZeroPrefixed_ColorCoverDetected()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactThenGuess };
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Manga("Test Zero Prefix", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Mangas.Add(manga);

        var chapter = new Chapter(manga, "1", null, "Title 1") { Downloaded = true, FileName = "chap1.cbz" };
        _mangaContext.Chapters.Add(chapter);
        await _mangaContext.SaveChangesAsync();

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Standard naming: "000.jpg" (color cover) sorts before "001.jpg" (grayscale page).
        // This is the happy path that already works.
        using (var zip = ZipFile.Open(Path.Combine(mangaDir, "chap1.cbz"), ZipArchiveMode.Create))
        {
            var coverEntry = zip.CreateEntry("000.jpg");
            using (var s = coverEntry.Open())
            {
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
                for (int y = 0; y < 10; y++)
                    for (int x = 0; x < 10; x++)
                        img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(255, 0, 0);
                img.SaveAsJpeg(s);
            }

            var pageEntry = zip.CreateEntry("001.jpg");
            using (var s = pageEntry.Open())
            {
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(10, 10);
                for (int y = 0; y < 10; y++)
                    for (int x = 0; x < 10; x++)
                        img[x, y] = new SixLabors.ImageSharp.PixelFormats.Rgb24(128, 128, 128);
                img.SaveAsJpeg(s);
            }
        }

        var worker = new ResolveMissingVolumesWorker(settings, _mockMangaDexResolver.Object);
        await worker.DoWork(_mockScope.Object);

        var chapterInDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        Assert.Equal(1, chapterInDb.VolumeNumber);
    }
}
