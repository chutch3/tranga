using System.Net;
using System.Text;
using API.Indexers;
using Xunit;

namespace API.Tests.Indexers;

public class IndexerProvidersTests
{
    private static HttpClient FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new FakeHttpMessageHandler(handler));

    // ---------- ConfiguredIndexerProvider (manual) ----------

    [Fact]
    public async Task ConfiguredProvider_YieldsOneIndexerPerManualEntry()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new ConfiguredIndexerProvider(http,
        [
            new ManualIndexerConfig("Tracker A", "http://a.test/api", "ka", [8000]),
            new ManualIndexerConfig("Tracker B", "http://b.test/api", "kb", [8000, 8020])
        ]);

        var indexers = await provider.GetIndexersAsync(CancellationToken.None);

        Assert.Equal(2, indexers.Count);
        Assert.Equal(new[] { "Tracker A", "Tracker B" }, indexers.Select(i => i.Name).ToArray());
    }

    [Fact]
    public async Task ConfiguredProvider_EmptyWhenNoManualIndexers()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new ConfiguredIndexerProvider(http, []);

        Assert.Empty(await provider.GetIndexersAsync(CancellationToken.None));
    }

    // ---------- ProwlarrIndexerProvider (sync) ----------

    private const string ProwlarrIndexerList = """
    [
      { "id": 1, "name": "Nyaa",        "enable": true },
      { "id": 5, "name": "AnimeBytes",  "enable": true }
    ]
    """;

    [Fact]
    public async Task ProwlarrProvider_EnumeratesManagedIndexers_AsTorznabEndpoints()
    {
        HttpRequestMessage? captured = null;
        var http = FakeHttp(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ProwlarrIndexerList, Encoding.UTF8, "application/json")
            };
        });
        var provider = new ProwlarrIndexerProvider(http, "http://prowlarr:9696", "prowlarr-key", [8000]);

        var indexers = await provider.GetIndexersAsync(CancellationToken.None);

        Assert.Equal(2, indexers.Count);
        Assert.Equal(new[] { "Nyaa", "AnimeBytes" }, indexers.Select(i => i.Name).ToArray());
        // It discovered indexers via Prowlarr's indexer-list endpoint with the API key.
        Assert.Contains("/api/v1/indexer", captured!.RequestUri!.ToString());
        Assert.Equal("prowlarr-key", captured.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task ProwlarrProvider_ReturnsEmpty_OnNonSuccess()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var provider = new ProwlarrIndexerProvider(http, "http://prowlarr:9696", "k", [8000]);

        Assert.Empty(await provider.GetIndexersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProwlarrProvider_BuiltIndexersSearchTheirPerIndexerEndpoint()
    {
        // First call: indexer list. Subsequent calls: the per-indexer Torznab search endpoint.
        var requestedUrls = new List<string>();
        var http = FakeHttp(req =>
        {
            requestedUrls.Add(req.RequestUri!.ToString());
            string path = req.RequestUri!.AbsolutePath;
            string body = path.EndsWith("/api/v1/indexer")
                ? ProwlarrIndexerList
                : "<rss><channel></channel></rss>";
            string mime = path.EndsWith("/api/v1/indexer") ? "application/json" : "application/xml";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mime) };
        });
        var provider = new ProwlarrIndexerProvider(http, "http://prowlarr:9696", "k", [8000]);

        var indexers = await provider.GetIndexersAsync(CancellationToken.None);
        await indexers[0].Search(new IndexerQuery("Saga", "60"), CancellationToken.None);

        // The per-indexer endpoint must route through Prowlarr at /{id}/api (Torznab), id=1 for Nyaa.
        Assert.Contains(requestedUrls, u => u.Contains("/1/api") && u.Contains("t=search"));
    }
}
