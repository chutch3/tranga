namespace API.Indexers;

/// <summary>
/// Owned abstraction over an indexer service (Prowlarr, Jackett, ...). Tranga never talks to
/// individual trackers; it asks one indexer service to enumerate releases matching a query.
/// </summary>
public interface IIndexerClient
{
    Task<IndexerSearchResult[]> Search(IndexerQuery query, CancellationToken ct);
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
