using API.Schema.SeriesContext;
using API.Services;
using API.Workers.MaintenanceWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Tests.Workers;

/// <summary>
/// Unit tests for RefreshMetadataSourceWorker logic.
/// Tests the worker's chapter resolution behaviour using an in-memory database and mocked search service.
/// </summary>
public class RefreshMetadataSourceWorkerTests
{
    private static SeriesContext CreateContext(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    private static API.Schema.SeriesContext.Series MakeTestManga(string name = "Test Series")
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], []);

    private static IServiceScope CreateServiceScope(SeriesContext ctx, IMangaDexSearchService searchService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ctx);
        services.AddSingleton(searchService);
        var provider = services.BuildServiceProvider();
        return provider.CreateScope();
    }

    [Fact]
    public async Task Worker_ResolvesChapterVolumes_WhenMapReturnsData()
    {
        string dbName = Guid.NewGuid().ToString();
        using var ctx = CreateContext(dbName);

        var manga = MakeTestManga("One Piece");
        manga.MetadataSource!.ExternalId = "one-piece-id";
        manga.MetadataSource!.Status = MetadataSourceStatus.Confirmed;

        var ch1 = new Chapter(manga, "1", null);
        var ch2 = new Chapter(manga, "12", null);
        var ch3 = new Chapter(manga, "24", null);

        ctx.Series.Add(manga);
        ctx.Chapters.AddRange(ch1, ch2, ch3);
        await ctx.SaveChangesAsync();

        var mockSearch = new Mock<IMangaDexSearchService>();
        mockSearch.Setup(s => s.GetChapterToVolumeMapAsync("one-piece-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int>
            {
                { "1", 1 },
                { "12", 2 },
                { "24", 3 }
            });

        using var scope = CreateServiceScope(ctx, mockSearch.Object);
        var worker = new RefreshMetadataSourceWorker(manga.Key);
        // Access internals: call DoWork with the service scope
        await worker.DoWork(scope);

        // Reload from DB
        using var ctx2 = CreateContext(dbName);
        var chapters = await ctx2.Chapters.Where(c => c.ParentMangaId == manga.Key).ToListAsync();

        var loadedCh1 = chapters.First(c => c.ChapterNumber == "1");
        var loadedCh2 = chapters.First(c => c.ChapterNumber == "12");
        var loadedCh3 = chapters.First(c => c.ChapterNumber == "24");

        Assert.Equal(1, loadedCh1.VolumeNumber);
        Assert.Equal(MetadataConfidence.Exact, loadedCh1.MetadataConfidence);
        Assert.Equal(2, loadedCh2.VolumeNumber);
        Assert.Equal(MetadataConfidence.Exact, loadedCh2.MetadataConfidence);
        Assert.Equal(3, loadedCh3.VolumeNumber);
        Assert.Equal(MetadataConfidence.Exact, loadedCh3.MetadataConfidence);
    }

    [Fact]
    public async Task Worker_SetsLastSyncedAt_AfterSuccessfulRefresh()
    {
        string dbName = Guid.NewGuid().ToString();
        using var ctx = CreateContext(dbName);

        var manga = MakeTestManga("Bleach");
        manga.MetadataSource!.ExternalId = "bleach-id";
        manga.MetadataSource!.Status = MetadataSourceStatus.Confirmed;
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var mockSearch = new Mock<IMangaDexSearchService>();
        mockSearch.Setup(s => s.GetChapterToVolumeMapAsync("bleach-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, int> { { "1", 1 } });

        using var scope = CreateServiceScope(ctx, mockSearch.Object);
        var worker = new RefreshMetadataSourceWorker(manga.Key);
        await worker.DoWork(scope);

        using var ctx2 = CreateContext(dbName);
        var loaded = await ctx2.Series.Include(m => m.MetadataSource).FirstAsync(m => m.Key == manga.Key);
        Assert.NotNull(loaded.MetadataSource!.LastSyncedAt);
    }

    [Fact]
    public async Task Worker_DoesNothing_WhenMangaNotFound()
    {
        using var ctx = CreateContext();
        var mockSearch = new Mock<IMangaDexSearchService>();

        using var scope = CreateServiceScope(ctx, mockSearch.Object);
        var worker = new RefreshMetadataSourceWorker("nonexistent-manga-id");

        // Should not throw
        var result = await worker.DoWork(scope);
        Assert.Empty(result);

        mockSearch.Verify(s => s.GetChapterToVolumeMapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Worker_DoesNothing_WhenStatusIsUnlinked()
    {
        string dbName = Guid.NewGuid().ToString();
        using var ctx = CreateContext(dbName);

        var manga = MakeTestManga("Naruto");
        // MetadataSource defaults to Unlinked with no ExternalId
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var mockSearch = new Mock<IMangaDexSearchService>();

        using var scope = CreateServiceScope(ctx, mockSearch.Object);
        var worker = new RefreshMetadataSourceWorker(manga.Key);
        await worker.DoWork(scope);

        mockSearch.Verify(s => s.GetChapterToVolumeMapAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
