using System.Net;
using API;
using API.MangaDownloadClients;
using Xunit;

namespace API.Tests.MangaDownloadClients;

public class HttpDownloadClientTests
{
    private static HttpDownloadClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };
        var rateLimit = new RateLimitHandler(settings, new FakeHttpMessageHandler(handler));
        return new HttpDownloadClient(rateLimit, settings);
    }

    [Fact]
    public async Task MakeRequest_Success_ReturnsResponse()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("hello")
        });

        HttpResponseMessage response = await client.MakeRequest("http://example.test/a", RequestType.Default);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MakeRequest_NonSuccessWithoutCloudflare_ReturnsInternalServerError()
    {
        // Exercises the error-logging branch (which previously blocked on .Result). Must complete and
        // return 500 without hanging or throwing.
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found")
        });

        HttpResponseMessage response = await client.MakeRequest("http://example.test/missing", RequestType.MangaInfo);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
