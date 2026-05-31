using Microsoft.EntityFrameworkCore;

namespace API.Schema.SeriesContext.MetadataFetchers;

/// <summary>
/// Metadata fetcher backed by metron.cloud (comic metadata). Sits beside <see cref="MyAnimeList"/>
/// in the existing fetcher subsystem — it shows up automatically in the website's metadata-fetcher
/// table (which lists fetchers by name) and supports the same link / search / update flow. It is
/// the natural enrichment partner for indexer/torrent-sourced series, which arrive with only a name.
/// </summary>
public class Metron : MetadataFetcher
{
    private readonly IMetronClient _client;

    public Metron(IMetronClient client) : base()
    {
        _client = client;
    }

    /// <summary>EF ONLY!!! Materialised instances never call the API (only DI instances do).</summary>
    internal Metron() : base()
    {
        _client = null!;
    }

    public override async Task<MetadataSearchResult[]> SearchMetadataEntry(Series manga)
    {
        // Mirror MyAnimeList: if a Metron link is recorded, we could resolve it directly; for v1 we
        // simply search by the series name (Metron's resource URLs are slug-based, not id-based).
        return await SearchMetadataEntry(manga.Name);
    }

    public override async Task<MetadataSearchResult[]> SearchMetadataEntry(string searchTerm)
    {
        Log.DebugFormat("Searching Metron for '{0}'...", searchTerm);
        MetronSeries[] series = await _client.SearchSeries(searchTerm, CancellationToken.None);
        return series
            .Select(s => new MetadataSearchResult(s.Id, s.Name, s.Url, s.Description, s.CoverUrl))
            .ToArray();
    }

    public override async Task UpdateMetadata(MetadataEntry metadataEntry, SeriesContext dbContext, CancellationToken token)
    {
        Log.DebugFormat("Updating Metadata from Metron: {0}", metadataEntry.MangaId);

        Series? dbManga = metadataEntry.Series;
        if (dbManga is null)
        {
            if (await dbContext.Series.FirstOrDefaultAsync(m => m.Key == metadataEntry.MangaId, token) is not { } update)
            {
                Log.ErrorFormat("Series not found: {0}", metadataEntry.MangaId);
                return;
            }
            dbManga = update;
        }

        // Metron only updates scalar fields (name/description/cover), so we deliberately do NOT load
        // the owned/related collections (AltTitles, Links, Authors, Tags) — loading them is both
        // unnecessary and trips the InMemory provider's owned-without-owner restriction.

        MetronSeries? detail = await _client.GetSeries(metadataEntry.Identifier, token);
        if (detail is null)
        {
            Log.ErrorFormat("Metron series detail not found: {0}", metadataEntry.Identifier);
            return;
        }

        if (!string.IsNullOrWhiteSpace(detail.Name))
            dbManga.Name = detail.Name;
        if (!string.IsNullOrWhiteSpace(detail.Description))
            dbManga.Description = detail.Description;
        if (!string.IsNullOrWhiteSpace(detail.CoverUrl))
            dbManga.CoverUrl = detail.CoverUrl;

        if (await dbContext.Sync(token, GetType(), "Update metadata") is { success: true })
            Log.InfoFormat("Updated Metadata from Metron: {0}", metadataEntry.MangaId);
    }
}
