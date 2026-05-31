using System.Text.Json;
using log4net;

namespace API.Indexers;

/// <summary>
/// Talks to a Prowlarr instance's search API. Configuration (base URL + API key + comic category
/// IDs) lives in TrangaSettings; DI registration constructs the client from those values.
/// </summary>
public class ProwlarrClient(HttpClient http, string baseUrl, string apiKey) : IIndexerClient
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ProwlarrClient));
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<IndexerSearchResult[]> Search(IndexerQuery query, CancellationToken ct)
    {
        try
        {
            string url = BuildSearchUrl(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Api-Key", apiKey);

            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.WarnFormat("Prowlarr search failed: HTTP {0} for {1}", (int)response.StatusCode, url);
                return [];
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Log.Warn("Prowlarr response was not a JSON array.");
                return [];
            }

            return doc.RootElement.EnumerateArray()
                .Select(ParseRelease)
                .Where(r => r is not null)
                .Select(r => r!)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Prowlarr search threw: {0}", ex);
            return [];
        }
    }

    private string BuildSearchUrl(IndexerQuery query)
    {
        // Combine "Series Title" with optional issue number for the search term.
        string term = string.IsNullOrEmpty(query.IssueNumber)
            ? query.SeriesTitle
            : $"{query.SeriesTitle} {query.IssueNumber}";

        var parts = new List<string>
        {
            $"query={Uri.EscapeDataString(term)}",
            "type=search"
        };
        if (query.Categories is { Length: > 0 })
            parts.AddRange(query.Categories.Select(c => $"categories={c}"));

        return $"{_baseUrl}/api/v1/search?{string.Join('&', parts)}";
    }

    private static IndexerSearchResult? ParseRelease(JsonElement el)
    {
        string title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(title)) return null;

        // Prefer downloadUrl (.torrent) and fall back to magnetUrl. Either is what we'll hand to the
        // torrent client.
        string downloadUrl = "";
        if (el.TryGetProperty("downloadUrl", out var d) && d.ValueKind == JsonValueKind.String)
            downloadUrl = d.GetString() ?? "";
        if (string.IsNullOrEmpty(downloadUrl) && el.TryGetProperty("magnetUrl", out var m) && m.ValueKind == JsonValueKind.String)
            downloadUrl = m.GetString() ?? "";
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        long size = el.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
        int seeders = el.TryGetProperty("seeders", out var sd) && sd.TryGetInt32(out int sn) ? sn : 0;
        string indexer = el.TryGetProperty("indexer", out var ix) ? ix.GetString() ?? "" : "";

        return new IndexerSearchResult(title, downloadUrl, size, seeders, indexer);
    }
}
