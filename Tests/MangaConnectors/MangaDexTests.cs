using System.Net;
using System.Text;
using API;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using Moq;
using Xunit;

namespace API.Tests.MangaConnectors;

public class MangaDexTests
{
    private static TrangaSettings CreateSettings() => new TrangaSettings();
    private static RateLimitHandler CreateRateLimitHandler() => new RateLimitHandler(CreateSettings());

    private static Mock<IDownloadClient> CreateMockClient(string responseContent, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var mockClient = new Mock<IDownloadClient>();
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
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
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("10", 10)]
    public async Task GetChapters_ParsesNumericVolumeCorrectly(string volumeStr, int expectedVolume)
    {
        var json = $$"""
        {
            "result": "ok",
            "data": [
                {
                    "id": "chap-1",
                    "attributes": {
                        "chapter": "1",
                        "volume": "{{volumeStr}}",
                        "title": "Test Chapter"
                    }
                }
            ]
        }
        """;

        var settings = CreateSettings();
        var mangaDex = new MangaDex(settings, CreateRateLimitHandler())
        {
            downloadClient = CreateMockClient(json).Object
        };

        var mangaId = CreateDummyManga(mangaDex);
        var chapters = await mangaDex.GetChapters(mangaId);

        Assert.Single(chapters);
        Assert.Equal(expectedVolume, chapters[0].Item1.VolumeNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("null")]
    public async Task GetChapters_HandlesNonNumericVolumeGracefully(string volumeStr)
    {
        var json = $$"""
        {
            "result": "ok",
            "data": [
                {
                    "id": "chap-1",
                    "attributes": {
                        "chapter": "1",
                        "volume": "{{volumeStr}}",
                        "title": "Test Chapter"
                    }
                }
            ]
        }
        """;

        var settings = CreateSettings();
        var mangaDex = new MangaDex(settings, CreateRateLimitHandler())
        {
            downloadClient = CreateMockClient(json).Object
        };

        var mangaId = CreateDummyManga(mangaDex);
        
        var chapters = await mangaDex.GetChapters(mangaId);

        Assert.Single(chapters);
        Assert.Null(chapters[0].Item1.VolumeNumber);
    }

    [Fact]
    public async Task SearchManga_IncludesDownloadLanguageInQuery()
    {
        var json = "{\"result\":\"ok\",\"data\":[]}";
        var settings = CreateSettings();
        settings.DownloadLanguage = "fr";

        var mockClient = new Mock<IDownloadClient>();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        string capturedUrl = "";
        mockClient
            .Setup(c => c.MakeRequest(It.IsAny<string>(), It.IsAny<RequestType>(), It.IsAny<string>(), It.IsAny<CancellationToken?>()))
            .ReturnsAsync(response)
            .Callback<string, RequestType, string, CancellationToken?>((url, _, _, _) => capturedUrl = url);

        var mangaDex = new MangaDex(settings, CreateRateLimitHandler())
        {
            downloadClient = mockClient.Object
        };

        await mangaDex.SearchManga("Test");

        Assert.Contains("availableTranslatedLanguage%5B%5D=fr", capturedUrl);
    }

    private static string OneMangaPage(int total) => $$"""
        {
            "result": "ok",
            "total": {{total}},
            "data": [
                {
                    "id": "manga-1",
                    "attributes": {
                        "title": { "en": "First Page Series" },
                        "description": { "en": "desc" },
                        "status": "ongoing"
                    },
                    "relationships": [
                        { "type": "cover_art", "attributes": { "fileName": "cover.jpg" } }
                    ]
                }
            ]
        }
        """;

    private static Mock<IDownloadClient> SequencedClient(params HttpResponseMessage[] responses)
    {
        var mock = new Mock<IDownloadClient>();
        var seq = mock.SetupSequence(c => c.MakeRequest(It.IsAny<string>(), It.IsAny<RequestType>(), It.IsAny<string>(), It.IsAny<CancellationToken?>()));
        foreach (var r in responses)
            seq = seq.ReturnsAsync(r);
        return mock;
    }

    [Fact]
    public async Task SearchManga_KeepsResultsGathered_WhenLaterPageFails()
    {
        // total > Limit forces a second page; the second request fails. Results from page 1 must survive.
        var page1 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(OneMangaPage(total: 150), Encoding.UTF8, "application/json")
        };
        var page2Fail = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };

        var settings = CreateSettings();
        var mangaDex = new MangaDex(settings, CreateRateLimitHandler())
        {
            downloadClient = SequencedClient(page1, page2Fail).Object
        };

        var results = await mangaDex.SearchManga("Test");

        Assert.Single(results);
        Assert.Equal("First Page Series", results[0].Item1.Name);
    }

    [Fact]
    public async Task SearchManga_DoesNotRequestBeyondOffsetCap()
    {
        // MangaDex hard-caps offset at 10000. Even if total claims far more, we must stop requesting
        // (Limit=100 => at most 100 pages) instead of looping into guaranteed errors.
        var settings = CreateSettings();
        int requestCount = 0;
        var mock = new Mock<IDownloadClient>();
        mock.Setup(c => c.MakeRequest(It.IsAny<string>(), It.IsAny<RequestType>(), It.IsAny<string>(), It.IsAny<CancellationToken?>()))
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":\"ok\",\"total\":1000000,\"data\":[]}", Encoding.UTF8, "application/json")
            })
            .Callback(() => requestCount++);

        var mangaDex = new MangaDex(settings, CreateRateLimitHandler())
        {
            downloadClient = mock.Object
        };

        await mangaDex.SearchManga("Test");

        Assert.True(requestCount <= 100, $"Expected at most 100 page requests (offset cap), but made {requestCount}.");
    }

    [Fact]
    public async Task SearchManga_Contract_IsAsync()
    {
        // This test defines our new async contract.
        // It will fail to compile initially until we refactor the base class and implementation.
        var json = "{\"result\":\"ok\",\"data\":[],\"total\":0}";
        var settings = CreateSettings();
        var mockClient = CreateMockClient(json);
        var mangaDex = new MangaDex(settings, CreateRateLimitHandler())
        {
            downloadClient = mockClient.Object
        };

        // We EXPECT to await this now
        var results = await mangaDex.SearchManga("One Piece");

        Assert.NotNull(results);
    }
}
