using System.Net;
using System.Text;
using API;
using API.Indexers;
using Xunit;

namespace API.Tests.Indexers;

public class ProwlarrClientTests
{
    private const string SampleResponse = """
    [
      {
        "title": "Saga 060 (2024) (Digital) (Zone-Empire)",
        "downloadUrl": "https://tracker.test/download/saga60.torrent",
        "size": 52428800,
        "seeders": 47,
        "leechers": 3,
        "indexer": "FakeTracker"
      },
      {
        "title": "Saga 060 (2024)",
        "magnetUrl": "magnet:?xt=urn:btih:DEADBEEF&dn=saga60",
        "size": 51200000,
        "seeders": 12,
        "leechers": 0,
        "indexer": "FakeTracker2"
      }
    ]
    """;

    private static HttpClient FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new FakeHttpMessageHandler(handler));

    [Fact]
    public async Task Search_ParsesProwlarrResponseIntoResults()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleResponse, Encoding.UTF8, "application/json")
        });
        var client = new ProwlarrClient(http, baseUrl: "http://prowlarr:9696", apiKey: "k");

        var results = await client.Search(new IndexerQuery("Saga", "60"), CancellationToken.None);

        Assert.Equal(2, results.Length);
        Assert.Equal("Saga 060 (2024) (Digital) (Zone-Empire)", results[0].Title);
        Assert.Equal("https://tracker.test/download/saga60.torrent", results[0].DownloadUrl);
        Assert.Equal(52428800, results[0].SizeBytes);
        Assert.Equal(47, results[0].Seeders);
        Assert.Equal("FakeTracker", results[0].IndexerName);
        // Second result: prefers magnetUrl when downloadUrl is absent
        Assert.StartsWith("magnet:?", results[1].DownloadUrl);
    }

    [Fact]
    public async Task Search_SendsApiKeyHeaderAndCategoriesQuery()
    {
        HttpRequestMessage? captured = null;
        var http = FakeHttp(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            };
        });
        var client = new ProwlarrClient(http, baseUrl: "http://prowlarr:9696", apiKey: "secret-key");

        await client.Search(new IndexerQuery("Saga", "60", null, new[] { 8000, 8020 }), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("secret-key", captured!.Headers.GetValues("X-Api-Key").Single());
        string url = captured.RequestUri!.ToString();
        Assert.Contains("/api/v1/search", url);
        Assert.Contains("query=Saga", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("categories=8000", url);
        Assert.Contains("categories=8020", url);
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenProwlarrReturnsNonSuccess()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new ProwlarrClient(http, baseUrl: "http://prowlarr:9696", apiKey: "k");

        var results = await client.Search(new IndexerQuery("Saga"), CancellationToken.None);

        Assert.Empty(results);
    }
}
