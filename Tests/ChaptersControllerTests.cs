using API.Controllers;
using API.Controllers.Requests;
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

    private static ChaptersController CreateController(MangaContext ctx, Func<string, string, Task>? moveFile = null)
    {
        var controller = new ChaptersController(ctx, moveFile ?? ((_, _) => Task.CompletedTask));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static Manga MakeTestManga(string name)
        => new(name, "", "http://example.com/img.jpg", MangaReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public async Task UpdateChapter_KnownChapter_UpdatesFileNameAndVolumeNumber()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var chapter = new Chapter(manga, "1", null);
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
        var chapter = new Chapter(manga, "1", 5);
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
    public async Task UpdateChapter_FileNameChanges_MovesFileFromOldPathToNewPath()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var chapter = new Chapter(manga, "1", null);
        chapter.FileName = "Berserk - Ch.1.cbz";
        ctx.Mangas.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        string? capturedSrc = null;
        string? capturedDst = null;
        Task CaptureMove(string src, string dst)
        {
            capturedSrc = src;
            capturedDst = dst;
            return Task.CompletedTask;
        }

        var request = new PatchChapterRecord("Berserk Vol 1/Berserk - Ch.1.cbz", 1);
        await CreateController(ctx, CaptureMove).UpdateChapter(chapter.Key, request);

        Assert.Equal("Berserk - Ch.1.cbz", capturedSrc);
        Assert.Equal("Berserk Vol 1/Berserk - Ch.1.cbz", capturedDst);
    }

    [Fact]
    public async Task UpdateChapter_FileNameUnchanged_DoesNotMoveFile()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var chapter = new Chapter(manga, "1", null);
        chapter.FileName = "Berserk - Ch.1.cbz";
        ctx.Mangas.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var moveInvoked = false;
        Task TrackMove(string src, string dst) { moveInvoked = true; return Task.CompletedTask; }

        var request = new PatchChapterRecord("Berserk - Ch.1.cbz", 1);
        await CreateController(ctx, TrackMove).UpdateChapter(chapter.Key, request);

        Assert.False(moveInvoked);
    }

    [Fact]
    public async Task UpdateChapter_MoveThrows_ReturnsInternalServerError_AndDoesNotUpdateDb()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var chapter = new Chapter(manga, "1", null);
        chapter.FileName = "Berserk - Ch.1.cbz";
        ctx.Mangas.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        Task FailingMove(string src, string dst) => Task.FromException(new IOException("disk full"));

        var request = new PatchChapterRecord("Berserk Vol 1/Berserk - Ch.1.cbz", 1);
        var result = await CreateController(ctx, FailingMove).UpdateChapter(chapter.Key, request);

        Assert.IsType<InternalServerError<string>>(result.Result);
        var unchanged = await ctx.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal("Berserk - Ch.1.cbz", unchanged.FileName);
        Assert.Null(unchanged.VolumeNumber);
    }
}
