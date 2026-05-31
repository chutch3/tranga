using log4net;

namespace API.Indexers;

/// <summary>
/// Default <see cref="IIndexerClient"/>: gathers every indexer from every configured provider
/// (manual + Prowlarr-synced), searches them concurrently, and returns the combined results
/// de-duplicated by download URL. A single failing indexer never sinks the whole search.
/// </summary>
public class AggregateIndexerSearch(IEnumerable<IIndexerProvider> providers) : IIndexerClient
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AggregateIndexerSearch));
    private readonly IIndexerProvider[] _providers = providers.ToArray();

    public async Task<IndexerSearchResult[]> Search(IndexerQuery query, CancellationToken ct)
    {
        // Resolve the full indexer set from all providers.
        var indexers = new List<IIndexer>();
        foreach (IIndexerProvider provider in _providers)
        {
            try
            {
                indexers.AddRange(await provider.GetIndexersAsync(ct));
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("Indexer provider {0} failed to list indexers: {1}", provider.GetType().Name, ex);
            }
        }

        if (indexers.Count == 0)
            return [];

        // Search every indexer concurrently. Individual indexers swallow their own errors, but guard
        // here too so a thrown task can't sink the aggregate.
        IndexerSearchResult[][] perIndexer = await Task.WhenAll(indexers.Select(async ix =>
        {
            try { return await ix.Search(query, ct); }
            catch (Exception ex)
            {
                Log.WarnFormat("Indexer '{0}' search failed: {1}", ix.Name, ex.Message);
                return [];
            }
        }));

        // De-duplicate by download URL (the same release is often surfaced by multiple indexers).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<IndexerSearchResult>();
        foreach (IndexerSearchResult r in perIndexer.SelectMany(x => x))
            if (seen.Add(r.DownloadUrl))
                deduped.Add(r);

        return deduped.ToArray();
    }
}
