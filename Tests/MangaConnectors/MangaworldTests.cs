using System.Net;
using System.Text;
using API;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using Moq;
using Xunit;

namespace API.Tests.MangaConnectors;

public class MangaworldTests
{
    private static TrangaSettings CreateSettings() => new TrangaSettings();
    private static RateLimitHandler CreateRateLimitHandler() => new RateLimitHandler(CreateSettings());

    private static Mock<IDownloadClient> CreateMockClient(string responseContent, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var mockClient = new Mock<IDownloadClient>();
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseContent, Encoding.UTF8, "text/html")
        };
        mockClient
            .Setup(c => c.MakeRequest(It.IsAny<string>(), It.IsAny<RequestType>(), It.IsAny<string>(), It.IsAny<CancellationToken?>()))
            .ReturnsAsync(response);
        return mockClient;
    }

    private static SourceId<Series> CreateDummyManga(SeriesSource connector)
    {
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], []);
        return new SourceId<Series>(manga, connector, "2003/test", "https://example.com/test");
    }

    [Theory]
    [InlineData("Volume 1", 1)]
    [InlineData("Vol. 3", 3)]
    [InlineData("Vol 10", 10)]
    [InlineData("Volume", 0)] // Default fallback currently is 0 if no digits found
    public async Task GetChapters_ParsesVolumeFromText(string volumeText, int expectedVolume)
    {
        var html = $$"""
        <html>
        <body>
            <div class="volume-element">
                <p class="volume-name">{{volumeText}}</p>
                <div class="chapters-wrapper">
                    <div class="chapter">
                        <a href="/manga/2003/test/read/abcdef">
                            <span>Chapter 1</span>
                        </a>
                    </div>
                </div>
            </div>
        </body>
        </html>
        """;

        var settings = CreateSettings();
        var mangaworld = new Mangaworld(settings, CreateRateLimitHandler())
        {
            downloadClient = CreateMockClient(html).Object
        };

        var mangaId = CreateDummyManga(mangaworld);
        var chapters = await mangaworld.GetChapters(mangaId);

        Assert.Single(chapters);
        Assert.Equal(expectedVolume, chapters[0].Item1.VolumeNumber);
    }
}
