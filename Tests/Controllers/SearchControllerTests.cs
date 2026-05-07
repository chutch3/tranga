using API;
using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.MangaContext;
using Moq;
using MangaDto = API.Controllers.DTOs.Manga;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchemaManga = API.Schema.MangaContext.Manga;
using SchemaConnectorId = API.Schema.MangaContext.MangaConnectorId<API.Schema.MangaContext.Manga>;

namespace API.Tests.Controllers;

public class SearchControllerTests
{
    private MangaContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MangaContext(options);
    }

    private static SearchController CreateController(
        MangaContext ctx,
        Func<string, string, (SchemaManga, SchemaConnectorId)?>? connectorLookup = null)
    {
        var connectors = Enumerable.Empty<API.MangaConnectors.MangaConnector>();
        var workerQueue = new Mock<API.Workers.IWorkerQueue>().Object;
        var controller = new SearchController(ctx, connectors, workerQueue, connectorLookup ?? ((_, _) => null));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static SchemaManga MakeTestManga(string name, string coverUrl = "http://example.com/cover.jpg")
        => new(name, "A description", coverUrl, MangaReleaseStatus.Continuing, [], [], [], []);

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
        var dtoId = Assert.Single(ok.Value.MangaConnectorIds);
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
        Assert.Equal(0, await ctx.Mangas.CountAsync());
    }

    [Fact]
    public void SearchManga_ReturnsCoverUrl()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("One Punch Man", "http://example.com/opm.jpg");
        var connectorId = MakeConnectorId(manga, "MangaDex", "opm-id");

        var mockConnector = new Mock<API.MangaConnectors.MangaConnector>("MangaDex", new[] { "en" }, new[] { "mangadex.org" }, "icon.png", new TrangaSettings());
        mockConnector.Setup(c => c.SearchManga(It.IsAny<string>())).Returns([(manga, connectorId)]);
        // Enabled is true by default, and Name is set in constructor.

        var connectors = new[] { mockConnector.Object };
        var workerQueue = new Mock<API.Workers.IWorkerQueue>().Object;
        var controller = new SearchController(ctx, connectors, workerQueue);

        var result = controller.SearchManga("MangaDex", "one punch man");

        var ok = Assert.IsType<Ok<List<MinimalManga>>>(result.Result);
        var searchResult = Assert.Single(ok.Value!);
        Assert.Equal("http://example.com/opm.jpg", searchResult.CoverUrl);
    }
}
