using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace API.Tests.Schema;

public class MangaTests
{
    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    internal static Series MakeTestManga(string name = "Test Series")
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public async Task GetTrackedMangas_IncludesManga_WhenIsTrackedTrue()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga();
        manga.IsTracked = true;
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var result = await ctx.GetTrackedMangas().ToArrayAsync();

        Assert.Single(result);
        Assert.Equal(manga.Key, result[0].Key);
    }

    [Fact]
    public async Task GetTrackedMangas_IncludesManga_WhenUseForDownloadTrue()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga();
        ctx.Series.Add(manga);
        var connectorId = new SourceId<Series>(manga, "TestConnector", "ext-id-1", null, useForDownload: true);
        ctx.Set<SourceId<Series>>().Add(connectorId);
        await ctx.SaveChangesAsync();

        var result = await ctx.GetTrackedMangas().ToArrayAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetTrackedMangas_IncludesManga_WhenHasDownloadedChapter()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga();
        ctx.Series.Add(manga);
        var chapter = new Chapter(manga, "1", null);
        chapter.Downloaded = true;
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var result = await ctx.GetTrackedMangas().ToArrayAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetTrackedMangas_ExcludesManga_WhenSearchOnlyResult()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga();
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var result = await ctx.GetTrackedMangas().ToArrayAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTrackedMangas_ExcludesManga_WhenConnectorIdExistsButUseForDownloadFalse()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga();
        ctx.Series.Add(manga);
        var connectorId = new SourceId<Series>(manga, "TestConnector", "ext-id-2", null, useForDownload: false);
        ctx.Set<SourceId<Series>>().Add(connectorId);
        await ctx.SaveChangesAsync();

        var result = await ctx.GetTrackedMangas().ToArrayAsync();

        Assert.Empty(result);
    }

    [Fact]
    public void Manga_DefaultLibraryLayout_IsFlat()
    {
        var manga = MakeTestManga();
        Assert.Equal(LibraryLayout.Flat, manga.LibraryLayout);
    }

    [Fact]
    public void FullDirectoryPath_WhenPathRestricted_ShouldNotThrowException()
    {
        // On Linux, /root is usually restricted. 
        // We want to verify that accessing the property doesn't trigger EnsureDirectoryExists side effects
        // that cause UnauthorizedAccessException if the directory doesn't exist yet.
        var library = new FileLibrary("/root/manga_test_forbidden", "Restricted");
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        
        // This should NOT throw even if we don't have permissions to create /root/manga_test_forbidden/Test_Manga
        var path = manga.FullDirectoryPath;
        
        Assert.Equal("/root/manga_test_forbidden/Test Series", path);
    }
}
