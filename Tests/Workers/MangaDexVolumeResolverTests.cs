using System.Net;
using System.Net.Http;
using API.Schema.SeriesContext;
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
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "direct-uuid", null));

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
        var manga = new Series("My Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);

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
        var manga = new Series("Unknown Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);

        var handler = new FakeHttpMessageHandler(_ => Json("""{ "data": [] }"""));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenAggregateRequestFails_ReturnsEmpty()
    {
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "some-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Fail());

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenVolumesIsArray_ReturnsEmpty()
    {
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "some-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Json("""{ "volumes": [] }"""));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenVolumeHasNonNumericLabel_SkipsIt()
    {
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "some-uuid", null));

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
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.SourceIds.Add(new SourceId<Series>(manga, "mangadex", "lower-uuid", null));

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
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "some-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Json("""{ "volumes": {} }"""));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Empty(map);
    }

    [Fact]
    public async Task GetChapterToVolumeMap_WhenMultipleVolumes_ChaptersMapToCorrectVolumes()
    {
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "multi-uuid", null));

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

    [Fact]
    public async Task GetChapterToVolumeMap_WhenChapterHasLeadingZeroDecimal_NormalizesKeyToMatchChapterConstructor()
    {
        // Chapter constructor normalizes "0.01" → "0.1" via int.Parse.
        // The resolver must apply the same normalization so TryGetValue succeeds.
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "some-uuid", null));

        var handler = new FakeHttpMessageHandler(_ => Json("""
            {
              "volumes": {
                "1": { "volume": "1", "chapters": { "0.01": { "chapter": "0.01" } } }
              }
            }
            """));

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.True(map.ContainsKey("0.1"), "key must be normalized from '0.01' to '0.1'");
        Assert.False(map.ContainsKey("0.01"), "un-normalized key must not be present");
    }

    [Fact]
    public async Task GetChapterToVolumeMapAsync_WhenExternalIdConfirmed_UsesExternalIdDirectly()
    {
        // Series has a Confirmed MetadataSource with ExternalId — resolver must use that UUID directly
        // and never call the connector-ID walk or title search.
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        // Set ExternalId and Confirmed status on the MetadataSource
        manga.MetadataSource!.ExternalId = "confirmed-external-uuid";
        manga.MetadataSource.Status = MetadataSourceStatus.Confirmed;
        // Also add a connector ID to verify it's NOT used
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "connector-uuid", null));

        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            return Json("""
                {
                  "volumes": {
                    "1": { "volume": "1", "chapters": { "1": { "chapter": "1" }, "2": { "chapter": "2" } } }
                  }
                }
                """);
        });

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        // Must use confirmed-external-uuid, not connector-uuid
        Assert.Contains(requestedUrls, url => url.Contains("confirmed-external-uuid/aggregate"));
        Assert.DoesNotContain(requestedUrls, url => url.Contains("connector-uuid"));
        Assert.DoesNotContain(requestedUrls, url => url.Contains("?title="));
        Assert.Equal(1, map["1"]);
        Assert.Equal(1, map["2"]);
    }

    [Fact]
    public async Task GetChapterToVolumeMapAsync_WhenExternalIdAutoMatched_UsesExternalIdDirectly()
    {
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MetadataSource!.ExternalId = "auto-matched-uuid";
        manga.MetadataSource.Status = MetadataSourceStatus.AutoMatched;

        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            return Json("""
                {
                  "volumes": {
                    "2": { "volume": "2", "chapters": { "5": { "chapter": "5" } } }
                  }
                }
                """);
        });

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Contains(requestedUrls, url => url.Contains("auto-matched-uuid/aggregate"));
        Assert.DoesNotContain(requestedUrls, url => url.Contains("?title="));
        Assert.Equal(2, map["5"]);
    }

    [Fact]
    public async Task GetChapterToVolumeMapAsync_WhenExternalIdNull_FallsBackToConnectorId()
    {
        // ExternalId is null → resolver must fall through to connector-ID walk
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        // MetadataSource starts Unlinked with null ExternalId (default from constructor)
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "fallback-uuid", null));

        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            return Json("""
                {
                  "volumes": {
                    "1": { "volume": "1", "chapters": { "3": { "chapter": "3" } } }
                  }
                }
                """);
        });

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Contains(requestedUrls, url => url.Contains("fallback-uuid/aggregate"));
        Assert.DoesNotContain(requestedUrls, url => url.Contains("?title="));
        Assert.Equal(1, map["3"]);
    }

    [Fact]
    public async Task GetChapterToVolumeMapAsync_WhenExternalIdUnlinked_FallsBackToConnectorId()
    {
        // Status is Unlinked (even if ExternalId was somehow set) → fall back to connector walk
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], Library);
        manga.MetadataSource!.Status = MetadataSourceStatus.Unlinked;
        manga.MetadataSource.ExternalId = null;
        manga.SourceIds.Add(new SourceId<Series>(manga, "MangaDex", "connector-only-uuid", null));

        var requestedUrls = new List<string>();
        var handler = new FakeHttpMessageHandler(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            return Json("""
                {
                  "volumes": {
                    "1": { "volume": "1", "chapters": { "7": { "chapter": "7" } } }
                  }
                }
                """);
        });

        var resolver = new MangaDexVolumeResolver(new HttpClient(handler));
        var map = await resolver.GetChapterToVolumeMapAsync(manga);

        Assert.Contains(requestedUrls, url => url.Contains("connector-only-uuid/aggregate"));
        Assert.Equal(1, map["7"]);
    }
}
