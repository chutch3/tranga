using System.Net;
using System.Net.Http;
using API.Services;
using API.Tests;

namespace Tests.Services;

public class AniListSearchServiceTests
{
    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Fail() =>
        new(HttpStatusCode.InternalServerError);

    private static AniListSearchService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new HttpClient(new FakeHttpMessageHandler(handler)));

    [Fact]
    public async Task SearchAsync_SuccessfulResponse_MapsResultsCorrectly()
    {
        var service = CreateService(_ => Json("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "id": 12345,
                      "title": { "romaji": "Berserk", "english": "Berserk" },
                      "staff": { "nodes": [{ "name": { "full": "Kentaro Miura" } }] },
                      "chapters": 364,
                      "volumes": 41
                    }
                  ]
                }
              }
            }
            """));

        var results = await service.SearchAsync("Berserk");

        Assert.Single(results);
        Assert.Equal(12345, results[0].AniListId);
        Assert.Equal("Berserk", results[0].Title);
        Assert.Equal("Kentaro Miura", results[0].Author);
        Assert.Equal(364, results[0].ChapterCount);
        Assert.Equal(41, results[0].VolumeCount);
    }

    [Fact]
    public async Task SearchAsync_PrefersEnglishTitleOverRomaji()
    {
        var service = CreateService(_ => Json("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "id": 99,
                      "title": { "romaji": "Shingeki no Kyojin", "english": "Attack on Titan" },
                      "staff": { "nodes": [] },
                      "chapters": 139,
                      "volumes": 34
                    }
                  ]
                }
              }
            }
            """));

        var results = await service.SearchAsync("Attack on Titan");

        Assert.Single(results);
        Assert.Equal("Attack on Titan", results[0].Title);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToRomajiWhenEnglishIsNull()
    {
        var service = CreateService(_ => Json("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "id": 77,
                      "title": { "romaji": "Vinland Saga", "english": null },
                      "staff": { "nodes": [] },
                      "chapters": null,
                      "volumes": null
                    }
                  ]
                }
              }
            }
            """));

        var results = await service.SearchAsync("Vinland Saga");

        Assert.Single(results);
        Assert.Equal("Vinland Saga", results[0].Title);
        Assert.Null(results[0].ChapterCount);
        Assert.Null(results[0].VolumeCount);
    }

    [Fact]
    public async Task SearchAsync_NoStaffNodes_AuthorIsNull()
    {
        var service = CreateService(_ => Json("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "id": 55,
                      "title": { "romaji": "Some Series", "english": null },
                      "staff": { "nodes": [] },
                      "chapters": 10,
                      "volumes": 2
                    }
                  ]
                }
              }
            }
            """));

        var results = await service.SearchAsync("Some Series");

        Assert.Single(results);
        Assert.Null(results[0].Author);
    }

    [Fact]
    public async Task SearchAsync_EmptyMediaArray_ReturnsEmptyList()
    {
        var service = CreateService(_ => Json("""
            {
              "data": {
                "Page": {
                  "media": []
                }
              }
            }
            """));

        var results = await service.SearchAsync("Nothing");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_HttpError_ReturnsEmptyList()
    {
        var service = CreateService(_ => Fail());

        var results = await service.SearchAsync("Anything");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_MalformedJson_ReturnsEmptyList()
    {
        var service = CreateService(_ => Json("not valid json {{{"));

        var results = await service.SearchAsync("Anything");

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_MultipleResults_MapsAll()
    {
        var service = CreateService(_ => Json("""
            {
              "data": {
                "Page": {
                  "media": [
                    {
                      "id": 1,
                      "title": { "romaji": "Alpha", "english": "Alpha" },
                      "staff": { "nodes": [{ "name": { "full": "Author A" } }] },
                      "chapters": 100,
                      "volumes": 10
                    },
                    {
                      "id": 2,
                      "title": { "romaji": "Beta", "english": null },
                      "staff": { "nodes": [{ "name": { "full": "Author B" } }] },
                      "chapters": 50,
                      "volumes": 5
                    }
                  ]
                }
              }
            }
            """));

        var results = await service.SearchAsync("Alpha");

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].AniListId);
        Assert.Equal("Alpha", results[0].Title);
        Assert.Equal("Author A", results[0].Author);
        Assert.Equal(2, results[1].AniListId);
        Assert.Equal("Beta", results[1].Title);
        Assert.Equal("Author B", results[1].Author);
    }

    [Fact]
    public async Task SearchAsync_SendsPostRequestToAniListGraphqlEndpoint()
    {
        HttpRequestMessage? captured = null;
        var service = CreateService(req =>
        {
            captured = req;
            return Json("""
                {
                  "data": {
                    "Page": {
                      "media": []
                    }
                  }
                }
                """);
        });

        await service.SearchAsync("test query");

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("https://graphql.anilist.co/", captured.RequestUri!.ToString());
    }
}
