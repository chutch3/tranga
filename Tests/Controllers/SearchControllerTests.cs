using System.Reflection;
using API;
using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.SeriesContext;
using Moq;
using MangaDto = API.Controllers.DTOs.Series;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchemaManga = API.Schema.SeriesContext.Series;
using SchemaConnectorId = API.Schema.SeriesContext.SourceId<API.Schema.SeriesContext.Series>;

namespace API.Tests.Controllers;

public class SearchControllerTests
{
    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    private static SearchController CreateController(
        SeriesContext ctx,
        Func<string, string, (SchemaManga, SchemaConnectorId)?>? connectorLookup = null)
    {
        var connectors = Enumerable.Empty<API.MangaConnectors.SeriesSource>();
        var workerQueue = new Mock<API.Workers.IWorkerQueue>().Object;
        var controller = new SearchController(ctx, connectors, workerQueue, connectorLookup ?? ((_, _) => null));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static SchemaManga MakeTestManga(string name, string coverUrl = "http://example.com/cover.jpg")
        => new(name, "A description", coverUrl, SeriesReleaseStatus.Continuing, [], [], [], []);

    private static SchemaConnectorId MakeConnectorId(SchemaManga manga, string connectorName, string idOnSite)
        => new(manga, connectorName, idOnSite, null, false);

    [Fact]
    public async Task GetMangaFromConnector_KnownConnectorAndId_ReturnsMangaDto()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var connectorId = MakeConnectorId(manga, "MangaDex", "berserk-id-123");

        (SchemaManga, SchemaConnectorId)? Lookup(string connectorName, string id)
        {
            if (connectorName == "MangaDex" && id == "berserk-id-123")
                return (manga, connectorId);
            return null;
        }

        var result = await CreateController(ctx, Lookup).GetMangaFromConnector("MangaDex", "berserk-id-123");

        var ok = Assert.IsType<Ok<MangaDto>>(result.Result);
        Assert.Equal("Berserk", ok.Value!.Name);
        var dtoId = Assert.Single(ok.Value.SourceIds);
        Assert.Equal("berserk-id-123", dtoId.ObjId);
    }

    [Fact]
    public async Task GetMangaFromConnector_UnknownId_ReturnsNotFound()
    {
        using var ctx = CreateContext();

        var result = await CreateController(ctx).GetMangaFromConnector("MangaDex", "does-not-exist");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task GetMangaFromConnector_DoesNotPersistMangaToDatabase()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var connectorId = MakeConnectorId(manga, "MangaDex", "berserk-id-123");

        var result = await CreateController(ctx, (_, _) => (manga, connectorId))
            .GetMangaFromConnector("MangaDex", "berserk-id-123");

        Assert.IsType<Ok<MangaDto>>(result.Result);
        Assert.Equal(0, await ctx.Series.CountAsync());
    }

    [Fact]
    public async Task GetMangaFromConnector_IdContainingSlash_ReturnsMangaDto()
    {
        // IDs like "2003/one-punch-man" must work; routing must accept ConnectorSeriesId as a query param
        // so that ASP.NET Core doesn't reject the encoded slash (%2F) in a path segment.
        using var ctx = CreateContext();
        var manga = MakeTestManga("One Punch Man");
        var connectorId = MakeConnectorId(manga, "Mangaworld", "2003/one-punch-man");

        var result = await CreateController(ctx, (_, id) => id == "2003/one-punch-man" ? (manga, connectorId) : null)
            .GetMangaFromConnector("Mangaworld", "2003/one-punch-man");

        var ok = Assert.IsType<Ok<MangaDto>>(result.Result);
        Assert.Equal("One Punch Man", ok.Value!.Name);
    }

    [Fact]
    public async Task GetMangaFromConnector_ConnectorMangaIdIsFromQueryParameter()
    {
        // Verifies the routing fix: ConnectorSeriesId must be a query param so that
        // IDs containing slashes (e.g. "2003/one-punch-man") are not rejected by ASP.NET Core routing.
        var method = typeof(SearchController).GetMethod(nameof(SearchController.GetMangaFromConnector));
        Assert.NotNull(method);
        var param = method!.GetParameters().Single(p => p.Name == "ConnectorSeriesId");
        Assert.True(
            param.GetCustomAttributes(typeof(FromQueryAttribute), inherit: false).Length > 0,
            "ConnectorSeriesId must be decorated with [FromQuery] to allow slash-containing IDs");
    }

    [Fact]
    public async Task SearchManga_ReturnsCoverUrl()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("One Punch Man", "http://example.com/opm.jpg");
        var connectorId = MakeConnectorId(manga, "MangaDex", "opm-id");

        var mockConnector = new Mock<API.MangaConnectors.SeriesSource>("MangaDex", new[] { "en" }, new[] { "mangadex.org" }, "icon.png", new TrangaSettings());
        mockConnector.Setup(c => c.SearchManga(It.IsAny<string>())).ReturnsAsync([(manga, connectorId)]);
        // Enabled is true by default, and Name is set in constructor.

        var connectors = new[] { mockConnector.Object };
        var workerQueue = new Mock<API.Workers.IWorkerQueue>().Object;
        var controller = new SearchController(ctx, connectors, workerQueue);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.SearchManga("MangaDex", "one punch man");

        var ok = Assert.IsType<Ok<List<MinimalSeries>>>(result.Result);
        var searchResult = Assert.Single(ok.Value!);
        Assert.Equal("http://example.com/opm.jpg", searchResult.CoverUrl);
    }

    [Fact]
    public async Task GetMangaFromConnector_ExistingTrackedManga_ReturnsRealFileLibraryId()
    {
        using var ctx = CreateContext();
        var library = new API.Schema.SeriesContext.FileLibrary("/manga", "Main Lib");
        ctx.FileLibraries.Add(library);
        
        var manga = MakeTestManga("One Piece");
        manga.Library = library;
        manga.IsTracked = true;
        ctx.Series.Add(manga);
        
        var connectorId = new SchemaConnectorId(manga, "MangaDex", "op-123", "http://op.com", false);
        ctx.MangaConnectorToManga.Add(connectorId);
        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx, (_, _) => (manga, connectorId))
            .GetMangaFromConnector("MangaDex", "op-123");

        var ok = Assert.IsType<Ok<MangaDto>>(result.Result);
        Assert.Equal(library.Key, ok.Value!.FileLibraryId);
        Assert.Equal(manga.Key, ok.Value.Key);
    }

    [Fact]
    public async Task SearchManga_ExistingTrackedManga_ReturnsRealFileLibraryId()
    {
        using var ctx = CreateContext();
        var library = new API.Schema.SeriesContext.FileLibrary("/manga", "Main Lib");
        ctx.FileLibraries.Add(library);
        
        var manga = MakeTestManga("One Piece");
        manga.Library = library;
        manga.IsTracked = true;
        ctx.Series.Add(manga);
        
        var connectorId = new SchemaConnectorId(manga, "MangaDex", "op-123", "http://op.com", false);
        ctx.MangaConnectorToManga.Add(connectorId);
        ctx.SaveChanges();

        var mockConnector = new Mock<API.MangaConnectors.SeriesSource>("MangaDex", new[] { "en" }, new[] { "mangadex.org" }, "icon.png", new TrangaSettings());
        mockConnector.Setup(c => c.SearchManga(It.IsAny<string>())).ReturnsAsync([(manga, connectorId)]);

        var controller = CreateController(ctx);
        // We need to inject the mock connector. The CreateController helper doesn't support it well currently.
        // Let's manually create it.
        var connectors = new[] { mockConnector.Object };
        var workerQueue = new Mock<API.Workers.IWorkerQueue>().Object;
        var searchController = new SearchController(ctx, connectors, workerQueue, null);
        searchController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await searchController.SearchManga("MangaDex", "One Piece");

        var ok = Assert.IsType<Ok<List<MinimalSeries>>>(result.Result);
        var searchResult = Assert.Single(ok.Value!);
        Assert.Equal(library.Key, searchResult.FileLibraryId);
        Assert.Equal("en", searchResult.Language);
        Assert.Equal(manga.Key, searchResult.Key);
    }
}
