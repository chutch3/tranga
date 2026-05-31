namespace API.Indexers;

/// <summary>
/// The aggregate search surface the rest of the app depends on. Fans a query out across every
/// configured <see cref="IIndexer"/> (manually added or synced from Prowlarr) and returns the
/// combined, de-duplicated set of releases. This is deliberately NOT coupled to Prowlarr — Prowlarr
/// is just one provider of indexers.
/// </summary>
public interface IIndexerClient
{
    Task<IndexerSearchResult[]> Search(IndexerQuery query, CancellationToken ct);
}

/// <summary>
/// A single indexer — concretely a Torznab/Newznab endpoint. The same shape whether the endpoint
/// was added by hand or pushed in by Prowlarr's app-sync. Indexers know nothing about each other.
/// </summary>
public interface IIndexer
{
    /// <summary>Human-readable indexer name (for logging / attribution on results).</summary>
    string Name { get; }

    Task<IndexerSearchResult[]> Search(IndexerQuery query, CancellationToken ct);
}

/// <summary>
/// Supplies the set of configured indexers. Implementations include a manual/settings-based source
/// and a Prowlarr-sync source that enumerates the indexers Prowlarr manages.
/// </summary>
public interface IIndexerProvider
{
    Task<IReadOnlyList<IIndexer>> GetIndexersAsync(CancellationToken ct);
}

public record IndexerQuery(
    string SeriesTitle,
    string? IssueNumber = null,
    string? Year = null,
    int[]? Categories = null);

public record IndexerSearchResult(
    string Title,
    string DownloadUrl,
    long SizeBytes,
    int Seeders,
    string IndexerName);
