namespace API.Schema.SeriesContext.MetadataFetchers;

/// <summary>
/// Owned abstraction over the Metron (metron.cloud) comic metadata API. Lets the Metron
/// MetadataFetcher be unit-tested without HTTP, and keeps the HTTP/auth specifics behind a seam.
/// </summary>
public interface IMetronClient
{
    /// <summary>Searches Metron series by name. Returns [] when unconfigured or on error.</summary>
    Task<MetronSeries[]> SearchSeries(string name, CancellationToken ct);

    /// <summary>Fetches full detail for a Metron series id. Returns null when unconfigured or not found.</summary>
    Task<MetronSeries?> GetSeries(string id, CancellationToken ct);
}

/// <summary>Subset of Metron series fields Tranga maps onto a <see cref="Series"/>.</summary>
public record MetronSeries(
    string Id,
    string Name,
    string Url,
    string? Description = null,
    int? YearBegan = null,
    string? CoverUrl = null);
