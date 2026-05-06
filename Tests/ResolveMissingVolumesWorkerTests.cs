using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using API;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
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

        // Mock empty connectors list
        var connectors = Enumerable.Empty<MangaConnector>();
        
        var worker = new ResolveMissingVolumesWorker(settings, connectors);
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

        var connectors = Enumerable.Empty<MangaConnector>();
        
        var worker = new ResolveMissingVolumesWorker(settings, connectors);
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

        var worker = new ResolveMissingVolumesWorker(settings, Enumerable.Empty<MangaConnector>());
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

        var worker = new ResolveMissingVolumesWorker(settings, Enumerable.Empty<MangaConnector>());
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

        var worker = new ResolveMissingVolumesWorker(settings, Enumerable.Empty<MangaConnector>());
        await worker.DoWork(_mockScope.Object);

        var chapter1InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "1");
        var chapter2InDb = await _mangaContext.Chapters.FirstAsync(c => c.ChapterNumber == "2");

        Assert.Null(chapter1InDb.VolumeNumber);
        Assert.Null(chapter2InDb.VolumeNumber);
    }
}
