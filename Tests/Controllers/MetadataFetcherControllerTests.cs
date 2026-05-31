using API;
using API.Controllers;
using API.Tests.Schema;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Schema.SeriesContext.MetadataFetchers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace API.Tests.Controllers;

public class MetadataFetcherControllerTests
{
    private SeriesContext CreateMangaContext() =>
        new(new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private ActionsContext CreateActionsContext() =>
        new(new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private MetadataFetcherController CreateController(
        SeriesContext mangaCtx,
        ActionsContext actionsCtx,
        IEnumerable<MetadataFetcher> fetchers)
    {
        var controller = new MetadataFetcherController(mangaCtx, actionsCtx, fetchers);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    // Concrete test double — parameterless MetadataFetcher sets Name = GetType().Name
    private sealed class FakeFetcher : MetadataFetcher
    {
        public override Task<MetadataSearchResult[]> SearchMetadataEntry(Series manga) => Task.FromResult<MetadataSearchResult[]>([]);
        public override Task<MetadataSearchResult[]> SearchMetadataEntry(string searchTerm) => Task.FromResult<MetadataSearchResult[]>([]);
        public override Task UpdateMetadata(MetadataEntry metadataEntry, SeriesContext dbContext, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public void GetConnectors_ReturnsAllFetcherNames()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var f1 = new FakeFetcher();
        var f2 = new FakeFetcher();

        var result = CreateController(mangaCtx, actionsCtx, [f1, f2]).GetConnectors();

        var ok = Assert.IsType<Ok<List<string>>>(result);
        Assert.Equal(2, ok.Value!.Count);
        // Both have the same type name — just verify count
        Assert.All(ok.Value, name => Assert.Equal("FakeFetcher", name));
    }

    [Fact]
    public void GetConnectors_WhenEmpty_ReturnsEmptyList()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();

        var result = CreateController(mangaCtx, actionsCtx, []).GetConnectors();

        var ok = Assert.IsType<Ok<List<string>>>(result);
        Assert.Empty(ok.Value!);
    }

    [Fact]
    public async Task GetLinkedEntries_NoEntries_ReturnsEmptyList()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();

        var result = await CreateController(mangaCtx, actionsCtx, []).GetLinkedEntries();

        var ok = Assert.IsType<Ok<List<MetadataEntry>>>(result.Result);
        Assert.Empty(ok.Value!);
    }

    [Fact]
    public async Task SearchMangaMetadata_UnknownMangaId_ReturnsNotFound()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var fetcher = new FakeFetcher();

        var result = await CreateController(mangaCtx, actionsCtx, [fetcher])
            .SearchMangaMetadata("nonexistent-id", fetcher.Name);

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task SearchMangaMetadata_UnknownFetcherName_ReturnsBadRequest()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var manga = MangaTests.MakeTestManga();
        mangaCtx.Series.Add(manga);
        await mangaCtx.SaveChangesAsync();

        var result = await CreateController(mangaCtx, actionsCtx, [])
            .SearchMangaMetadata(manga.Key, "UnknownFetcher");

        Assert.IsType<BadRequest>(result.Result);
    }

    [Fact]
    public async Task LinkMangaMetadata_UnknownMangaId_ReturnsNotFound()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var fetcher = new FakeFetcher();

        var result = await CreateController(mangaCtx, actionsCtx, [fetcher])
            .LinkMangaMetadata("nonexistent-id", fetcher.Name, "12345");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task LinkMangaMetadata_UnknownFetcherName_ReturnsBadRequest()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var manga = MangaTests.MakeTestManga();
        mangaCtx.Series.Add(manga);
        await mangaCtx.SaveChangesAsync();

        var result = await CreateController(mangaCtx, actionsCtx, [])
            .LinkMangaMetadata(manga.Key, "UnknownFetcher", "12345");

        Assert.IsType<BadRequest>(result.Result);
    }

    [Fact]
    public async Task UnlinkMangaMetadata_UnknownMangaId_ReturnsNotFound()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var fetcher = new FakeFetcher();

        var result = await CreateController(mangaCtx, actionsCtx, [fetcher])
            .UnlinkMangaMetadata("nonexistent-id", fetcher.Name);

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task UnlinkMangaMetadata_UnknownFetcherName_ReturnsBadRequest()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var manga = MangaTests.MakeTestManga();
        mangaCtx.Series.Add(manga);
        await mangaCtx.SaveChangesAsync();

        var result = await CreateController(mangaCtx, actionsCtx, [])
            .UnlinkMangaMetadata(manga.Key, "UnknownFetcher");

        Assert.IsType<BadRequest>(result.Result);
    }

    [Fact]
    public async Task UpdateMetadata_NoLinkedEntry_ReturnsPreconditionFailed()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var fetcher = new FakeFetcher();

        var result = await CreateController(mangaCtx, actionsCtx, [fetcher])
            .UpdateMetadata("some-manga-id", fetcher.Name);

        var statusResult = Assert.IsType<StatusCodeHttpResult>(result.Result);
        Assert.Equal(412, statusResult.StatusCode);
    }

    private sealed class ExplodingFetcher : MetadataFetcher
    {
        public override Task<MetadataSearchResult[]> SearchMetadataEntry(Series manga) => throw new Exception("Jikan Gateway Timeout");
        public override Task<MetadataSearchResult[]> SearchMetadataEntry(string searchTerm) => Task.FromResult<MetadataSearchResult[]>([]);
        public override Task UpdateMetadata(MetadataEntry metadataEntry, SeriesContext dbContext, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task SearchMangaMetadata_WhenFetcherThrows_ReturnsProblem()
    {
        using var mangaCtx = CreateMangaContext();
        using var actionsCtx = CreateActionsContext();
        var manga = new Series("Test", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], []);
        mangaCtx.Series.Add(manga);
        await mangaCtx.SaveChangesAsync();
        
        var fetcher = new ExplodingFetcher();

        // This currently propagates the exception and returns 500 (crash)
        // We want it to return a clean error response.
        var result = await CreateController(mangaCtx, actionsCtx, [fetcher])
            .SearchMangaMetadata(manga.Key, fetcher.Name);

        // We expect some kind of non-crashing error result
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result.Result);
        var status = (IStatusCodeHttpResult)result.Result;
        Assert.True(status.StatusCode >= 400);
    }
}
