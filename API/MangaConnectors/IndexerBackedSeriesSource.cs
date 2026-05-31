using API.Acquirers;
using API.Indexers;
using API.Schema.SeriesContext;

namespace API.MangaConnectors;

/// <summary>
/// A series source whose "site" is the configured set of indexers (see <see cref="IIndexerClient"/>).
/// Declares <see cref="AcquisitionKind.Torrent"/>, so chapters discovered here are downloaded via the
/// torrent path (TorrentAcquirer → torrent client → TorrentCompletionWorker) rather than scraped.
///
/// It is named for the model (indexer-backed), not for Prowlarr: the indexers may be manually added
/// or Prowlarr-synced — this source neither knows nor cares.
/// </summary>
public sealed class IndexerBackedSeriesSource : SeriesSource
{
    private readonly IIndexerClient _indexers;
    private readonly int[] _categories;

    public IndexerBackedSeriesSource(IIndexerClient indexers, TrangaSettings settings)
        : base("Indexers", ["en"], [], "", settings)
    {
        _indexers = indexers;
        _categories = settings.IndexerComicCategories;
        // No downloadClient: this source never scrapes images.
    }

    public override AcquisitionKind Kind => AcquisitionKind.Torrent;

    public override async Task<(Series, SourceId<Series>)[]> SearchManga(string mangaSearchName)
    {
        IndexerSearchResult[] results = await _indexers.Search(
            new IndexerQuery(mangaSearchName, null, null, _categories), CancellationToken.None);

        // Collapse the flat release list into distinct series by parsed title.
        var bySeries = results
            .Select(r => ReleaseTitleParser.Parse(r.Title))
            .Where(p => !string.IsNullOrWhiteSpace(p.SeriesTitle))
            .GroupBy(p => p.SeriesTitle, StringComparer.OrdinalIgnoreCase);

        var list = new List<(Series, SourceId<Series>)>();
        foreach (var group in bySeries)
        {
            list.Add(BuildSeries(group.First().SeriesTitle,
                group.Select(p => p.Year).FirstOrDefault(y => y.HasValue)));
        }
        return list.ToArray();
    }

    public override Task<(Series, SourceId<Series>)?> GetMangaFromUrl(string url)
        => Task.FromResult<(Series, SourceId<Series>)?>(null);

    public override Task<(Series, SourceId<Series>)?> GetMangaFromId(string mangaIdOnSite)
        => Task.FromResult<(Series, SourceId<Series>)?>(BuildSeries(mangaIdOnSite, null));

    public override async Task<(Chapter, SourceId<Chapter>)[]> GetChapters(SourceId<Series> mangaId, string? language = null)
    {
        string seriesTitle = mangaId.Obj.Name;
        IndexerSearchResult[] results = await _indexers.Search(
            new IndexerQuery(seriesTitle, null, null, _categories), CancellationToken.None);

        var byIssue = new Dictionary<string, (Chapter, SourceId<Chapter>)>();
        foreach (IndexerSearchResult r in results)
        {
            ParsedRelease p = ReleaseTitleParser.Parse(r.Title);
            if (p.IssueNumber is null) continue;
            // Only keep releases that parse to this series (avoid cross-series bleed in the result set).
            if (!string.Equals(p.SeriesTitle, seriesTitle, StringComparison.OrdinalIgnoreCase)) continue;
            if (byIssue.ContainsKey(p.IssueNumber)) continue;

            var chapter = new Chapter(mangaId.Obj, p.IssueNumber, null, null);
            var chId = new SourceId<Chapter>(chapter, this, p.IssueNumber, null, mangaId.UseForDownload);
            chapter.SourceIds.Add(chId);
            byIssue[p.IssueNumber] = (chapter, chId);
        }
        return byIssue.Values.ToArray();
    }

    // Torrent sources never expose page images; both image-path hooks are unreachable for Kind=Torrent.
    internal override Task<string[]> GetChapterImageUrls(SourceId<Chapter> chapterId)
        => throw new NotSupportedException("Indexer-backed (torrent) sources do not expose page images.");

    public override Task<Stream?> DownloadImage(string imageUrl, CancellationToken ct)
        => throw new NotSupportedException("Indexer-backed (torrent) sources do not download images.");

    private (Series, SourceId<Series>) BuildSeries(string title, int? year)
    {
        var series = new Series(
            title, "", "", SeriesReleaseStatus.Continuing,
            [], [], [], [],
            year: year.HasValue ? (uint)year.Value : null,
            originalLanguage: "en");
        var id = new SourceId<Series>(series, this, title, null);
        series.SourceIds.Add(id);
        return (series, id);
    }
}
