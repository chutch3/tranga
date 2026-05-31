using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using API.Schema.SeriesContext;
using Newtonsoft.Json.Linq;

namespace API.Workers.MaintenanceWorkers;

public class MangaDexVolumeResolver(HttpClient httpClient) : IMangaDexVolumeResolver
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<Dictionary<string, int>> GetChapterToVolumeMapAsync(Series manga, CancellationToken cancellationToken = default)
    {
        string? mangadexUuid = null;

        // 1. Prefer a confirmed or auto-matched ExternalId from MetadataSource
        if (manga.MetadataSource?.ExternalId is { Length: > 0 } externalId &&
            manga.MetadataSource.Status is MetadataSourceStatus.Confirmed or MetadataSourceStatus.AutoMatched)
        {
            mangadexUuid = externalId;
        }
        else
        {
            // 2. Fall back to connector-ID walk
            var mdConnector = manga.SourceIds.FirstOrDefault(c => c.MangaConnectorName.Equals("MangaDex", StringComparison.OrdinalIgnoreCase));
            if (mdConnector != null)
            {
                mangadexUuid = mdConnector.IdOnConnectorSite;
            }
            else
            {
                // 3. Last resort: title search
                var searchResponse = await _httpClient.GetAsync($"https://api.mangadex.org/manga?title={Uri.EscapeDataString(manga.Name)}&limit=1", cancellationToken);
                if (searchResponse.IsSuccessStatusCode)
                {
                    var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(cancellationToken));
                    if (searchJson["data"] is JArray dataArray && dataArray.Count > 0)
                        mangadexUuid = dataArray[0]["id"]?.ToString();
                }
            }
        }

        if (string.IsNullOrEmpty(mangadexUuid))
            return [];

        var aggResponse = await _httpClient.GetAsync($"https://api.mangadex.org/manga/{mangadexUuid}/aggregate?translatedLanguage[]=en", cancellationToken);
        if (!aggResponse.IsSuccessStatusCode)
            return [];

        var aggJson = JObject.Parse(await aggResponse.Content.ReadAsStringAsync(cancellationToken));
        var volumesToken = aggJson["volumes"];

        if (volumesToken == null || volumesToken.Type == JTokenType.Array || volumesToken is not JObject volumesObj)
            return [];

        Dictionary<string, int> chapterToVolumeMap = new();

        foreach (var volProp in volumesObj.Properties())
        {
            if (volProp.Value is not JObject volEntry) continue;

            string volStr = volEntry["volume"]?.ToString() ?? "";
            if (!int.TryParse(volStr, out int volNum)) continue;

            if (volEntry["chapters"] is not JObject chaptersObj) continue;

            foreach (var chapProp in chaptersObj.Properties())
            {
                if (chapProp.Value is not JObject chapEntry) continue;
                string chapStr = chapEntry["chapter"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(chapStr))
                    chapterToVolumeMap[NormalizeChapterNumber(chapStr)] = volNum;
            }
        }

        return chapterToVolumeMap;
    }

    private static string NormalizeChapterNumber(string chapter)
    {
        var parts = chapter.Split('.');
        var normalized = parts.Select(p => int.TryParse(p, out int n) ? n.ToString() : p);
        return string.Join('.', normalized);
    }
}
