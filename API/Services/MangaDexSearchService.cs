using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace API.Services;

/// <summary>
/// Concrete implementation of <see cref="IMangaDexSearchService"/> using the MangaDex API.
/// </summary>
public class MangaDexSearchService(HttpClient httpClient) : IMangaDexSearchService
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<List<MangaDexSearchResult>> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.mangadex.org/manga?title={Uri.EscapeDataString(title)}&limit=10&includes[]=author";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        var json = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (json["data"] is not JArray data)
            return [];

        var results = new List<MangaDexSearchResult>();
        foreach (var item in data)
        {
            string id = item["id"]?.ToString() ?? "";
            var attrs = item["attributes"];
            if (attrs is null) continue;

            // Pick the first English title or the first available title
            string resolvedTitle = "";
            if (attrs["title"] is JObject titleObj)
            {
                resolvedTitle = titleObj["en"]?.ToString()
                    ?? titleObj.Properties().FirstOrDefault()?.Value.ToString()
                    ?? "";
            }

            // Get author from relationships
            string? author = null;
            if (item["relationships"] is JArray rels)
            {
                var authorRel = rels.FirstOrDefault(r => r["type"]?.ToString() == "author");
                author = authorRel?["attributes"]?["name"]?.ToString();
            }

            // Get chapter count from lastChapter attribute (approximate)
            int chapterCount = 0;
            if (attrs["lastChapter"]?.ToString() is { } lastCh && int.TryParse(lastCh, out int lc))
                chapterCount = lc;

            results.Add(new MangaDexSearchResult
            {
                MangaDexId = id,
                Title = resolvedTitle,
                Author = author,
                ChapterCount = chapterCount
            });
        }

        return results;
    }

    public async Task<Dictionary<string, int>> GetChapterToVolumeMapAsync(string mangaDexId, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.mangadex.org/manga/{Uri.EscapeDataString(mangaDexId)}/aggregate?translatedLanguage[]=en";
        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        var json = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var volumesToken = json["volumes"];

        if (volumesToken == null || volumesToken.Type == JTokenType.Array || volumesToken is not JObject volumesObj)
            return [];

        var map = new Dictionary<string, int>();
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
                    map[NormalizeChapterNumber(chapStr)] = volNum;
            }
        }

        return map;
    }

    private static string NormalizeChapterNumber(string chapter)
    {
        var parts = chapter.Split('.');
        var normalized = parts.Select(p => int.TryParse(p, out int n) ? n.ToString() : p);
        return string.Join('.', normalized);
    }
}
