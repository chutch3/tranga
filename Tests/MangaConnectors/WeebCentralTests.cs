using System.Net;
using System.Text;
using API;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using Moq;
using Xunit;

namespace API.Tests.MangaConnectors;

public class WeebCentralTests
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
        return new SourceId<Series>(manga, connector, "test-id", "https://example.com/test");
    }

    [Theory]
    [InlineData("Volume 1 Chapter 2", 1, "2")]
    [InlineData("Vol 3 Chapter 4", 3, "4")]
    [InlineData("Season 5 Episode 6", 5, "6")]
    [InlineData("Chapter 10", null, "10")] // No volume
    public async Task GetChapters_ParsesVolumeFromText(string linkText, int? expectedVolume, string expectedChapter)
    {
        var html = $$"""
        <html>
        <body>
            <a href="/chapters/chap-1">
                <span class="">{{linkText}}</span>
            </a>
        </body>
        </html>
        """;

        var settings = CreateSettings();
        var weebCentral = new WeebCentral(settings, CreateRateLimitHandler())
        {
            downloadClient = CreateMockClient(html).Object
        };

        var mangaId = CreateDummyManga(weebCentral);
        var chapters = await weebCentral.GetChapters(mangaId);

        Assert.Single(chapters);
        Assert.Equal(expectedVolume, chapters[0].Item1.VolumeNumber);
        Assert.Equal(expectedChapter, chapters[0].Item1.ChapterNumber);
    }
}
