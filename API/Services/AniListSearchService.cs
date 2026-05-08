using log4net;
using Newtonsoft.Json.Linq;

namespace API.Services;

/// <summary>
/// Concrete implementation of <see cref="IAniListSearchService"/> using the AniList GraphQL API.
/// </summary>
public class AniListSearchService(HttpClient httpClient) : IAniListSearchService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AniListSearchService));
    private readonly HttpClient _httpClient = httpClient;

    private const string AniListEndpoint = "https://graphql.anilist.co";

    private const string Query = """
        query ($search: String) {
          Page(page: 1, perPage: 10) {
            media(search: $search, type: MANGA, format_not_in: [NOVEL, ONE_SHOT]) {
              id
              title { romaji english }
              staff(sort: RELEVANCE, page: 1, perPage: 1) {
                nodes { name { full } }
              }
              chapters
              volumes
            }
          }
        }
        """;

    public async Task<List<AniListSearchResult>> SearchAsync(string title, CancellationToken cancellationToken = default)
    {
        var payload = new JObject
        {
            ["query"] = Query,
            ["variables"] = new JObject { ["search"] = title }
        };

        HttpResponseMessage response;
        try
        {
            var content = new StringContent(payload.ToString(), System.Text.Encoding.UTF8, "application/json");
            response = await _httpClient.PostAsync(AniListEndpoint, content, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error("AniList HTTP request failed", ex);
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            Log.WarnFormat("AniList returned HTTP {0}", response.StatusCode);
            return [];
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read AniList response body", ex);
            return [];
        }

        JObject json;
        try
        {
            json = JObject.Parse(body);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to parse AniList JSON response", ex);
            return [];
        }

        if (json["data"]?["Page"]?["media"] is not JArray media)
            return [];

        var results = new List<AniListSearchResult>();
        foreach (var item in media)
        {
            int id = item["id"]?.Value<int>() ?? 0;

            var titleObj = item["title"];
            string resolvedTitle = titleObj?["english"]?.Value<string>()
                ?? titleObj?["romaji"]?.Value<string>()
                ?? "";

            string? author = null;
            if (item["staff"]?["nodes"] is JArray nodes && nodes.Count > 0)
                author = nodes[0]["name"]?["full"]?.Value<string>();

            int? chapterCount = item["chapters"]?.Type == JTokenType.Integer
                ? item["chapters"]!.Value<int>()
                : null;

            int? volumeCount = item["volumes"]?.Type == JTokenType.Integer
                ? item["volumes"]!.Value<int>()
                : null;

            results.Add(new AniListSearchResult
            {
                AniListId = id,
                Title = resolvedTitle,
                Author = author,
                ChapterCount = chapterCount,
                VolumeCount = volumeCount
            });
        }

        return results;
    }
}
