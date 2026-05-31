using API;
using API.Acquirers;
using API.Indexers;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using API.TorrentClients;
using Moq;
using Xunit;

namespace API.Tests.Acquirers;

public class TorrentAcquirerTests
{
    private sealed class FakeSource : SeriesSource
    {
        public FakeSource(TrangaSettings s) : base("FakeTorrent", ["en"], ["fake.test"], "i", s) { }
        public override AcquisitionKind Kind => AcquisitionKind.Torrent;
        public override Task<(Series, SourceId<Series>)[]> SearchManga(string s) => throw new NotSupportedException();
        public override Task<(Series, SourceId<Series>)?> GetMangaFromUrl(string u) => throw new NotSupportedException();
        public override Task<(Series, SourceId<Series>)?> GetMangaFromId(string i) => throw new NotSupportedException();
        public override Task<(Chapter, SourceId<Chapter>)[]> GetChapters(SourceId<Series> m, string? l = null) => throw new NotSupportedException();
        internal override Task<string[]> GetChapterImageUrls(SourceId<Chapter> c) => throw new NotSupportedException();
    }

    private static (SourceId<Chapter> chapterId, FakeSource source, string tempRoot) BuildFixture()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tranga-tor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var settings = new TrangaSettings { AppData = tempRoot };
        var library = new FileLibrary(Path.Combine(tempRoot, "lib"), "Lib");
        var series = new Series("Saga", "d", "c", SeriesReleaseStatus.Continuing,
            new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
            library, 0f, 2024, "en");
        var chapter = new Chapter(series, "60", null, null);
        var chapterId = new SourceId<Chapter>(chapter, "FakeTorrent", "60", null, true);
        return (chapterId, new FakeSource(settings), tempRoot);
    }

    [Fact]
    public async Task AcquireAsync_HandsOffSelectedReleaseToTorrentClient_AndReturnsNull()
    {
        var (chapterId, source, tempRoot) = BuildFixture();
        try
        {
            var release = new IndexerSearchResult("Saga 060.cbz", "magnet:?xt=urn:btih:abc", 1000, 50, "ix");
            var indexer = new Mock<IIndexerClient>();
            indexer.Setup(i => i.Search(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([release]);

            string? capturedUrl = null, capturedTag = null, capturedDir = null;
            var torrent = new Mock<ITorrentClient>();
            torrent.Setup(t => t.Add(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .Callback<string, string, string, CancellationToken>((u, d, tag, _) => { capturedUrl = u; capturedDir = d; capturedTag = tag; })
                   .ReturnsAsync("tag-result");

            var selector = new ReleaseSelector { MinSeeders = 1, PreferredTokens = ["cbz"], BlockedTokens = [] };
            var acquirer = new TorrentAcquirer(indexer.Object, torrent.Object, selector,
                new TorrentAcquirerSettings(Path.Combine(tempRoot, "staging"), [8000]));

            string? result = await acquirer.AcquireAsync(chapterId, source, Path.Combine(tempRoot, "x.cbz"), CancellationToken.None);

            // Null because the file isn't ready synchronously — completion worker handles it later.
            Assert.Null(result);
            Assert.Equal("magnet:?xt=urn:btih:abc", capturedUrl);
            Assert.Equal(chapterId.Key, capturedTag);
            Assert.StartsWith(Path.Combine(tempRoot, "staging"), capturedDir!);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcquireAsync_ReturnsNull_WhenIndexerHasNoResults()
    {
        var (chapterId, source, tempRoot) = BuildFixture();
        try
        {
            var indexer = new Mock<IIndexerClient>();
            indexer.Setup(i => i.Search(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([]);
            var torrent = new Mock<ITorrentClient>();
            var acquirer = new TorrentAcquirer(indexer.Object, torrent.Object, new ReleaseSelector(),
                new TorrentAcquirerSettings(Path.Combine(tempRoot, "staging"), [8000]));

            string? result = await acquirer.AcquireAsync(chapterId, source, Path.Combine(tempRoot, "x.cbz"), CancellationToken.None);

            Assert.Null(result);
            torrent.Verify(t => t.Add(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { try { Directory.Delete(tempRoot, recursive: true); } catch { } }
    }

    [Fact]
    public async Task AcquireAsync_ReturnsNull_WhenSelectorRejectsAllReleases()
    {
        var (chapterId, source, tempRoot) = BuildFixture();
        try
        {
            var release = new IndexerSearchResult("Saga 060.cbr", "x", 1000, 1, "ix"); // .cbr blocked
            var indexer = new Mock<IIndexerClient>();
            indexer.Setup(i => i.Search(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([release]);
            var torrent = new Mock<ITorrentClient>();
            var selector = new ReleaseSelector { MinSeeders = 1, PreferredTokens = [], BlockedTokens = ["cbr"] };
            var acquirer = new TorrentAcquirer(indexer.Object, torrent.Object, selector,
                new TorrentAcquirerSettings(Path.Combine(tempRoot, "staging"), [8000]));

            string? result = await acquirer.AcquireAsync(chapterId, source, Path.Combine(tempRoot, "x.cbz"), CancellationToken.None);

            Assert.Null(result);
            torrent.Verify(t => t.Add(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { try { Directory.Delete(tempRoot, recursive: true); } catch { } }
    }
}
