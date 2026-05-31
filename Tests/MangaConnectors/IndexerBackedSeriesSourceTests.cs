using API;
using API.Acquirers;
using API.Indexers;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using Moq;
using Xunit;

namespace API.Tests.MangaConnectors;

public class IndexerBackedSeriesSourceTests
{
    private static IndexerSearchResult R(string title) => new(title, "magnet:" + title.GetHashCode(), 1000, 10, "ix");

    private static IndexerBackedSeriesSource Build(params IndexerSearchResult[] results)
    {
        var indexers = new Mock<IIndexerClient>();
        indexers.Setup(i => i.Search(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(results);
        return new IndexerBackedSeriesSource(indexers.Object, new TrangaSettings { AppData = "/tmp" });
    }

    [Fact]
    public void Kind_IsTorrent()
    {
        Assert.Equal(AcquisitionKind.Torrent, Build().Kind);
    }

    [Fact]
    public async Task SearchManga_CollapsesReleasesIntoDistinctSeries()
    {
        var source = Build(
            R("Saga 060 (2024)"),
            R("Saga 061 (2024)"),
            R("The Walking Dead 100 (2012)"));

        var results = await source.SearchManga("comic");

        Assert.Equal(2, results.Length);
        Assert.Contains(results, s => s.Item1.Name == "Saga");
        Assert.Contains(results, s => s.Item1.Name == "The Walking Dead");
        // Series carries the parsed year.
        Assert.Equal(2024u, results.First(s => s.Item1.Name == "Saga").Item1.Year);
    }

    [Fact]
    public async Task GetChapters_ParsesDistinctIssuesForTheSeries()
    {
        var source = Build(
            R("Saga 060 (2024)"),
            R("Saga 060 (2024) (Digital)"),   // duplicate issue, different release — collapses to one chapter
            R("Saga 061 (2024)"),
            R("Some Other Book 005 (2020)")); // different series — excluded

        var (series, seriesId) = (await source.SearchManga("Saga")).First(s => s.Item1.Name == "Saga");

        var chapters = await source.GetChapters(seriesId);

        var numbers = chapters.Select(c => c.Item1.ChapterNumber).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "60", "61" }, numbers);
    }

    [Fact]
    public async Task GetChapterImageUrls_Throws_BecauseTorrentSourcesHaveNoPages()
    {
        var source = Build();
        var series = new Series("Saga", "", "", SeriesReleaseStatus.Continuing, [], [], [], [], originalLanguage: "en");
        var chapter = new Chapter(series, "60", null, null);
        var chId = new SourceId<Chapter>(chapter, "Indexers", "60", null, true);

        await Assert.ThrowsAsync<NotSupportedException>(() => source.GetChapterImageUrls(chId));
    }
}
