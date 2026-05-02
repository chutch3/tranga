using API.Controllers;
using API.Controllers.Requests;
using API.Controllers.DTOs;
using API.Schema.MangaContext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;


namespace Tests;

public class ChaptersControllerTests
{
    private MangaContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MangaContext(options);
    }

    private static ChaptersController CreateController(MangaContext ctx)
    {
        var controller = new ChaptersController(ctx);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static API.Schema.MangaContext.Manga MakeTestManga(string name)
        => new(name, "", "http://example.com/img.jpg", MangaReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public async Task UpdateChapter_KnownChapter_UpdatesFileNameAndVolumeNumber()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var chapter = new API.Schema.MangaContext.Chapter(manga, "1", null);
        ctx.Mangas.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var request = new PatchChapterRecord("Berserk Vol 1/Berserk - Ch.1.cbz", 1);
        var result = await CreateController(ctx).UpdateChapter(chapter.Key, request);

        Assert.IsType<Ok>(result.Result);
        var updated = await ctx.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal("Berserk Vol 1/Berserk - Ch.1.cbz", updated.FileName);
        Assert.Equal(1, updated.VolumeNumber);
    }

    [Fact]
    public async Task UpdateChapter_UnknownChapterId_ReturnsNotFound()
    {
        using var ctx = CreateContext();

        var request = new PatchChapterRecord("Berserk Vol 1/Berserk - Ch.1.cbz", 1);
        var result = await CreateController(ctx).UpdateChapter("nonexistent-id", request);

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task UpdateChapter_NullVolumeNumber_ClearsVolumeNumber()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var chapter = new API.Schema.MangaContext.Chapter(manga, "1", 5);
        ctx.Mangas.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var request = new PatchChapterRecord("Berserk - Ch.1.cbz", null);
        var result = await CreateController(ctx).UpdateChapter(chapter.Key, request);

        Assert.IsType<Ok>(result.Result);
        var updated = await ctx.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal("Berserk - Ch.1.cbz", updated.FileName);
        Assert.Null(updated.VolumeNumber);
    }


    [Fact]
    public async Task GetChapters_InvalidPagination_ReturnsBadRequest()
    {
        // Edge Case: User passes 0 or negative numbers for pagination
        using var ctx = CreateContext();

        var result = await CreateController(ctx).GetChapters("any-id", filter: null, page: 0, pageSize: 10);

        Assert.IsType<BadRequest>(result.Result);
    }

    [Fact]
    public async Task GetChapters_WithDownloadedFilter_ReturnsOnlyDownloadedChapters()
    {
        // Edge Case: Filtering should correctly exclude non-matching records
        using var ctx = CreateContext();
        var manga = MakeTestManga("One Punch Man");

        var downloadedChapter = new API.Schema.MangaContext.Chapter(manga, "1", 1) { Downloaded = true };
        var missingChapter = new API.Schema.MangaContext.Chapter(manga, "2", 1) { Downloaded = false };

        ctx.Mangas.Add(manga);
        ctx.Chapters.AddRange(downloadedChapter, missingChapter);
        await ctx.SaveChangesAsync();

        var filter = new ChapterFilterRecord(true, null, null, null);
        var response = await CreateController(ctx).GetChapters(manga.Key, filter, page: 1, pageSize: 10);

        var okResult = Assert.IsType<Ok<PagedResponse<API.Controllers.DTOs.Chapter>>>(response.Result);
        var pagedData = okResult.Value;

        Assert.NotNull(pagedData);
        Assert.Single(pagedData.Data); // Should only return the 1 downloaded chapter
        Assert.Equal(downloadedChapter.Key, pagedData.Data.First().Key);
    }

    [Fact]
    public async Task GetChapters_MultiplePages_ReturnsCorrectPaginationMetadata()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Naruto");
        ctx.Mangas.Add(manga);

        // Add 15 chapters
        for (int i = 1; i <= 15; i++)
        {
            ctx.Chapters.Add(new API.Schema.MangaContext.Chapter(manga, i.ToString(), null));
        }
        await ctx.SaveChangesAsync();
        var response = await CreateController(ctx).GetChapters(manga.Key, null, page: 1, pageSize: 10);
        var okResult = Assert.IsType<Ok<API.Controllers.DTOs.PagedResponse<API.Controllers.DTOs.Chapter>>>(response.Result);
        var pagedData = okResult.Value;

        Assert.NotNull(pagedData);
        Assert.Equal(2, pagedData.TotalPages); // 15 items / 10 per page = 2 pages
        Assert.Equal(10, pagedData.Data.Count());
    }

    [Fact]
    public async Task GetChapter_KnownId_ReturnsChapter()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Jujutsu Kaisen");
        var chapter = new API.Schema.MangaContext.Chapter(manga, "1", 1);
        ctx.Mangas.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx).GetChapter(chapter.Key);

        var okResult = Assert.IsType<Ok<API.Controllers.DTOs.Chapter>>(result.Result);

        // Assert it's not null to fix the CS8602 warning
        Assert.NotNull(okResult.Value);
        Assert.Equal(chapter.Key, okResult.Value.Key);
    }

    [Fact]
    public async Task GetLatestChapter_UnknownManga_ReturnsNotFound()
    {
        using var ctx = CreateContext();

        // Requesting latest chapter for a manga ID that doesn't exist in the DB
        var result = await CreateController(ctx).GetLatestChapter("invalid-manga-id");

        Assert.IsType<NotFound<string>>(result.Result);
    }


    [Fact]
    public async Task GetLatestDownloaded_WhenNoneAreDownloaded_ReturnsNoContent()
    {
        // Edge Case: The manga exists and has chapters, but NONE of them are downloaded yet.
        using var ctx = CreateContext();
        var manga = MakeTestManga("Mob Psycho 100");

        // Add 3 chapters, all marked as not downloaded
        ctx.Mangas.Add(manga);
        ctx.Chapters.Add(new API.Schema.MangaContext.Chapter(manga, "1", 1) { Downloaded = false });
        ctx.Chapters.Add(new API.Schema.MangaContext.Chapter(manga, "2", 1) { Downloaded = false });
        ctx.Chapters.Add(new API.Schema.MangaContext.Chapter(manga, "3", 1) { Downloaded = false });
        await ctx.SaveChangesAsync();

        var result = await CreateController(ctx).GetLatestChapterDownloaded(manga.Key);

        Assert.IsType<NoContent>(result.Result);
    }


    [Fact]
    public async Task IgnoreChaptersBefore_ValidManga_UpdatesThresholdInDatabase()
    {
        // Edge Case: Ensure the threshold physically saves to the DB entity
        using var ctx = CreateContext();
        var manga = MakeTestManga("My Hero Academia");
        ctx.Mangas.Add(manga);
        await ctx.SaveChangesAsync();

        // Act: Set threshold to chapter 50.5
        float newThreshold = 50.5f;
        var result = await CreateController(ctx).IgnoreChaptersBefore(manga.Key, newThreshold);

        Assert.IsType<Ok>(result.Result);
        var updatedManga = await ctx.Mangas.FirstAsync(m => m.Key == manga.Key);
        Assert.Equal(newThreshold, updatedManga.IgnoreChaptersBefore);
    }

    [Fact]
    public async Task DeleteChapter_ExistingChapter_RemovesFromDatabase()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Attack on Titan");
        var chapter = new API.Schema.MangaContext.Chapter(manga, "1", 1);
        ctx.Mangas.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        // Ensure it's there
        Assert.Equal(1, await ctx.Chapters.CountAsync());

        var result = await CreateController(ctx).DeleteChapter(chapter.Key);

        Assert.IsType<Ok>(result.Result);
        Assert.Equal(0, await ctx.Chapters.CountAsync()); // Should be gone
    }
}
