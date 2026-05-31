using API;
using API.Controllers;
using API.Controllers.DTOs;
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

public class VolumeControllerBundleTests : IDisposable
{
    private readonly string _tempDir;

    public VolumeControllerBundleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"BundleControllerTest_{Guid.NewGuid()}");
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

    private static SchemaManga MakeManga(string name, SchemaFileLibrary library)
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], [], library);

    // ──────────────────────────────────────────────────────
    // POST /volumes/{VolumeNumber}/bundle
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task PostBundle_WhenMangaNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var (controller, _) = CreateController(ctx);

        var result = await controller.PostBundle("nonexistent-id", 1);

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task PostBundle_WhenVolumeMetadataNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeManga("Test Series", library);
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.PostBundle(manga.Key, 99);

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task PostBundle_WhenNoUnbundledChaptersExist_Returns409()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeManga("Bundled Series", library);
        ctx.Series.Add(manga);
        var vol = new VolumeMetadata(manga, 1);
        ctx.VolumeMetadata.Add(vol);
        // Add chapter that is already bundled (no FileName)
        var ch = new SchemaChapter(manga, "1", 1) { IsBundled = true, FileName = null };
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.PostBundle(manga.Key, 1);

        Assert.IsType<Conflict<string>>(result.Result);
    }

    [Fact]
    public async Task PostBundle_WhenValidRequest_Returns202WithJobId()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeManga("One Piece", library);
        ctx.Series.Add(manga);
        var vol = new VolumeMetadata(manga, 1);
        ctx.VolumeMetadata.Add(vol);
        var ch = new SchemaChapter(manga, "1", 1) { Downloaded = true, FileName = "ch1.cbz" };
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, workerQueueMock) = CreateController(ctx);
        var result = await controller.PostBundle(manga.Key, 1);

        var accepted = Assert.IsType<Accepted<BundleJobResult>>(result.Result);
        Assert.NotNull(accepted.Value);
        Assert.NotEmpty(accepted.Value!.JobId);
        workerQueueMock.Verify(q => q.AddWorker(It.IsAny<BaseWorker>()), Times.Once);
    }

    // ──────────────────────────────────────────────────────
    // DELETE /volumes/{VolumeNumber}/bundle
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBundle_WhenMangaNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var (controller, _) = CreateController(ctx);

        var result = await controller.DeleteBundle("nonexistent-id", 1);

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task DeleteBundle_WhenVolumeMetadataNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeManga("Test Series", library);
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.DeleteBundle(manga.Key, 1);

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task DeleteBundle_WhenNotBundled_Returns409()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeManga("Unbundled Series", library);
        ctx.Series.Add(manga);
        var vol = new VolumeMetadata(manga, 1); // ArchiveFileName is null
        ctx.VolumeMetadata.Add(vol);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.DeleteBundle(manga.Key, 1);

        Assert.IsType<Conflict<string>>(result.Result);
    }

    [Fact]
    public async Task DeleteBundle_WhenBundledWithMapRows_Returns202WithJobId()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeManga("Bundled Series", library);
        ctx.Series.Add(manga);
        var vol = new VolumeMetadata(manga, 1);
        vol.ArchiveFileName = "Vol 1.cbz";
        ctx.VolumeMetadata.Add(vol);
        var ch = new SchemaChapter(manga, "1", 1) { IsBundled = true, FileName = null };
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        ctx.BundleChapterMaps.Add(new BundleChapterMap
        {
            VolumeKey = vol.Key,
            ChapterKey = ch.Key,
            StartPage = 0,
            PageCount = 10
        });
        await ctx.SaveChangesAsync();

        var (controller, workerQueueMock) = CreateController(ctx);
        var result = await controller.DeleteBundle(manga.Key, 1);

        var accepted = Assert.IsType<Accepted<UnbundleJobResult>>(result.Result);
        Assert.NotNull(accepted.Value);
        Assert.NotEmpty(accepted.Value!.JobId);
        Assert.Null(accepted.Value.Warning);
        workerQueueMock.Verify(q => q.AddWorker(It.IsAny<BaseWorker>()), Times.Once);
    }

    [Fact]
    public async Task DeleteBundle_WhenBundledWithNoMapRows_Returns202WithWarning()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeManga("Bundled No Map", library);
        ctx.Series.Add(manga);
        var vol = new VolumeMetadata(manga, 1);
        vol.ArchiveFileName = "Vol 1.cbz";
        ctx.VolumeMetadata.Add(vol);
        await ctx.SaveChangesAsync();

        var (controller, workerQueueMock) = CreateController(ctx);
        var result = await controller.DeleteBundle(manga.Key, 1);

        var accepted = Assert.IsType<Accepted<UnbundleJobResult>>(result.Result);
        Assert.NotNull(accepted.Value);
        Assert.NotNull(accepted.Value!.Warning);
        Assert.Contains("incomplete", accepted.Value.Warning, StringComparison.OrdinalIgnoreCase);
    }
}
