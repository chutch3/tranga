using System.Net;
using System.Text;
using API.Schema.SeriesContext.MetadataFetchers;
using Xunit;

namespace API.Tests.Schema.MetadataFetchers;

public class MetronClientTests
{
    private const string SeriesListJson = """
    {
      "count": 1,
      "results": [
        { "id": 2419, "series": "Saga", "year_began": 2012 }
      ]
    }
    """;

    private const string SeriesDetailJson = """
    {
      "id": 2419,
      "name": "Saga",
      "year_began": 2012,
      "desc": "Star Wars-style epic, Game of Thrones-esque...",
      "image": "https://static.metron.cloud/media/issue/2024/saga.jpg",
      "resource_url": "https://metron.cloud/series/saga-2012/"
    }
    """;

    private static HttpClient FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new FakeHttpMessageHandler(handler));

    [Fact]
    public async Task SearchSeries_ParsesResults_AndSendsBasicAuth()
    {
        HttpRequestMessage? captured = null;
        var http = FakeHttp(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SeriesListJson, Encoding.UTF8, "application/json")
            };
        });
        var client = new MetronClient(http, "user", "pass");

        var results = await client.SearchSeries("Saga", CancellationToken.None);

        var s = Assert.Single(results);
        Assert.Equal("2419", s.Id);
        Assert.Equal("Saga", s.Name);
        Assert.Equal(2012, s.YearBegan);

        Assert.NotNull(captured);
        Assert.Contains("/series", captured!.RequestUri!.ToString());
        Assert.Contains("name=Saga", captured.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        var auth = captured.Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth!.Scheme);
        Assert.Equal("user:pass", Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)));
    }

    [Fact]
    public async Task GetSeries_ParsesDetail()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SeriesDetailJson, Encoding.UTF8, "application/json")
        });
        var client = new MetronClient(http, "user", "pass");

        var s = await client.GetSeries("2419", CancellationToken.None);

        Assert.NotNull(s);
        Assert.Equal("Saga", s!.Name);
        Assert.Equal(2012, s.YearBegan);
        Assert.Contains("Star Wars", s.Description);
        Assert.Equal("https://static.metron.cloud/media/issue/2024/saga.jpg", s.CoverUrl);
        Assert.Equal("https://metron.cloud/series/saga-2012/", s.Url);
    }

    [Fact]
    public async Task SearchSeries_ReturnsEmpty_OnNonSuccess()
    {
        var http = FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new MetronClient(http, "user", "pass");

        Assert.Empty(await client.SearchSeries("Saga", CancellationToken.None));
    }

    [Fact]
    public async Task SearchSeries_ReturnsEmpty_WhenCredentialsMissing()
    {
        // No HTTP call should be made when unconfigured.
        var http = FakeHttp(_ => throw new InvalidOperationException("must not be called"));
        var client = new MetronClient(http, "", "");

        Assert.Empty(await client.SearchSeries("Saga", CancellationToken.None));
    }
}
