using System.Net;
using API;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace API.Tests.MangaConnectors;

public class MangaConnectorCoverCacheTests
{
    /// <summary>Minimal connector whose download client is backed by a faked HTTP boundary.</summary>
    private sealed class FakeConnector : MangaConnector
    {
        public FakeConnector(TrangaSettings settings, IDownloadClient client)
            : base("Fake:Conn", ["en"], ["fake.com"], "icon", settings)
        {
            downloadClient = client;
        }

        public override Task<(Series, MangaConnectorId<Series>)[]> SearchManga(string mangaSearchName) => throw new NotSupportedException();
        public override Task<(Series, MangaConnectorId<Series>)?> GetMangaFromUrl(string url) => throw new NotSupportedException();
        public override Task<(Series, MangaConnectorId<Series>)?> GetMangaFromId(string mangaIdOnSite) => throw new NotSupportedException();
        public override Task<(Chapter, MangaConnectorId<Chapter>)[]> GetChapters(MangaConnectorId<Series> mangaId, string? language = null) => throw new NotSupportedException();
        internal override Task<string[]> GetChapterImageUrls(MangaConnectorId<Chapter> chapterId) => throw new NotSupportedException();
    }

    private static byte[] CreateJpegBytes()
    {
        using var image = new Image<Rgba32>(8, 8);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task SaveCoverImageToCache_ReturnedFilename_MatchesFileWrittenToCache()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tranga-cover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settings = new TrangaSettings { AppData = tempRoot };
            byte[] jpeg = CreateJpegBytes();

            var inner = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(jpeg)
            });
            using var rateLimit = new RateLimitHandler(settings, inner);
            var downloadClient = new HttpDownloadClient(rateLimit, settings);
            var connector = new FakeConnector(settings, downloadClient);

            var library = new FileLibrary(Path.Combine(tempRoot, "lib"), "Lib");
            var manga = new Series("Cover Series", "Desc", "https://example.com/img/cover.png", MangaReleaseStatus.Continuing,
                new List<Author>(), new List<MangaTag>(), new List<Link>(), new List<AltTitle>(),
                library, 0f, 2024, "en");
            // Connector name contains a Windows-forbidden character (':') so a cleaned-vs-uncleaned
            // mismatch in the returned filename is observable.
            var mcId = new MangaConnectorId<Series>(manga, "Fake:Conn", "site-id", "https://fake.com/x", true);

            string? returned = await connector.SaveCoverImageToCache(mcId);

            Assert.NotNull(returned);
            Assert.True(File.Exists(Path.Join(settings.CoverImageCacheOriginal, returned)),
                "The returned cover filename must correspond to the file actually written to the cache.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
