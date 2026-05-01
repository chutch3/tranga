using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Chapter = API.Schema.MangaContext.Chapter;
using ConnectorId = API.Schema.MangaContext.MangaConnectorId<API.Schema.MangaContext.Manga>;

namespace Tests;

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

    private static MangaController CreateController(MangaContext ctx, ActionsContext actionsCtx)
    {
        var controller = new MangaController(ctx, actionsCtx);
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
    public async Task GetAllManga_IncludesTrackedManga()
    {
        var (ctx, actionsCtx) = CreateContexts();
        var manga = MakeTestManga("Tracked");
        manga.IsTracked = true;
        ctx.Mangas.Add(manga);
        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx, actionsCtx).GetAllManga();

        var ok = Assert.IsType<Ok<List<MinimalManga>>>(result.Result);
        Assert.Single(ok.Value!);
        Assert.Equal("Tracked", ok.Value![0].Name);
    }

    [Fact]
    public async Task GetAllManga_IncludesMangaWithDownloadedChapter()
    {
        var (ctx, actionsCtx) = CreateContexts();
        var manga = MakeTestManga("Downloaded");
        ctx.Mangas.Add(manga);
        var chapter = new Chapter(manga, "1", null);
        chapter.Downloaded = true;
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx, actionsCtx).GetAllManga();

        var ok = Assert.IsType<Ok<List<MinimalManga>>>(result.Result);
        Assert.Single(ok.Value!);
    }

    [Fact]
    public async Task GetAllManga_IncludesMangaWithUseForDownload()
    {
        var (ctx, actionsCtx) = CreateContexts();
        var manga = MakeTestManga("Monitored");
        ctx.Mangas.Add(manga);
        ctx.Set<ConnectorId>()
            .Add(new ConnectorId(manga, "TestConnector", "ext-id", null, useForDownload: true));
        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx, actionsCtx).GetAllManga();

        var ok = Assert.IsType<Ok<List<MinimalManga>>>(result.Result);
        Assert.Single(ok.Value!);
    }

    [Fact]
    public async Task GetAllManga_ExcludesMixedBag_OnlyReturnsTracked()
    {
        var (ctx, actionsCtx) = CreateContexts();

        var searchOnly = MakeTestManga("SearchOnly");
        ctx.Mangas.Add(searchOnly);

        var tracked = MakeTestManga("Tracked");
        tracked.IsTracked = true;
        ctx.Mangas.Add(tracked);

        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx, actionsCtx).GetAllManga();

        var ok = Assert.IsType<Ok<List<MinimalManga>>>(result.Result);
        Assert.Single(ok.Value!);
        Assert.Equal("Tracked", ok.Value![0].Name);
    }
}
