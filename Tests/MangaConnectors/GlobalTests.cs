using API.MangaConnectors;
using API.Schema.SeriesContext;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.MangaConnectors;

public class GlobalTests
{
    [Fact]
    public async Task SearchManga_SortsByDownloadLanguage()
    {
        var settings = new TrangaSettings { DownloadLanguage = "en" };
        var services = new ServiceCollection();

        var mockItConnector = new Mock<SeriesSource>("Mangaworld", new[] { "it" }, new[] { "mangaworld.mx" }, "icon", settings);
        var mangaIt = new Series("Dan Da Dan IT", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], []);
        var idIt = new SourceId<Series>(mangaIt, mockItConnector.Object, "it-id", "url");
        mockItConnector.Setup(c => c.SearchManga(It.IsAny<string>())).ReturnsAsync([(mangaIt, idIt)]);
        mockItConnector.Object.Enabled = true;

        var mockEnConnector = new Mock<SeriesSource>("WeebCentral", new[] { "en" }, new[] { "weebcentral.com" }, "icon", settings);
        var mangaEn = new Series("Dan Da Dan EN", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], []);
        var idEn = new SourceId<Series>(mangaEn, mockEnConnector.Object, "en-id", "url");
        mockEnConnector.Setup(c => c.SearchManga(It.IsAny<string>())).ReturnsAsync([(mangaEn, idEn)]);
        mockEnConnector.Object.Enabled = true;

        var mockAllConnector = new Mock<SeriesSource>("Global", new[] { "all" }, new[] { "" }, "icon", settings);

        services.AddSingleton(mockItConnector.Object);
        services.AddSingleton(mockEnConnector.Object);
        services.AddSingleton(mockAllConnector.Object);

        var sp = services.BuildServiceProvider();
        var global = new Global(settings, sp);

        var results = await global.SearchManga("Dan Da Dan");

        Assert.Equal(2, results.Length);
        Assert.Equal("Dan Da Dan EN", results[0].Item1.Name);
        Assert.Equal("Dan Da Dan IT", results[1].Item1.Name);
    }
}
