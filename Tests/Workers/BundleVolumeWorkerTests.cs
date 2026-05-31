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

public class BundleVolumeWorkerTests : IDisposable
{
    private readonly string _testRoot;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly SeriesContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private const string NamingScheme = "%M - Ch.%C";

    public BundleVolumeWorkerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"BundleVolumeTest_{Guid.NewGuid()}");
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
                s.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }); // minimal JPEG bytes
            }
        }
        return ms.ToArray();
    }

    private async Task<(Series manga, VolumeMetadata vol, Chapter[] chapters)> SetupAsync(
        int volumeNumber = 1, int chapterCount = 2, int pagesPerChapter = 5)
    {
        var library = new FileLibrary(_testRoot, "Test Library");
        _mangaContext.FileLibraries.Add(library);

        var manga = new Series("Test Series", "Desc", "http://example.com/cover.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);

        var vol = new VolumeMetadata(manga, volumeNumber);
        _mangaContext.VolumeMetadata.Add(vol);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        Directory.CreateDirectory(mangaDir);

        var chapters = new Chapter[chapterCount];
        for (int i = 0; i < chapterCount; i++)
        {
            string chapterNumber = (i + 1).ToString();
            var chapter = new Chapter(manga, chapterNumber, volumeNumber)
            {
                Downloaded = true,
                FileName = $"ch{chapterNumber}.cbz"
            };
            _mangaContext.Chapters.Add(chapter);
            chapters[i] = chapter;

            // Write real CBZ file
            byte[] cbzBytes = CreateFakeCbz(pagesPerChapter, $"p{i}");
            File.WriteAllBytes(Path.Combine(mangaDir, $"ch{chapterNumber}.cbz"), cbzBytes);
        }

        await _mangaContext.SaveChangesAsync();
        return (manga, vol, chapters);
    }

    [Fact]
    public async Task DoWork_WhenVolumeNotFound_CompletesWithoutError()
    {
        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new BundleVolumeWorker("nonexistent-manga", 1, settings);
        var ex = await Record.ExceptionAsync(() => worker.DoWork(_mockScope.Object));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DoWork_WhenNoUnbundledChapters_ReturnsEmpty()
    {
        var (manga, vol, _) = await SetupAsync();
        // Mark all chapters as already bundled
        var chapters = await _mangaContext.Chapters.ToListAsync();
        foreach (var ch in chapters)
        {
            ch.IsBundled = true;
            ch.FileName = null;
        }
        await _mangaContext.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new BundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        var result = await worker.DoWork(_mockScope.Object);
        Assert.Empty(result);

        // ArchiveFileName should still be null
        var reloaded = await _mangaContext.VolumeMetadata.FirstAsync(v => v.Key == vol.Key);
        Assert.Null(reloaded.ArchiveFileName);
    }

    [Fact]
    public async Task DoWork_CreatesOutputCbzWithCorrectPageCount()
    {
        var (manga, vol, chapters) = await SetupAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new BundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        string expectedPath = Path.Combine(mangaDir, "Vol 1.cbz");
        Assert.True(File.Exists(expectedPath), $"Bundle CBZ should exist at {expectedPath}");

        // 2 chapters × 3 pages = 6 pages total in bundle
        using var zip = ZipFile.OpenRead(expectedPath);
        int imageCount = zip.Entries.Count(e =>
            e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(6, imageCount);
    }

    [Fact]
    public async Task DoWork_CreatesBundleChapterMapRows()
    {
        var (manga, vol, chapters) = await SetupAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new BundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        var maps = await _mangaContext.BundleChapterMaps.ToListAsync();
        Assert.Equal(2, maps.Count);

        // Maps ordered by StartPage
        var ordered = maps.OrderBy(m => m.StartPage).ToList();
        Assert.Equal(0, ordered[0].StartPage);
        Assert.Equal(3, ordered[0].PageCount);
        Assert.Equal(3, ordered[1].StartPage);
        Assert.Equal(3, ordered[1].PageCount);
    }

    [Fact]
    public async Task DoWork_SetsBundledFlagAndClearsFileName()
    {
        var (manga, vol, _) = await SetupAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new BundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        var chapters = await _mangaContext.Chapters.ToListAsync();
        Assert.All(chapters, ch =>
        {
            Assert.True(ch.IsBundled);
            Assert.Null(ch.FileName);
        });
    }

    [Fact]
    public async Task DoWork_SetsVolumeArchiveFileName()
    {
        var (manga, vol, _) = await SetupAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new BundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        var reloaded = await _mangaContext.VolumeMetadata.FirstAsync(v => v.Key == vol.Key);
        Assert.Equal("Vol 1.cbz", reloaded.ArchiveFileName);
    }

    [Fact]
    public async Task DoWork_DeletesOriginalChapterFiles()
    {
        var (manga, vol, chapters) = await SetupAsync(volumeNumber: 1, chapterCount: 2, pagesPerChapter: 3);
        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new BundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        Assert.False(File.Exists(Path.Combine(mangaDir, "ch1.cbz")));
        Assert.False(File.Exists(Path.Combine(mangaDir, "ch2.cbz")));
    }

    [Fact]
    public async Task DoWork_OutputCbzContainsComicInfoXml()
    {
        var (manga, vol, _) = await SetupAsync(volumeNumber: 2, chapterCount: 1, pagesPerChapter: 2);

        var settings = new TrangaSettings { AppData = _testRoot, ChapterNamingScheme = NamingScheme };
        var worker = new BundleVolumeWorker(manga.Key, vol.VolumeNumber, settings);
        await worker.DoWork(_mockScope.Object);

        string mangaDir = Path.Combine(_testRoot, manga.DirectoryName);
        string bundlePath = Path.Combine(mangaDir, "Vol 2.cbz");
        Assert.True(File.Exists(bundlePath));

        using var zip = ZipFile.OpenRead(bundlePath);
        var comicInfo = zip.Entries.FirstOrDefault(e =>
            e.Name.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(comicInfo);

        using var reader = new StreamReader(comicInfo!.Open());
        string content = await reader.ReadToEndAsync();
        Assert.Contains("Test Series", content);
        Assert.Contains("<Volume>2</Volume>", content);
    }
}
