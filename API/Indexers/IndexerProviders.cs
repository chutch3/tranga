using System.Text.Json;
using log4net;

namespace API.Indexers;

/// <summary>A manually-configured Torznab/Newznab indexer (name + endpoint + key + categories).</summary>
public record ManualIndexerConfig(string Name, string Url, string ApiKey, int[] Categories);

/// <summary>
/// Yields the indexers a user added by hand (Torznab/Newznab endpoints stored in settings). No
/// Prowlarr involvement — this is the "you can add them" half of the *arr model.
/// </summary>
public class ConfiguredIndexerProvider(HttpClient http, IReadOnlyList<ManualIndexerConfig> indexers) : IIndexerProvider
{
    public Task<IReadOnlyList<IIndexer>> GetIndexersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IIndexer>>(
            indexers.Select(i => (IIndexer)new TorznabIndexer(http, i.Name, i.Url, i.ApiKey, i.Categories)).ToArray());
}

/// <summary>
/// The "Prowlarr can sync them" half of the *arr model. Enumerates the indexers Prowlarr manages
/// (via its <c>/api/v1/indexer</c> endpoint) and exposes each as a <see cref="TorznabIndexer"/>
/// pointing at Prowlarr's per-indexer Torznab endpoint (<c>{prowlarr}/{id}/api</c>). Tranga treats
/// these exactly like manually-added indexers — Prowlarr is just the source of the list.
/// </summary>
public class ProwlarrIndexerProvider(HttpClient http, string baseUrl, string apiKey, int[] comicCategories) : IIndexerProvider
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ProwlarrIndexerProvider));
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<IReadOnlyList<IIndexer>> GetIndexersAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v1/indexer");
            request.Headers.Add("X-Api-Key", apiKey);
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.WarnFormat("Prowlarr indexer list returned HTTP {0}", (int)response.StatusCode);
                return [];
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<IIndexer>();
            foreach (JsonElement el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out int id)) continue;
                string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? $"prowlarr-{id}" : $"prowlarr-{id}";
                // Each Prowlarr-managed indexer is reachable as a Torznab endpoint at {base}/{id}/api.
                list.Add(new TorznabIndexer(http, name, $"{_baseUrl}/{id}/api", apiKey, comicCategories));
            }
            Log.DebugFormat("Prowlarr sync discovered {0} indexers.", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Prowlarr indexer sync threw: {0}", ex);
            return [];
        }
    }
}
