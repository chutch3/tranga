using API;
using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Chapter = API.Schema.SeriesContext.Chapter;
using ConnectorId = API.Schema.SeriesContext.SourceId<API.Schema.SeriesContext.Series>;

namespace API.Tests.Controllers;

public class MangaControllerTests
{
    private (SeriesContext, ActionsContext) CreateContexts()
    {
        var mangaOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return (new SeriesContext(mangaOptions), new ActionsContext(actionsOptions));
    }

    private static SeriesController CreateController(
        SeriesContext ctx, 
        ActionsContext actionsCtx, 
        IEnumerable<API.MangaConnectors.SeriesSource>? connectors = null)
    {
        var settings = new TrangaSettings { AppData = Path.GetTempPath() };
        var connectorsList = connectors ?? Enumerable.Empty<API.MangaConnectors.SeriesSource>();
        var workerQueue = new Mock<API.Workers.IWorkerQueue>().Object;
        var controller = new SeriesController(ctx, actionsCtx, settings, connectorsList, workerQueue);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static API.Schema.SeriesContext.Series MakeTestManga(string name)
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public async Task GetAllManga_ExcludesSearchOnlyManga()
    {
        var (ctx, actionsCtx) = CreateContexts();
        ctx.Series.Add(MakeTestManga("SearchResult"));
        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx, actionsCtx).GetAllManga();

        var ok = Assert.IsType<Ok<List<MinimalSeries>>>(result.Result);
        Assert.Empty(ok.Value!);
    }

    [Fact]
    public async Task ChangeLibrary_AddsUntrackedMangaWhenConnectorInfoProvided()
    {
        var (ctx, actionsCtx) = CreateContexts();
        var library = new API.Schema.SeriesContext.FileLibrary(Path.GetTempPath(), "TestLib");
        ctx.FileLibraries.Add(library);
        await ctx.SaveChangesAsync();

        var manga = MakeTestManga("New Series");
        var connectorId = new ConnectorId(manga, "MangaDex", "ext-id", null);

        var mockConnector = new Mock<API.MangaConnectors.SeriesSource>("MangaDex", new[] { "en" }, new[] { "mangadex.org" }, "icon.png", new TrangaSettings());
        mockConnector.Setup(c => c.GetMangaFromId("ext-id")).ReturnsAsync((manga, connectorId));

        var controller = CreateController(ctx, actionsCtx, [mockConnector.Object]);
        
        var result = await controller.ChangeLibrary(manga.Key, library.Key, "MangaDex", "ext-id");

        Assert.IsType<Ok>(result.Result);
        var mangaInDb = await ctx.Series.FirstOrDefaultAsync(m => m.Key == manga.Key);
        Assert.NotNull(mangaInDb);
        Assert.True(mangaInDb.IsTracked);
        Assert.Equal(library.Key, mangaInDb.LibraryId);
    }
}
