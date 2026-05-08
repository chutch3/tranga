using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;

namespace API.Tests.Schema;

public class MangaTests
{
    private MangaContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MangaContext(options);
    }

    internal static Manga MakeTestManga(string name = "Test Manga")
        => new(name, "", "http://example.com/img.jpg", MangaReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public async Task GetTrackedMangas_IncludesManga_WhenIsTrackedTrue()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga();
        manga.IsTracked = true;
        ctx.Mangas.Add(manga);
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
        ctx.Mangas.Add(manga);
        var connectorId = new MangaConnectorId<Manga>(manga, "TestConnector", "ext-id-1", null, useForDownload: true);
        ctx.Set<MangaConnectorId<Manga>>().Add(connectorId);
        await ctx.SaveChangesAsync();

        var result = await ctx.GetTrackedMangas().ToArrayAsync();

        Assert.Single(result);
    }

    [Fact]
    public async Task GetTrackedMangas_IncludesManga_WhenHasDownloadedChapter()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga();
        ctx.Mangas.Add(manga);
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
        ctx.Mangas.Add(manga);
        await ctx.SaveChangesAsync();

        var result = await ctx.GetTrackedMangas().ToArrayAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTrackedMangas_ExcludesManga_WhenConnectorIdExistsButUseForDownloadFalse()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga();
        ctx.Mangas.Add(manga);
        var connectorId = new MangaConnectorId<Manga>(manga, "TestConnector", "ext-id-2", null, useForDownload: false);
        ctx.Set<MangaConnectorId<Manga>>().Add(connectorId);
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
}
