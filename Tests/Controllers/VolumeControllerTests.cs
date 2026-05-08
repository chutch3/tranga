using API;
using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.MangaContext;
using API.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SchemaManga = API.Schema.MangaContext.Manga;
using SchemaFileLibrary = API.Schema.MangaContext.FileLibrary;
using SchemaChapter = API.Schema.MangaContext.Chapter;

namespace API.Tests.Controllers;

public class VolumeControllerTests : IDisposable
{
    private readonly string _tempDir;

    public VolumeControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private MangaContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new MangaContext(options);
    }

    private (VolumeController controller, Mock<IWorkerQueue> workerQueueMock) CreateController(MangaContext ctx)
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
        => new(name, "", "http://example.com/img.jpg", MangaReleaseStatus.Continuing, [], [], [], [], library);

    // ──────────────────────────────────────────────────────
    // GET /volumes
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetVolumes_WhenMangaNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var (controller, _) = CreateController(ctx);

        var result = await controller.GetVolumes("nonexistent-id");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task GetVolumes_ReturnsChaptersGroupedByVolume()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("One Piece", library);
        ctx.Mangas.Add(manga);

        var ch1 = new SchemaChapter(manga,"1", 1);
        ch1.FileName = ch1.GetArchiveFileName(new TrangaSettings().ChapterNamingScheme);
        var ch2 = new SchemaChapter(manga,"2", 1);
        ch2.FileName = ch2.GetArchiveFileName(new TrangaSettings().ChapterNamingScheme);
        var ch3 = new SchemaChapter(manga,"10", 2);
        ch3.FileName = ch3.GetArchiveFileName(new TrangaSettings().ChapterNamingScheme);
        ctx.Chapters.AddRange(ch1, ch2, ch3);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetVolumes(manga.Key);

        var ok = Assert.IsType<Ok<VolumeListResult>>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.Equal(2, ok.Value!.Volumes.Count);

        var vol1 = ok.Value.Volumes.First(v => v.VolumeNumber == 1);
        Assert.Equal(2, vol1.ChapterCount);
        Assert.Equal(2, vol1.Chapters.Count);

        var vol2 = ok.Value.Volumes.First(v => v.VolumeNumber == 2);
        Assert.Equal(1, vol2.ChapterCount);
    }

    [Fact]
    public async Task GetVolumes_FilesNeedReorganizing_CountsOnlyMismatchedFileNames()
    {
        // No disk files created — verifies it's a pure string comparison
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Berserk", library);
        ctx.Mangas.Add(manga);

        var settings = new TrangaSettings();
        var chGood = new SchemaChapter(manga,"1", 1);
        chGood.FileName = chGood.GetArchiveFileName(settings.ChapterNamingScheme); // correct name
        var chBad = new SchemaChapter(manga,"2", 1);
        chBad.FileName = "wrong_name.cbz"; // mismatched

        ctx.Chapters.AddRange(chGood, chBad);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetVolumes(manga.Key);

        var ok = Assert.IsType<Ok<VolumeListResult>>(result.Result);
        Assert.Equal(1, ok.Value!.FilesNeedReorganizing);
    }

    [Fact]
    public async Task GetVolumes_UnassignedChapters_IncludedInUnassigned()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Naruto", library);
        ctx.Mangas.Add(manga);

        var chAssigned = new SchemaChapter(manga,"1", 1);
        chAssigned.FileName = chAssigned.GetArchiveFileName(new TrangaSettings().ChapterNamingScheme);
        var chUnassigned = new SchemaChapter(manga,"99", null); // no volume
        chUnassigned.FileName = chUnassigned.GetArchiveFileName(new TrangaSettings().ChapterNamingScheme);

        ctx.Chapters.AddRange(chAssigned, chUnassigned);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetVolumes(manga.Key);

        var ok = Assert.IsType<Ok<VolumeListResult>>(result.Result);
        Assert.Single(ok.Value!.Volumes);
        Assert.Single(ok.Value.Unassigned);
        Assert.Equal(chUnassigned.Key, ok.Value.Unassigned[0].ChapterId);
    }

    [Fact]
    public async Task GetVolumes_BundledChapters_CorrectlyTagged()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Bleach", library);
        ctx.Mangas.Add(manga);

        var ch = new SchemaChapter(manga,"1", 1);
        ch.FileName = ch.GetArchiveFileName(new TrangaSettings().ChapterNamingScheme);
        ch.IsBundled = true;
        ctx.Chapters.Add(ch);

        var volMeta = new VolumeMetadata(manga, 1);
        volMeta.ArchiveFileName = "Bleach Vol.1.cbz"; // non-null means bundled
        ctx.VolumeMetadata.Add(volMeta);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetVolumes(manga.Key);

        var ok = Assert.IsType<Ok<VolumeListResult>>(result.Result);
        var vol1 = ok.Value!.Volumes.Single(v => v.VolumeNumber == 1);
        Assert.True(vol1.IsBundled);
        Assert.NotNull(vol1.ArchiveFileName);
        Assert.True(vol1.Chapters[0].IsBundled);
    }

    [Fact]
    public async Task GetVolumes_VolumeMetadata_TitleIncluded()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Vinland Saga", library);
        ctx.Mangas.Add(manga);

        var ch = new SchemaChapter(manga,"1", 3);
        ch.FileName = ch.GetArchiveFileName(new TrangaSettings().ChapterNamingScheme);
        ctx.Chapters.Add(ch);

        var volMeta = new VolumeMetadata(manga, 3, "Slave");
        ctx.VolumeMetadata.Add(volMeta);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetVolumes(manga.Key);

        var ok = Assert.IsType<Ok<VolumeListResult>>(result.Result);
        var vol3 = ok.Value!.Volumes.Single(v => v.VolumeNumber == 3);
        Assert.Equal("Slave", vol3.Title);
    }

    // ──────────────────────────────────────────────────────
    // GET /reorganize/preview
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetReorganizePreview_WhenMangaNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var (controller, _) = CreateController(ctx);

        var result = await controller.GetReorganizePreview("nonexistent-id");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task GetReorganizePreview_WhenAllFilesCorrect_ReturnsEmptyLists()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Fullmetal Alchemist", library);
        ctx.Mangas.Add(manga);

        var settings = new TrangaSettings();
        var ch = new SchemaChapter(manga,"1", 1);
        ch.FileName = ch.GetArchiveFileName(settings.ChapterNamingScheme);
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetReorganizePreview(manga.Key);

        var ok = Assert.IsType<Ok<ReorganizePreviewResult>>(result.Result);
        Assert.Empty(ok.Value!.Moves);
        Assert.Empty(ok.Value.Creates);
        Assert.Empty(ok.Value.Deletes);
    }

    [Fact]
    public async Task GetReorganizePreview_WhenFilesMisplaced_ReturnsMoveList()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Dragon Ball", library);
        ctx.Mangas.Add(manga);

        var ch = new SchemaChapter(manga,"5", 2);
        ch.FileName = "wrong_name.cbz"; // does not match computed name
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetReorganizePreview(manga.Key);

        var ok = Assert.IsType<Ok<ReorganizePreviewResult>>(result.Result);
        Assert.Single(ok.Value!.Moves);
        Assert.EndsWith("wrong_name.cbz", ok.Value.Moves[0].From);
    }

    [Fact]
    public async Task GetReorganizePreview_SkipsBundledChapters()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Attack on Titan", library);
        ctx.Mangas.Add(manga);

        var ch = new SchemaChapter(manga,"1", 1);
        ch.FileName = "wrong_name.cbz";
        ch.IsBundled = true; // bundled — skip
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetReorganizePreview(manga.Key);

        var ok = Assert.IsType<Ok<ReorganizePreviewResult>>(result.Result);
        Assert.Empty(ok.Value!.Moves);
    }

    // ──────────────────────────────────────────────────────
    // POST /reorganize
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task PostReorganize_WhenMangaNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var (controller, _) = CreateController(ctx);

        var result = await controller.PostReorganize("nonexistent-id");

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task PostReorganize_WhenFilesNeedMoving_QueuesRenameWorkers()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Hunter x Hunter", library);
        ctx.Mangas.Add(manga);

        var ch1 = new SchemaChapter(manga,"1", 1);
        ch1.FileName = "wrong1.cbz";
        var ch2 = new SchemaChapter(manga,"2", 1);
        ch2.FileName = "wrong2.cbz";
        ctx.Chapters.AddRange(ch1, ch2);
        await ctx.SaveChangesAsync();

        var (controller, workerQueueMock) = CreateController(ctx);
        var result = await controller.PostReorganize(manga.Key);

        var accepted = Assert.IsType<Accepted<ReorganizeJobResult>>(result.Result);
        Assert.NotNull(accepted.Value);
        Assert.NotNull(accepted.Value!.JobId);
        workerQueueMock.Verify(q => q.AddWorkers(It.IsAny<IEnumerable<BaseWorker>>()), Times.Once);
    }

    [Fact]
    public async Task PostReorganize_WhenNothingToMove_Returns200WithEmptyResult()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Fairy Tail", library);
        ctx.Mangas.Add(manga);

        var settings = new TrangaSettings();
        var ch = new SchemaChapter(manga,"1", 1);
        ch.FileName = ch.GetArchiveFileName(settings.ChapterNamingScheme);
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, workerQueueMock) = CreateController(ctx);
        var result = await controller.PostReorganize(manga.Key);

        var ok = Assert.IsType<Ok<ReorganizeJobResult>>(result.Result);
        Assert.NotNull(ok.Value);
        workerQueueMock.Verify(q => q.AddWorkers(It.IsAny<IEnumerable<BaseWorker>>()), Times.Never);
    }
}
