using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using log4net;

namespace API.Schema.SeriesContext.MetadataFetchers;

/// <summary>
/// Talks to the Metron (metron.cloud) REST API. Auth is HTTP Basic with a Metron account
/// username + password. Returns empty/null (never throws) when unconfigured or on error so the
/// fetcher degrades gracefully — it still shows in the UI, it just yields nothing without creds.
/// </summary>
public class MetronClient(HttpClient http, string username, string password) : IMetronClient
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MetronClient));
    private const string BaseUrl = "https://metron.cloud/api";

    private bool Configured => !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);

    public async Task<MetronSeries[]> SearchSeries(string name, CancellationToken ct)
    {
        if (!Configured)
        {
            Log.Warn("Metron is not configured (missing username/password); skipping search.");
            return [];
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/series/?name={Uri.EscapeDataString(name)}");
            AddAuth(request);
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.WarnFormat("Metron series search returned HTTP {0}", (int)response.StatusCode);
                return [];
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];

            return results.EnumerateArray().Select(ParseListItem).Where(s => s is not null).Select(s => s!).ToArray();
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Metron series search threw: {0}", ex);
            return [];
        }
    }

    public async Task<MetronSeries?> GetSeries(string id, CancellationToken ct)
    {
        if (!Configured)
        {
            Log.Warn("Metron is not configured (missing username/password); skipping detail fetch.");
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/series/{Uri.EscapeDataString(id)}/");
            AddAuth(request);
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.WarnFormat("Metron series detail returned HTTP {0} for id {1}", (int)response.StatusCode, id);
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            return ParseDetail(doc.RootElement);
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Metron series detail threw: {0}", ex);
            return null;
        }
    }

    private void AddAuth(HttpRequestMessage request)
    {
        string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    // List items are sparse: id + "series" (name) + year_began.
    private static MetronSeries? ParseListItem(JsonElement el)
    {
        if (!el.TryGetProperty("id", out var idEl)) return null;
        string id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "";
        if (string.IsNullOrEmpty(id)) return null;

        string name = el.TryGetProperty("series", out var n) ? n.GetString() ?? "" : "";
        int? year = el.TryGetProperty("year_began", out var y) && y.TryGetInt32(out int yv) ? yv : null;
        return new MetronSeries(id, name, $"https://metron.cloud/series/{id}/", null, year, null);
    }

    // Detail has "name", "desc", "image", "year_began", "resource_url".
    private static MetronSeries ParseDetail(JsonElement el)
    {
        string id = el.TryGetProperty("id", out var idEl)
            ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString() ?? "")
            : "";
        string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        string? desc = el.TryGetProperty("desc", out var d) ? d.GetString() : null;
        string? cover = el.TryGetProperty("image", out var img) ? img.GetString() : null;
        int? year = el.TryGetProperty("year_began", out var y) && y.TryGetInt32(out int yv) ? yv : null;
        string url = el.TryGetProperty("resource_url", out var u) ? u.GetString() ?? "" : $"https://metron.cloud/series/{id}/";
        return new MetronSeries(id, name, url, desc, year, cover);
    }
}
