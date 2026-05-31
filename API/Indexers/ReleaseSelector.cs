namespace API.Indexers;

/// <summary>
/// Picks the best release from indexer search results according to a simple scoring rule:
/// honour seeder floor, exclude blocked tokens, prefer preferred tokens, then highest seeders wins.
/// Per user choice for v1; profilarr-compatible custom formats are a future iteration.
/// </summary>
public class ReleaseSelector
{
    public int MinSeeders { get; init; } = 2;
    public string[] PreferredTokens { get; init; } = ["cbz"];
    public string[] BlockedTokens { get; init; } = ["cbr", "pdf"];

    public IndexerSearchResult? SelectBest(IReadOnlyCollection<IndexerSearchResult> results)
    {
        if (results.Count == 0) return null;

        return results
            .Where(r => r.Seeders >= MinSeeders)
            .Where(r => !ContainsAny(r.Title, BlockedTokens))
            .OrderByDescending(r => ContainsAny(r.Title, PreferredTokens) ? 1 : 0)
            .ThenByDescending(r => r.Seeders)
            .FirstOrDefault();
    }

    private static bool ContainsAny(string text, IEnumerable<string> tokens) =>
        tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
}
