using System.Net;
using System.Text;
using API.Indexers;
using Xunit;

namespace API.Tests.Indexers;

public class TorznabIndexerTests
{
    // Minimal Torznab (RSS + torznab namespace) response with two items.
    private const string SampleTorznab = """
    <?xml version="1.0" encoding="UTF-8"?>
    <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
      <channel>
        <item>
          <title>Saga 060 (2024) (Digital) (Zone-Empire)</title>
          <size>52428800</size>
          <link>https://tracker.test/download/saga60.torrent</link>
          <enclosure url="https://tracker.test/download/saga60.torrent" type="application/x-bittorrent" />
          <torznab:attr name="seeders" value="47" />
          <torznab:attr name="size" value="52428800" />
        </item>
        <item>
          <title>Saga 060 (2024)</title>
          <size>51200000</size>
          <enclosure url="magnet:?xt=urn:btih:DEADBEEF&amp;dn=saga60" type="application/x-bittorrent" />
          <torznab:attr name="seeders" value="12" />
        </item>
      </channel>
    </rss>
    """;

    private static HttpClient FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new FakeHttpMessageHandler(handler));

    [Fact]
    public async Task Search_ParsesTorznabXmlIntoResults()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleTorznab, Encoding.UTF8, "application/xml")
        });
        var indexer = new TorznabIndexer(http, "MyTracker", "http://prowlarr:9696/1/api", "key", [8000]);

        var results = await indexer.Search(new IndexerQuery("Saga", "60"), CancellationToken.None);

        Assert.Equal(2, results.Length);
        Assert.Equal("Saga 060 (2024) (Digital) (Zone-Empire)", results[0].Title);
        Assert.Equal("https://tracker.test/download/saga60.torrent", results[0].DownloadUrl);
        Assert.Equal(52428800, results[0].SizeBytes);
        Assert.Equal(47, results[0].Seeders);
        Assert.Equal("MyTracker", results[0].IndexerName);
        // Second item has no <link>; falls back to the magnet enclosure.
        Assert.StartsWith("magnet:?", results[1].DownloadUrl);
        Assert.Equal(12, results[1].Seeders);
    }

    [Fact]
    public async Task Search_BuildsTorznabQueryWithApiKeyCategoriesAndTerm()
    {
        HttpRequestMessage? captured = null;
        var http = FakeHttp(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss><channel></channel></rss>", Encoding.UTF8, "application/xml")
            };
        });
        var indexer = new TorznabIndexer(http, "MyTracker", "http://prowlarr:9696/3/api", "secret", [8000, 8020]);

        await indexer.Search(new IndexerQuery("Saga", "60"), CancellationToken.None);

        Assert.NotNull(captured);
        string url = captured!.RequestUri!.ToString();
        Assert.Contains("t=search", url);
        Assert.Contains("apikey=secret", url);
        Assert.Contains("cat=8000,8020", url);
        Assert.Contains("q=Saga", url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("60", url); // issue number folded into the query term
    }

    [Fact]
    public async Task Search_ReturnsEmpty_OnNonSuccess()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var indexer = new TorznabIndexer(http, "MyTracker", "http://prowlarr:9696/1/api", "key", [8000]);

        var results = await indexer.Search(new IndexerQuery("Saga"), CancellationToken.None);

        Assert.Empty(results);
    }
}
