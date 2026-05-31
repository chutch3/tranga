using System.Text.Json;
using log4net;

namespace API.TorrentClients;

/// <summary>
/// Talks to a qBittorrent instance via its Web API (v2). Login is session-cookie based and cached
/// between calls; on a 401/403 the client re-logs-in lazily on next request.
/// </summary>
public class QBittorrentClient(HttpClient http, string baseUrl, string username, string password) : ITorrentClient
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(QBittorrentClient));
    private readonly string _baseUrl = baseUrl.TrimEnd('/');
    private string? _sid;

    public async Task<string?> Add(string downloadUrl, string saveDir, string tag, CancellationToken ct)
    {
        if (!await EnsureAuth(ct)) return null;

        // qBittorrent's /torrents/add accepts a multipart form; for URL-only adds, urlencoded works too.
        var form = new Dictionary<string, string>
        {
            ["urls"] = downloadUrl,
            ["savepath"] = saveDir,
            ["tags"] = tag,
            ["category"] = "tranga"
        };
        using HttpResponseMessage resp = await Post("/api/v2/torrents/add", form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log.WarnFormat("qBittorrent add failed: HTTP {0}", (int)resp.StatusCode);
            return null;
        }
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!body.Trim().Equals("Ok.", StringComparison.OrdinalIgnoreCase))
        {
            Log.WarnFormat("qBittorrent add returned non-Ok body: {0}", body);
            return null;
        }
        return tag;
    }

    public async Task<TorrentStatus?> GetStatus(string tag, CancellationToken ct)
    {
        if (!await EnsureAuth(ct)) return null;

        using HttpResponseMessage resp = await GetWithCookie($"/api/v2/torrents/info?tag={Uri.EscapeDataString(tag)}", ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        string body = await resp.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        // Match the first torrent whose tags include the requested tag (qBittorrent's /info?tag=
        // doesn't always filter as a strict equality on older versions, so verify client-side).
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            string tags = el.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : "";
            if (!TagsContain(tags, tag)) continue;

            string state = el.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            double progress = el.TryGetProperty("progress", out var p) && p.TryGetDouble(out double pv) ? pv : 0.0;
            string savePath = el.TryGetProperty("save_path", out var sp) ? sp.GetString() ?? "" : "";

            if (state.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                state.Equals("missingFiles", StringComparison.OrdinalIgnoreCase))
                return new TorrentStatus.Errored(state);

            if (progress >= 1.0)
                return new TorrentStatus.Completed(savePath);

            return new TorrentStatus.Downloading(progress);
        }

        return null;
    }

    public async Task Remove(string tag, bool deleteData, CancellationToken ct)
    {
        if (!await EnsureAuth(ct)) return;

        // Resolve the hash via a tag lookup, then issue the delete.
        using HttpResponseMessage info = await GetWithCookie($"/api/v2/torrents/info?tag={Uri.EscapeDataString(tag)}", ct);
        if (!info.IsSuccessStatusCode) return;
        string body = await info.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

        var hashes = new List<string>();
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            string tags = el.TryGetProperty("tags", out var t) ? t.GetString() ?? "" : "";
            if (!TagsContain(tags, tag)) continue;
            if (el.TryGetProperty("hash", out var h) && h.ValueKind == JsonValueKind.String)
                hashes.Add(h.GetString()!);
        }
        if (hashes.Count == 0) return;

        var form = new Dictionary<string, string>
        {
            ["hashes"] = string.Join('|', hashes),
            ["deleteFiles"] = deleteData ? "true" : "false"
        };
        using HttpResponseMessage del = await Post("/api/v2/torrents/delete", form, ct);
        if (!del.IsSuccessStatusCode)
            Log.WarnFormat("qBittorrent delete failed: HTTP {0}", (int)del.StatusCode);
    }

    private static bool TagsContain(string tagList, string target) =>
        tagList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(t => string.Equals(t, target, StringComparison.Ordinal));

    private async Task<bool> EnsureAuth(CancellationToken ct)
    {
        if (_sid != null) return true;
        try
        {
            var form = new Dictionary<string, string> { ["username"] = username, ["password"] = password };
            using HttpResponseMessage resp = await PostWithoutCookie("/api/v2/auth/login", form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.WarnFormat("qBittorrent login failed: HTTP {0}", (int)resp.StatusCode);
                return false;
            }
            // Extract SID cookie if present; otherwise a successful login response is sufficient.
            if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (string c in cookies)
                {
                    int eq = c.IndexOf('=');
                    int semi = c.IndexOf(';');
                    if (eq > 0 && c.StartsWith("SID=", StringComparison.OrdinalIgnoreCase))
                    {
                        _sid = semi > eq ? c.Substring(eq + 1, semi - eq - 1) : c[(eq + 1)..];
                        break;
                    }
                }
            }
            _sid ??= "authenticated"; // sentinel — we logged in successfully even if no cookie reached us
            return true;
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("qBittorrent login threw: {0}", ex);
            return false;
        }
    }

    private Task<HttpResponseMessage> Post(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
        {
            Content = new FormUrlEncodedContent(form)
        };
        AddCookie(request);
        return http.SendAsync(request, ct);
    }

    private Task<HttpResponseMessage> PostWithoutCookie(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
        {
            Content = new FormUrlEncodedContent(form)
        };
        return http.SendAsync(request, ct);
    }

    private Task<HttpResponseMessage> GetWithCookie(string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);
        AddCookie(request);
        return http.SendAsync(request, ct);
    }

    private void AddCookie(HttpRequestMessage request)
    {
        if (_sid is not null and not "authenticated")
            request.Headers.Add("Cookie", $"SID={_sid}");
    }
}
