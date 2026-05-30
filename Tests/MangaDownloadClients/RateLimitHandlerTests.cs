using System.Net;
using API;
using API.MangaDownloadClients;
using Xunit;

namespace API.Tests.MangaDownloadClients;

public class RateLimitHandlerTests
{
    [Fact]
    public async Task SendAsync_RateLimitsPerHost_BucketsAreIndependent()
    {
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };
        var inner = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        // One token per host, no queue: the second request to a host is immediately throttled.
        using var handler = new RateLimitHandler(settings, inner, requestsPerMinute: 1, queueLimit: 0);
        using var client = new HttpClient(handler);

        HttpResponseMessage a1 = await client.GetAsync("http://hosta.test/1");
        HttpResponseMessage a2 = await client.GetAsync("http://hosta.test/2");
        HttpResponseMessage b1 = await client.GetAsync("http://hostb.test/1");

        Assert.Equal(HttpStatusCode.OK, a1.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, a2.StatusCode);
        // host B must have its own budget and not be starved by traffic to host A.
        Assert.Equal(HttpStatusCode.OK, b1.StatusCode);
    }
}
