using API.Indexers;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using API.TorrentClients;
using log4net;

namespace API.Acquirers;

/// <summary>
/// Asynchronously acquires a chapter by handing the best matching torrent release off to an
/// external torrent client. Always returns null from <see cref="AcquireAsync"/> — null here does
/// NOT mean failure; it means "not ready yet". A separate periodic worker
/// (TorrentCompletionWorker) finalises the chapter once the torrent finishes.
/// </summary>
public class TorrentAcquirer(
    IIndexerClient indexer,
    ITorrentClient torrentClient,
    ReleaseSelector selector,
    TorrentAcquirerSettings settings) : IChapterAcquirer
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TorrentAcquirer));

    public AcquisitionKind Kind => AcquisitionKind.Torrent;

    public async Task<string?> AcquireAsync(
        SourceId<Chapter> chapter,
        SeriesSource source,
        string saveArchiveFilePath,
        CancellationToken ct)
    {
        Chapter ch = chapter.Obj;
        Series series = ch.ParentManga;

        var query = new IndexerQuery(
            SeriesTitle: series.Name,
            IssueNumber: ch.ChapterNumber,
            Year: series.Year?.ToString(),
            Categories: settings.IndexerCategories);

        IndexerSearchResult[] results = await indexer.Search(query, ct);
        if (results.Length == 0)
        {
            Log.InfoFormat("No torrent releases found for {0} ch.{1}", series.Name, ch.ChapterNumber);
            return null;
        }

        IndexerSearchResult? best = selector.SelectBest(results);
        if (best is null)
        {
            Log.InfoFormat("No torrent releases passed selection criteria for {0} ch.{1} ({2} candidates)",
                series.Name, ch.ChapterNumber, results.Length);
            return null;
        }

        string stagingDir = Path.Combine(settings.StagingDirectory, chapter.Key);
        Directory.CreateDirectory(stagingDir);

        string? tag = await torrentClient.Add(best.DownloadUrl, stagingDir, chapter.Key, ct);
        if (tag is null)
        {
            Log.WarnFormat("Torrent client refused release {0} for {1} ch.{2}", best.Title, series.Name, ch.ChapterNumber);
            return null;
        }

        Log.InfoFormat("Handed off torrent '{0}' for {1} ch.{2}; TorrentCompletionWorker will finalise on completion.",
            best.Title, series.Name, ch.ChapterNumber);
        // Always null: the .cbz isn't on disk yet. The completion worker takes over.
        return null;
    }
}

/// <summary>Settings the TorrentAcquirer needs at construction time. Populated from TrangaSettings via DI.</summary>
public record TorrentAcquirerSettings(string StagingDirectory, int[] IndexerCategories);
