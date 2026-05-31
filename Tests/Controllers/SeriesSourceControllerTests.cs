using API;
using API.Controllers;
using API.Controllers.DTOs;
using ConnectorDto = API.Controllers.DTOs.SeriesSource;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using MangaConnectorImpl = API.MangaConnectors.SeriesSource;

namespace API.Tests.Controllers;

public class MangaConnectorControllerTests
{
    private readonly TrangaSettings _settings = new() { AppData = Path.GetTempPath() };

    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    private Mock<MangaConnectorImpl> MakeConnector(string name, bool enabled = true, string[] languages = null!)
    {
        var mock = new Mock<MangaConnectorImpl>(name, languages ?? ["en"], new[] { "example.com" }, "icon.png", _settings);
        mock.Object.Enabled = enabled;
        return mock;
    }

    private SeriesSourceController CreateController(SeriesContext ctx, IEnumerable<MangaConnectorImpl> connectors, TrangaSettings settings)
    {
        var controller = new SeriesSourceController(ctx, connectors, settings);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public void GetConnectors_ReturnsAllConnectors()
    {
        using var ctx = CreateContext();
        var c1 = MakeConnector("MangaDex");
        var c2 = MakeConnector("Mangaworld", languages: ["it"]);

        var result = CreateController(ctx, [c1.Object, c2.Object], _settings).GetConnectors();

        var ok = Assert.IsType<Ok<List<ConnectorDto>>>(result);
        Assert.Equal(2, ok.Value!.Count);
        Assert.Contains(ok.Value, c => c.Name == "MangaDex");
        Assert.Contains(ok.Value, c => c.Name == "Mangaworld");
    }

    [Fact]
    public void GetConnectors_WhenEmpty_ReturnsEmptyList()
    {
        using var ctx = CreateContext();

        var result = CreateController(ctx, [], _settings).GetConnectors();

        var ok = Assert.IsType<Ok<List<ConnectorDto>>>(result);
        Assert.Empty(ok.Value!);
    }

    [Fact]
    public void GetConnector_KnownName_ReturnsConnector()
    {
        using var ctx = CreateContext();
        var connector = MakeConnector("MangaDex");

        var result = CreateController(ctx, [connector.Object], _settings).GetConnector("MangaDex");

        var ok = Assert.IsType<Ok<ConnectorDto>>(result.Result);
        Assert.Equal("MangaDex", ok.Value!.Name);
    }

    [Fact]
    public void GetConnector_CaseInsensitiveLookup_ReturnsConnector()
    {
        using var ctx = CreateContext();
        var connector = MakeConnector("MangaDex");

        var result = CreateController(ctx, [connector.Object], _settings).GetConnector("mangadex");

        Assert.IsType<Ok<ConnectorDto>>(result.Result);
    }

    [Fact]
    public void GetConnector_UnknownName_ReturnsNotFound()
    {
        using var ctx = CreateContext();

        var result = CreateController(ctx, [], _settings).GetConnector("UnknownSite");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public void GetEnabledConnectors_ReturnsOnlyEnabledConnectors()
    {
        using var ctx = CreateContext();
        var enabled = MakeConnector("MangaDex", enabled: true);
        var disabled = MakeConnector("Mangaworld", enabled: false);

        var result = CreateController(ctx, [enabled.Object, disabled.Object], _settings).GetEnabledConnectors(true);

        var ok = Assert.IsType<Ok<List<ConnectorDto>>>(result);
        Assert.Single(ok.Value!);
        Assert.Equal("MangaDex", ok.Value![0].Name);
    }

    [Fact]
    public void GetEnabledConnectors_ReturnsOnlyDisabledConnectors()
    {
        using var ctx = CreateContext();
        var enabled = MakeConnector("MangaDex", enabled: true);
        var disabled = MakeConnector("Mangaworld", enabled: false);

        var result = CreateController(ctx, [enabled.Object, disabled.Object], _settings).GetEnabledConnectors(false);

        var ok = Assert.IsType<Ok<List<ConnectorDto>>>(result);
        Assert.Single(ok.Value!);
        Assert.Equal("Mangaworld", ok.Value![0].Name);
    }

    [Fact]
    public void GetConnectors_ReturnsSupportedLanguages()
    {
        using var ctx = CreateContext();
        var connector = MakeConnector("Mangaworld", languages: ["it", "en"]);

        var result = CreateController(ctx, [connector.Object], _settings).GetConnectors();

        var ok = Assert.IsType<Ok<List<ConnectorDto>>>(result);
        Assert.Contains("it", ok.Value![0].SupportedLanguages);
        Assert.Contains("en", ok.Value![0].SupportedLanguages);
    }

    // --- Persistence tests (RED: these fail until TrangaSettings.DisabledConnectors is implemented) ---

    [Fact]
    public void SetEnabled_DisableConnector_PersistsToSettings()
    {
        using var ctx = CreateContext();
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };
        var connector = MakeConnector("MangaDex", enabled: true);

        CreateController(ctx, [connector.Object], settings).SetEnabled("MangaDex", false);

        Assert.Contains("MangaDex", settings.DisabledConnectors);
    }

    [Fact]
    public void SetEnabled_EnablePreviouslyDisabledConnector_RemovesFromSettings()
    {
        using var ctx = CreateContext();
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };
        settings.DisabledConnectors.Add("MangaDex");
        var connector = MakeConnector("MangaDex", enabled: false);

        CreateController(ctx, [connector.Object], settings).SetEnabled("MangaDex", true);

        Assert.DoesNotContain("MangaDex", settings.DisabledConnectors);
    }

    [Fact]
    public void SetEnabled_UnknownConnector_ReturnsNotFound()
    {
        using var ctx = CreateContext();
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };

        var result = CreateController(ctx, [], settings).SetEnabled("Unknown", false);

        Assert.IsType<NotFound<string>>(result.Result);
        Assert.Empty(settings.DisabledConnectors);
    }

    [Fact]
    public void ApplyDisabledConnectors_SetsEnabledFalseForDisabledNames()
    {
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };
        settings.DisabledConnectors.Add("MangaDex");
        var connector = MakeConnector("MangaDex", enabled: true);

        settings.ApplyDisabledConnectors([connector.Object]);

        Assert.False(connector.Object.Enabled);
    }

    [Fact]
    public void ApplyDisabledConnectors_LeavesUnlistedConnectorsEnabled()
    {
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };
        settings.DisabledConnectors.Add("Mangaworld");
        var connector = MakeConnector("MangaDex", enabled: true);

        settings.ApplyDisabledConnectors([connector.Object]);

        Assert.True(connector.Object.Enabled);
    }
}
