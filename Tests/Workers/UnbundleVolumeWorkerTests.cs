using System.IO.Compression;
using API;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Workers.MaintenanceWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

public class UnbundleVolumeWorkerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly SeriesContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private const string NamingScheme = "%M - Ch.%C";

    public UnbundleVolumeWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"UnbundleVolumeTest_{Guid.NewGuid()}");
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

    private static byte[] CreateFakeCbz(int pageCount, string prefix = "page")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 1; i <= pageCount; i++)
            {
                var entry = zip.CreateEntry($"{prefix}{i:D3}.jpg");
                using var s = entry.Open();
                s.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a bundle CBZ with pages from multiple chapters and sets up the DB state.
    /// Returns (manga, vol, chapters).
    /// </summary>
    private async Task<(Series manga, VolumeMetadata vol, Chapter[] chapters)> SetupBundledAsync(
        int volumeNumber = 1, int chapterCount = 2, int pagesPerChapter = 4)
    {
        var library = new FileLibrary(_testRoot, "Test Series Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Series("Bundle Test Series", "Desc", "http://example.com/cover.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);

        string bundleName = $"Vol {volumeNumber}.cbz";
        var vol = new VolumeMetadata(manga, volumeNumber);
        vol.ArchiveFileName = bundleName;
        _mangaContext.VolumeMetadata.Add(vol);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        // Create bundle CBZ
        using var bundleMs = new MemoryStream();
        using (var bundleZip = new ZipArchive(bundleMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            int globalIdx = 0;
            for (int c = 0; c < chapterCount; c++)
            {
                for (int p = 1; p <= pagesPerChapter; p++)
                {
                    var entry = bundleZip.CreateEntry($"{globalIdx:D5}.jpg");
                    using var s = entry.Open();
                    s.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
                    globalIdx++;
                }
            }
        }
        File.WriteAllBytes(Path.Combine(mangaDir, bundleName), bundleMs.ToArray());

        var chapters = new Chapter[chapterCount];
        for (int i = 0; i < chapterCount; i++)
        {
            var chapter = new Chapter(manga, (i + 1).ToString(), volumeNumber)
            {
                IsBundled = true,
                FileName = null
            };
            _mangaContext.Chapters.Add(chapter);
            chapters[i] = chapter;
        }
        await _mangaContext.SaveChangesAsync();

        // Create BundleChapterMap rows
        for (int i = 0; i < chapterCount; i++)
        {
            _mangaContext.BundleChapterMaps.Add(new BundleChapterMap
            {
                VolumeKey = vol.Key,
                ChapterKey = chapters[i].Key,
                StartPage = i * pagesPerChapter,
                PageCount = pagesPerChapter
            });
        }
        await _mangaContext.SaveChangesAsync();

        return (manga, vol, chapters);
    }

    [Fact]
    public async Task DoWork_WhenVolumeNotFound_CompletesWithoutError()
    {
        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new UnbundleVolumeWorker("nonexistent-manga", 1, settings);
        var ex = await Record.ExceptionAsync(() => worker.DoWork(_mockScope.Object));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DoWork_WhenNoMapRows_SkipsWork()
    {
        var library = new FileLibrary(_testRoot, "Lib");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("No Map Series", "Desc", "http://example.com/cover.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        var vol = new VolumeMetadata(manga, 1);
        vol.ArchiveFileName = "Vol 1.cbz";
        _mangaContext.VolumeMetadata.Add(vol);
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new UnbundleVolumeWorker(manga.Key, 1, settings);
        var result = await worker.DoWork(_mockScope.Object);
        Assert.Empty(result);

        // ArchiveFileName should remain unchanged
        var reloaded = await _mangaContext.VolumeMetadata.FirstAsync(v => v.Key == vol.Key);
        Assert.Equal("Vol 1.cbz", reloaded.ArchiveFileName);
    }

    [Fact]
    public async Task DoWork_ExtractsIndividualChapterCbzFiles()
    {
        var (manga, vol, chapters) = await SetupBundledAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);
        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new UnbundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        // Each chapter should have its own CBZ
        var updatedChapters = await _mangaContext.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .ToListAsync();

        Assert.All(updatedChapters, ch =>
        {
            Assert.False(ch.IsBundled);
            Assert.NotNull(ch.FileName);
            string fullPath = Path.Combine(mangaDir, ch.FileName!);
            Assert.True(File.Exists(fullPath), $"Chapter CBZ should exist at {fullPath}");
        });
    }

    [Fact]
    public async Task DoWork_ExtractedChapterCbzHasCorrectPageCount()
    {
        var (manga, vol, chapters) = await SetupBundledAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 4);
        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new UnbundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        var updatedChapters = await _mangaContext.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .ToListAsync();

        foreach (var ch in updatedChapters)
        {
            Assert.NotNull(ch.FileName);
            string cbzPath = Path.Combine(mangaDir, ch.FileName!);
            using var zip = ZipFile.OpenRead(cbzPath);
            int imageCount = zip.Entries.Count(e =>
                e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(4, imageCount);
        }
    }

    [Fact]
    public async Task DoWork_DeletesBundleChapterMapRows()
    {
        var (manga, vol, _) = await SetupBundledAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new UnbundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        var maps = await _mangaContext.BundleChapterMaps.ToListAsync();
        Assert.Empty(maps);
    }

    [Fact]
    public async Task DoWork_ClearsVolumeArchiveFileName()
    {
        var (manga, vol, _) = await SetupBundledAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new UnbundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        var reloaded = await _mangaContext.VolumeMetadata.FirstAsync(v => v.Key == vol.Key);
        Assert.Null(reloaded.ArchiveFileName);
    }

    [Fact]
    public async Task DoWork_DeletesBundleCbzAfterSuccessfulUnbundle()
    {
        var (manga, vol, _) = await SetupBundledAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);
        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        string bundlePath = Path.Combine(mangaDir, "Vol 1.cbz");
        Assert.True(File.Exists(bundlePath));

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new UnbundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        Assert.False(File.Exists(bundlePath), "Bundle CBZ should be deleted after unbundle");
    }
}
