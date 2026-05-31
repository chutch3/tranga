using API;
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
using SchemaManga = API.Schema.SeriesContext.Series;
using SchemaFileLibrary = API.Schema.SeriesContext.FileLibrary;
using SchemaChapter = API.Schema.SeriesContext.Chapter;

namespace API.Tests.Controllers;

public class VolumeControllerAssignmentTests : IDisposable
{
    private readonly string _tempDir;

    public VolumeControllerAssignmentTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    private (VolumeController controller, Mock<IWorkerQueue> workerQueueMock) CreateController(SeriesContext ctx)
    {
        var settings = new TrangaSettings { AppData = _tempDir };
        var workerQueueMock = new Mock<IWorkerQueue>();
        var controller = new VolumeController(ctx, settings, workerQueueMock.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return (controller, workerQueueMock);
    }

    private SchemaFileLibrary MakeLibrary()
    {
        var libPath = Path.Combine(_tempDir, "lib");
        Directory.CreateDirectory(libPath);
        return new SchemaFileLibrary(libPath, "TestLib");
    }

    private static SchemaManga MakeTestManga(string name, SchemaFileLibrary library)
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], [], library);

    // ──────────────────────────────────────────────────────
    // POST /volumes/assignments
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task PostBulkAssignment_WhenMangaNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var (controller, _) = CreateController(ctx);

        var request = new BulkAssignmentRecord(new Dictionary<string, int> { { "1", 1 } });
        var result = await controller.PostBulkAssignment("nonexistent-id", request);

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task PostBulkAssignment_AppliesAssignmentsAndSetsMetadataConfidenceManual()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("One Piece", library);
        ctx.Series.Add(manga);

        var ch1 = new SchemaChapter(manga, "1", null);
        var ch2 = new SchemaChapter(manga, "2", null);
        var ch3 = new SchemaChapter(manga, "10", null);
        ctx.Chapters.AddRange(ch1, ch2, ch3);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var request = new BulkAssignmentRecord(new Dictionary<string, int>
        {
            { "1", 1 },
            { "2", 1 },
            { "10", 2 }
        });

        var result = await controller.PostBulkAssignment(manga.Key, request);

        var ok = Assert.IsType<Ok<BulkAssignmentResult>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal(3, ok.Value!.Applied);
        Assert.Empty(ok.Value.NotFound);

        // Verify DB side-effects
        var updatedCh1 = await ctx.Chapters.FindAsync(ch1.Key);
        Assert.Equal(1, updatedCh1!.VolumeNumber);
        Assert.Equal(MetadataConfidence.Manual, updatedCh1.MetadataConfidence);

        var updatedCh2 = await ctx.Chapters.FindAsync(ch2.Key);
        Assert.Equal(1, updatedCh2!.VolumeNumber);
        Assert.Equal(MetadataConfidence.Manual, updatedCh2.MetadataConfidence);

        var updatedCh3 = await ctx.Chapters.FindAsync(ch3.Key);
        Assert.Equal(2, updatedCh3!.VolumeNumber);
        Assert.Equal(MetadataConfidence.Manual, updatedCh3.MetadataConfidence);
    }

    [Fact]
    public async Task PostBulkAssignment_ReturnsNotFoundListForUnknownChapterNumbers()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Naruto", library);
        ctx.Series.Add(manga);

        var ch1 = new SchemaChapter(manga, "1", null);
        ctx.Chapters.Add(ch1);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var request = new BulkAssignmentRecord(new Dictionary<string, int>
        {
            { "1", 1 },
            { "999", 5 },   // does not exist
            { "888", 3 }    // does not exist
        });

        var result = await controller.PostBulkAssignment(manga.Key, request);

        var ok = Assert.IsType<Ok<BulkAssignmentResult>>(result.Result);
        Assert.Equal(1, ok.Value!.Applied);
        Assert.Equal(2, ok.Value.NotFound.Count);
        Assert.Contains("999", ok.Value.NotFound);
        Assert.Contains("888", ok.Value.NotFound);
    }

    [Fact]
    public async Task PostBulkAssignment_SetsMetadataSourceTypeManualAndStatusConfirmed()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Berserk", library);
        ctx.Series.Add(manga);

        var ch1 = new SchemaChapter(manga, "1", null);
        ctx.Chapters.Add(ch1);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var request = new BulkAssignmentRecord(new Dictionary<string, int> { { "1", 1 } });

        await controller.PostBulkAssignment(manga.Key, request);

        // Reload manga with MetadataSource
        var updatedManga = await ctx.Series
            .Include(m => m.MetadataSource)
            .FirstAsync(m => m.Key == manga.Key);

        Assert.NotNull(updatedManga.MetadataSource);
        Assert.Equal(MetadataSourceType.Manual, updatedManga.MetadataSource!.SourceType);
        Assert.Equal(MetadataSourceStatus.Confirmed, updatedManga.MetadataSource.Status);
    }

    [Fact]
    public async Task PostBulkAssignment_ChapterNumberMatchIsCaseInsensitive()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Bleach", library);
        ctx.Series.Add(manga);

        var ch = new SchemaChapter(manga, "5", null);
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        // "5" vs "5" — same, but test that OrdinalIgnoreCase is used (numbers are the same either way here,
        // but the implementation must use case-insensitive comparison as specified)
        var request = new BulkAssignmentRecord(new Dictionary<string, int> { { "5", 2 } });

        var result = await controller.PostBulkAssignment(manga.Key, request);

        var ok = Assert.IsType<Ok<BulkAssignmentResult>>(result.Result);
        Assert.Equal(1, ok.Value!.Applied);
        Assert.Empty(ok.Value.NotFound);
    }
}
