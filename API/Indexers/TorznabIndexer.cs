using System.Xml.Linq;
using log4net;

namespace API.Indexers;

/// <summary>
/// A single Torznab/Newznab indexer. Works for any Torznab endpoint — a manually-added tracker,
/// a Jackett feed, or one of the per-indexer endpoints Prowlarr exposes at
/// <c>{prowlarr}/{indexerId}/api</c>. Knows nothing about Prowlarr; that's the whole point.
/// </summary>
public class TorznabIndexer(HttpClient http, string name, string baseUrl, string apiKey, int[] categories) : IIndexer
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(TorznabIndexer));
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";

    public string Name { get; } = name;

    public async Task<IndexerSearchResult[]> Search(IndexerQuery query, CancellationToken ct)
    {
        try
        {
            string url = BuildUrl(query);
            using HttpResponseMessage response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.WarnFormat("Torznab indexer '{0}' returned HTTP {1}", Name, (int)response.StatusCode);
                return [];
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            XDocument doc = XDocument.Parse(body);

            return doc.Descendants("item")
                .Select(ParseItem)
                .Where(r => r is not null)
                .Select(r => r!)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Torznab indexer '{0}' search threw: {1}", Name, ex);
            return [];
        }
    }

    private string BuildUrl(IndexerQuery query)
    {
        string term = string.IsNullOrEmpty(query.IssueNumber)
            ? query.SeriesTitle
            : $"{query.SeriesTitle} {query.IssueNumber}";

        // Query categories override the indexer's configured defaults when provided.
        int[] cats = query.Categories is { Length: > 0 } ? query.Categories : categories;

        var parts = new List<string>
        {
            "t=search",
            $"apikey={Uri.EscapeDataString(apiKey)}",
            $"q={Uri.EscapeDataString(term)}"
        };
        if (cats.Length > 0)
            parts.Add($"cat={string.Join(',', cats)}");

        string sep = baseUrl.Contains('?') ? "&" : "?";
        return $"{baseUrl}{sep}{string.Join('&', parts)}";
    }

    private IndexerSearchResult? ParseItem(XElement item)
    {
        string title = (string?)item.Element("title") ?? "";
        if (string.IsNullOrEmpty(title)) return null;

        // Prefer an explicit <link> when it points at a torrent/magnet; otherwise the enclosure url.
        string? link = (string?)item.Element("link");
        string? enclosure = item.Element("enclosure")?.Attribute("url")?.Value;
        string downloadUrl = PickDownloadUrl(link, enclosure);
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        long size = ParseLong((string?)item.Element("size"))
                    ?? ParseLong(TorznabAttr(item, "size"))
                    ?? 0;
        int seeders = (int)(ParseLong(TorznabAttr(item, "seeders")) ?? 0);

        return new IndexerSearchResult(title, downloadUrl, size, seeders, Name);
    }

    private static string PickDownloadUrl(string? link, string? enclosure)
    {
        bool LinkIsDownloadable(string? s) =>
            !string.IsNullOrEmpty(s) &&
            (s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
             s.Contains(".torrent", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("/download", StringComparison.OrdinalIgnoreCase));

        if (LinkIsDownloadable(link)) return link!;
        if (!string.IsNullOrEmpty(enclosure)) return enclosure!;
        return link ?? "";
    }

    private static string? TorznabAttr(XElement item, string name) =>
        item.Elements(Torznab + "attr")
            .FirstOrDefault(a => (string?)a.Attribute("name") == name)
            ?.Attribute("value")?.Value;

    private static long? ParseLong(string? s) => long.TryParse(s, out long v) ? v : null;
}
