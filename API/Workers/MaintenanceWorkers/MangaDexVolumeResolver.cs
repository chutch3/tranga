using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using API.Schema.MangaContext;
using Newtonsoft.Json.Linq;

namespace API.Workers.MaintenanceWorkers;

public class MangaDexVolumeResolver(HttpClient httpClient) : IMangaDexVolumeResolver
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<Dictionary<string, int>> GetChapterToVolumeMapAsync(Manga manga, CancellationToken cancellationToken = default)
    {
        string? mangadexUuid = null;

        var mdConnector = manga.MangaConnectorIds.FirstOrDefault(c => c.MangaConnectorName.Equals("MangaDex", StringComparison.OrdinalIgnoreCase));
        if (mdConnector != null)
        {
            mangadexUuid = mdConnector.IdOnConnectorSite;
        }
        else
        {
            var searchResponse = await _httpClient.GetAsync($"https://api.mangadex.org/manga?title={Uri.EscapeDataString(manga.Name)}&limit=1", cancellationToken);
            if (searchResponse.IsSuccessStatusCode)
            {
                var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(cancellationToken));
                if (searchJson["data"] is JArray dataArray && dataArray.Count > 0)
                    mangadexUuid = dataArray[0]["id"]?.ToString();
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
                    chapterToVolumeMap[chapStr] = volNum;
            }
        }

        return chapterToVolumeMap;
    }
}
