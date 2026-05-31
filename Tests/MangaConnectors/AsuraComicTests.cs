using System.Net;
using System.Text;
using API;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using Moq;
using Xunit;

namespace API.Tests.MangaConnectors;

public class AsuraComicTests
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
    [InlineData("Chapter 1", null, "1")]
    [InlineData("Vol. 2 Chapter 3", 2, "3")]
    [InlineData("Season 1 Chapter 4", 1, "4")]
    public async Task GetChapters_ParsesVolumeFromText(string linkText, int? expectedVolume, string expectedChapter)
    {
        var html = $$"""
        <html>
        <body>
            <a href="/chapter/{{expectedChapter}}">
                {{linkText}}
            </a>
        </body>
        </html>
        """;

        var settings = CreateSettings();
        var asuracomic = new AsuraComic(settings, CreateRateLimitHandler())
        {
            downloadClient = CreateMockClient(html).Object
        };

        var mangaId = CreateDummyManga(asuracomic);
        var chapters = asuracomic.GetChapters(mangaId);

        Assert.Single(await chapters);
        // AsuraComic currently does not parse volume, but we want it to. 
        // This test sets up the expectation for the Red/Green/Refactor cycle.
        Assert.Equal(expectedVolume, (await chapters)[0].Item1.VolumeNumber);
        Assert.Equal(expectedChapter, (await chapters)[0].Item1.ChapterNumber);
    }
}
