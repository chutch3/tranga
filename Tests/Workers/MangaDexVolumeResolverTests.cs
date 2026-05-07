using System.Net;
using System.Net.Http;
using API.Schema.MangaContext;
using API.Workers.MaintenanceWorkers;
using Xunit;

namespace API.Tests.Workers;

public class MangaDexVolumeResolverTests
{
    private static readonly FileLibrary Library = new("/tmp", "Test Library");

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpResponseMessage Fail() =>
        new(HttpStatusCode.InternalServerError);

    [Fact]
    public async Task GetChapterToVolumeMap_WhenMangaHasMangaDexConnector_UsesConnectorIdWithoutSearch()
    {
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MangaConnectorIds.Add(new MangaConnectorId<Manga>(manga, "MangaDex", "direct-uuid", null));

        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            return Json("""
                {
                  "volumes": {
                    "1": { "volume": "1", "chapters": { "1": { "chapter": "1" }, "2": { "chapter": "2" } } },
                    "2": { "volume": "2", "chapters": { "3": { "chapter": "3" } } }
                  }
                }
                """);
        });

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.DoesNotContain(requestedUrls, url => url.Contains("?title="));
        Assert.Contains(requestedUrls, url => url.Contains("direct-uuid/aggregate"));
        Assert.Equal(1, map["1"]);
        Assert.Equal(1, map["2"]);
        Assert.Equal(2, map["3"]);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenNoConnector_SearchesByNameAndUsesResultUUID()
    {
        var manga = new Manga("My Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/aggregate"))
                return Json("""
                    {
                      "volumes": {
                        "1": { "volume": "1", "chapters": { "5": { "chapter": "5" } } }
                      }
                    }
                    """);

            return Json("""{ "data": [{ "id": "search-uuid" }] }""");
        });

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Equal(1, map["5"]);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenSearchReturnsNoResults_ReturnsEmpty()
    {
        var manga = new Manga("Unknown Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);

        var handler = new FakeHttpMessageHandler(_ => Json("""{ "data": [] }"""));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenAggregateRequestFails_ReturnsEmpty()
    {
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MangaConnectorIds.Add(new MangaConnectorId<Manga>(manga, "MangaDex", "some-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Fail());

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenVolumesIsArray_ReturnsEmpty()
    {
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MangaConnectorIds.Add(new MangaConnectorId<Manga>(manga, "MangaDex", "some-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Json("""{ "volumes": [] }"""));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenVolumeHasNonNumericLabel_SkipsIt()
    {
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MangaConnectorIds.Add(new MangaConnectorId<Manga>(manga, "MangaDex", "some-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Json("""
            {
              "volumes": {
                "none": { "volume": "none", "chapters": { "0": { "chapter": "0.5" } } },
                "1":    { "volume": "1",    "chapters": { "1": { "chapter": "1"   } } }
              }
            }
            """));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.False(map.ContainsKey("0.5"));
        Assert.Equal(1, map["1"]);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenConnectorNameIsLowercase_MatchesMangaDex()
    {
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MangaConnectorIds.Add(new MangaConnectorId<Manga>(manga, "mangadex", "lower-uuid", null));

        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            return Json("""
                {
                  "volumes": {
                    "1": { "volume": "1", "chapters": { "1": { "chapter": "1" } } }
                  }
                }
                """);
        });

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        // Connector name comparison is case-insensitive; should skip the name search
        Assert.DoesNotContain(requestedUrls, url => url.Contains("?title="));
        Assert.Contains(requestedUrls, url => url.Contains("lower-uuid/aggregate"));
        Assert.Equal(1, map["1"]);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenVolumesObjectIsEmpty_ReturnsEmpty()
    {
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MangaConnectorIds.Add(new MangaConnectorId<Manga>(manga, "MangaDex", "some-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Json("""{ "volumes": {} }"""));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenMultipleVolumes_ChaptersMapToCorrectVolumes()
    {
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MangaConnectorIds.Add(new MangaConnectorId<Manga>(manga, "MangaDex", "multi-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Json("""
            {
              "volumes": {
                "1": { "volume": "1", "chapters": { "1": { "chapter": "1" }, "2": { "chapter": "2" } } },
                "2": { "volume": "2", "chapters": { "3": { "chapter": "3" }, "4": { "chapter": "4" } } },
                "3": { "volume": "3", "chapters": { "5": { "chapter": "5" } } }
              }
            }
            """));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Equal(5, map.Count);
        Assert.Equal(1, map["1"]);
        Assert.Equal(1, map["2"]);
        Assert.Equal(2, map["3"]);
        Assert.Equal(2, map["4"]);
        Assert.Equal(3, map["5"]);
    }
}
