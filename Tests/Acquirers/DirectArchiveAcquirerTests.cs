using System.Net;
using API;
using API.Acquirers;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using Xunit;

namespace API.Tests.Acquirers;

public class DirectArchiveAcquirerTests
{
    private sealed class FakeSource : SeriesSource
    {
        public FakeSource(TrangaSettings settings)
            : base("FakeArchive", ["en"], ["fake.test"], "icon", settings) { }

        public override AcquisitionKind Kind => AcquisitionKind.DirectArchive;
        public override Task<(Series, SourceId<Series>)[]> SearchManga(string s) => throw new NotSupportedException();
        public override Task<(Series, SourceId<Series>)?> GetMangaFromUrl(string u) => throw new NotSupportedException();
        public override Task<(Series, SourceId<Series>)?> GetMangaFromId(string i) => throw new NotSupportedException();
        public override Task<(Chapter, SourceId<Chapter>)[]> GetChapters(SourceId<Series> m, string? l = null) => throw new NotSupportedException();
        internal override Task<string[]> GetChapterImageUrls(SourceId<Chapter> c) => throw new NotSupportedException();
    }

    private static (Series series, Chapter chapter, SourceId<Chapter> sourceId, FakeSource source, TrangaSettings settings, string tempRoot)
        BuildFixture(string archiveUrl)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tranga-da-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var settings = new TrangaSettings { AppData = tempRoot };
        var library = new FileLibrary(Path.Combine(tempRoot, "lib"), "Lib");
        var series = new Series("S", "d", "https://x/c.png", SeriesReleaseStatus.Continuing,
            new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
            library, 0f, 2024, "en");
        var chapter = new Chapter(series, "1", null, "T");
        var sourceId = new SourceId<Chapter>(chapter, "FakeArchive", "s1", archiveUrl, true);
        var source = new FakeSource(settings);
        return (series, chapter, sourceId, source, settings, tempRoot);
    }

    [Fact]
    public async Task AcquireAsync_DownloadsArchiveFromChapterWebsiteUrl_AndReturnsPath()
    {
        const string archiveUrl = "https://fake.test/series/x/chapter1.cbz";
        byte[] archiveBytes = [0x50, 0x4B, 0x03, 0x04, 0xAA, 0xBB]; // ZIP magic + a couple bytes
        var (_, _, sourceId, source, settings, tempRoot) = BuildFixture(archiveUrl);
        try
        {
            string saveTo = Path.Combine(tempRoot, "out.cbz");
            string capturedUrl = "";
            var inner = new FakeHttpMessageHandler(req =>
            {
                capturedUrl = req.RequestUri!.ToString();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                };
            });
            using var http = new HttpClient(inner);
            var acquirer = new DirectArchiveAcquirer(http);

            string? result = await acquirer.AcquireAsync(sourceId, source, saveTo, CancellationToken.None);

            Assert.Equal(saveTo, result);
            Assert.Equal(archiveUrl, capturedUrl);
            Assert.True(File.Exists(saveTo), "Acquirer must write the archive to disk.");
            Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(saveTo));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AcquireAsync_ReturnsNull_WhenServerReturnsNonSuccess()
    {
        var (_, _, sourceId, source, settings, tempRoot) = BuildFixture("https://fake.test/missing.cbz");
        try
        {
            var inner = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            using var http = new HttpClient(inner);
            var acquirer = new DirectArchiveAcquirer(http);
            string saveTo = Path.Combine(tempRoot, "out.cbz");

            string? result = await acquirer.AcquireAsync(sourceId, source, saveTo, CancellationToken.None);

            Assert.Null(result);
            Assert.False(File.Exists(saveTo), "No file should be written on failure.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcquireAsync_ReturnsNull_WhenChapterHasNoWebsiteUrl()
    {
        var (_, _, _, source, settings, tempRoot) = BuildFixture("");
        try
        {
            // sourceId.WebsiteUrl is null/empty
            var chapter = new Chapter(new Series("S2", "d", "c", SeriesReleaseStatus.Continuing,
                new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
                new FileLibrary("/tmp", "L"), 0f, 2024, "en"), "1", null, null);
            var emptyId = new SourceId<Chapter>(chapter, "FakeArchive", "s1", null, true);

            var http = new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException("must not be called")));
            var acquirer = new DirectArchiveAcquirer(http);

            string? result = await acquirer.AcquireAsync(emptyId, source, Path.Combine(tempRoot, "x.cbz"), CancellationToken.None);

            Assert.Null(result);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
