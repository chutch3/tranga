using API.Controllers;
using API.Controllers.DTOs;
using API.Controllers.Requests;
using API.Schema.SeriesContext;
using API.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SchemaChapter = API.Schema.SeriesContext.Chapter;

namespace Tests.Controllers;

/// <summary>
/// Tests for PUT /v2/Chapters/{chapterId}/volume
/// </summary>
public class ChaptersControllerMetadataTests
{
    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    private static ChaptersController CreateController(SeriesContext ctx)
    {
        var testSettings = new API.TrangaSettings { AppData = Path.GetTempPath() };
        var mockWorkerQueue = new Mock<IWorkerQueue>();
        var connectors = Enumerable.Empty<API.MangaConnectors.SeriesSource>();
        var mockThumbnailService = new Mock<API.Services.IChapterThumbnailService>();
        var controller = new ChaptersController(ctx, testSettings, connectors, mockWorkerQueue.Object, mockThumbnailService.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static API.Schema.SeriesContext.Series MakeTestManga(string name = "Test Series")
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public async Task AssignVolume_NonNullVolume_SetsVolumeNumberAndManualConfidence()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var chapter = new SchemaChapter(manga, "1", null);
        ctx.Series.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var request = new PatchChapterVolumeRecord(7);
        var result = await CreateController(ctx).AssignChapterVolume(chapter.Key, request);

        var ok = Assert.IsType<Ok<ChapterVolumeAssignmentResult>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal(chapter.Key, ok.Value.ChapterId);
        Assert.Equal(7, ok.Value.VolumeNumber);
        Assert.Equal("Manual", ok.Value.MetadataConfidence);

        var updated = await ctx.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal(7, updated.VolumeNumber);
        Assert.Equal(MetadataConfidence.Manual, updated.MetadataConfidence);
    }

    [Fact]
    public async Task AssignVolume_NullVolume_ClearsVolumeAndConfidence()
    {
        using var ctx = CreateContext();
        var manga = MakeTestManga("Bleach");
        var chapter = new SchemaChapter(manga, "5", 3);
        chapter.MetadataConfidence = MetadataConfidence.Exact;
        ctx.Series.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var request = new PatchChapterVolumeRecord(null);
        var result = await CreateController(ctx).AssignChapterVolume(chapter.Key, request);

        var ok = Assert.IsType<Ok<ChapterVolumeAssignmentResult>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Null(ok.Value.VolumeNumber);
        Assert.Null(ok.Value.MetadataConfidence);

        var updated = await ctx.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Null(updated.VolumeNumber);
        Assert.Null(updated.MetadataConfidence);
    }

    [Fact]
    public async Task AssignVolume_UnknownChapterId_ReturnsNotFound()
    {
        using var ctx = CreateContext();
        var request = new PatchChapterVolumeRecord(5);
        var result = await CreateController(ctx).AssignChapterVolume("nonexistent-id", request);

        Assert.IsType<NotFound<string>>(result.Result);
    }
}
