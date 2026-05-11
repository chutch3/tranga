using API;
using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Chapter = API.Schema.MangaContext.Chapter;
using ConnectorId = API.Schema.MangaContext.MangaConnectorId<API.Schema.MangaContext.Manga>;

namespace API.Tests.Controllers;

public class MangaControllerTests
{
    private (MangaContext, ActionsContext) CreateContexts()
    {
        var mangaOptions = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return (new MangaContext(mangaOptions), new ActionsContext(actionsOptions));
    }

    private static MangaController CreateController(
        MangaContext ctx, 
        ActionsContext actionsCtx, 
        IEnumerable<API.MangaConnectors.MangaConnector>? connectors = null)
    {
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };
        var connectorsList = connectors ?? Enumerable.Empty<API.MangaConnectors.MangaConnector>();
        var workerQueue = new Mock<API.Workers.IWorkerQueue>().Object;
        var controller = new MangaController(ctx, actionsCtx, settings, connectorsList, workerQueue);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static API.Schema.MangaContext.Manga MakeTestManga(string name)
        => new(name, "", "http://example.com/img.jpg", MangaReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public async Task GetAllManga_ExcludesSearchOnlyManga()
    {
        var (ctx, actionsCtx) = CreateContexts();
        ctx.Mangas.Add(MakeTestManga("SearchResult"));
        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx, actionsCtx).GetAllManga();

        var ok = Assert.IsType<Ok<List<MinimalManga>>>(result.Result);
        Assert.Empty(ok.Value!);
    }

    [Fact]
    public async Task ChangeLibrary_AddsUntrackedMangaWhenConnectorInfoProvided()
    {
        var (ctx, actionsCtx) = CreateContexts();
        var library = new API.Schema.MangaContext.FileLibrary(Path.GetTempPath(), "TestLib");
        ctx.FileLibraries.Add(library);
        await ctx.SaveChangesAsync();

        var manga = MakeTestManga("New Manga");
        var connectorId = new ConnectorId(manga, "MangaDex", "ext-id", null);

        var mockConnector = new Mock<API.MangaConnectors.MangaConnector>("MangaDex", new[] { "en" }, new[] { "mangadex.org" }, "icon.png", new TrangaSettings());
        mockConnector.Setup(c => c.GetMangaFromId("ext-id")).ReturnsAsync((manga, connectorId));

        var controller = CreateController(ctx, actionsCtx, [mockConnector.Object]);
        
        var result = await controller.ChangeLibrary(manga.Key, library.Key, "MangaDex", "ext-id");

        Assert.IsType<Ok>(result.Result);
        var mangaInDb = await ctx.Mangas.FirstOrDefaultAsync(m => m.Key == manga.Key);
        Assert.NotNull(mangaInDb);
        Assert.True(mangaInDb.IsTracked);
        Assert.Equal(library.Key, mangaInDb.LibraryId);
    }
}
