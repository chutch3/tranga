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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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
        ctx.Series.Add(manga);

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

    // ──────────────────────────────────────────────────────
    // PUT /libraryLayout
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task PutLibraryLayout_WhenMangaNotFound_Returns404()
    {
        using var ctx = CreateContext();
        var (controller, _) = CreateController(ctx);

        var result = await controller.PutLibraryLayout("nonexistent-id", new API.Controllers.Requests.PutLibraryLayoutRecord(API.Schema.SeriesContext.LibraryLayout.VolumeFolder));

        Assert.IsType<NotFound<string>>(result.Result);
    }

    [Fact]
    public async Task PutLibraryLayout_StoresPreferenceInDb()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Naruto", library);
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.PutLibraryLayout(manga.Key, new API.Controllers.Requests.PutLibraryLayoutRecord(API.Schema.SeriesContext.LibraryLayout.VolumeFolder));

        Assert.IsType<Ok<API.Controllers.DTOs.LibraryLayoutResult>>(result.Result);

        // Verify DB state persisted
        var updated = await ctx.Series.FindAsync(manga.Key);
        Assert.NotNull(updated);
        Assert.Equal(API.Schema.SeriesContext.LibraryLayout.VolumeFolder, updated!.LibraryLayout);
    }

    [Fact]
    public async Task PutLibraryLayout_ReturnsPreviewWithNewLayout()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Bleach", library);
        ctx.Series.Add(manga);

        // Chapter with volume 1 — under VolumeFolder layout, target path should contain "Vol 1"
        var ch = new SchemaChapter(manga, "1", 1);
        ch.FileName = ch.GetArchiveFileName(new TrangaSettings().ChapterNamingScheme); // flat path
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.PutLibraryLayout(manga.Key, new API.Controllers.Requests.PutLibraryLayoutRecord(API.Schema.SeriesContext.LibraryLayout.VolumeFolder));

        var ok = Assert.IsType<Ok<API.Controllers.DTOs.LibraryLayoutResult>>(result.Result);
        Assert.NotNull(ok.Value);
        // The layout field in the response should reflect what was set
        Assert.Equal("VolumeFolder", ok.Value!.Layout);
        // The preview should show moves (flat → Vol 1 subdir)
        Assert.NotNull(ok.Value.ReorganizePreview);
        Assert.Single(ok.Value.ReorganizePreview.Moves);
        Assert.Contains("Vol 1", ok.Value.ReorganizePreview.Moves[0].To);
    }

    [Fact]
    public async Task PutLibraryLayout_DoesNotMoveFiles()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("One Piece", library);
        ctx.Series.Add(manga);

        var ch = new SchemaChapter(manga, "1", 1);
        ch.FileName = "wrong.cbz";
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, workerQueueMock) = CreateController(ctx);
        await controller.PutLibraryLayout(manga.Key, new API.Controllers.Requests.PutLibraryLayoutRecord(API.Schema.SeriesContext.LibraryLayout.VolumeFolder));

        // No workers should be queued
        workerQueueMock.Verify(q => q.AddWorkers(It.IsAny<IEnumerable<BaseWorker>>()), Times.Never);
    }

    [Fact]
    public async Task GetReorganizePreview_WithVolumeFolderLayout_UsesSubdirectoryPaths()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Dragon Ball Z", library);
        manga.LibraryLayout = API.Schema.SeriesContext.LibraryLayout.VolumeFolder;
        ctx.Series.Add(manga);

        var ch = new SchemaChapter(manga, "1", 3);
        ch.FileName = "wrong.cbz"; // current flat path, triggers a move
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetReorganizePreview(manga.Key);

        var ok = Assert.IsType<Ok<ReorganizePreviewResult>>(result.Result);
        Assert.Single(ok.Value!.Moves);
        // Target path should go into a "Vol 3" subdirectory
        Assert.Contains("Vol 3", ok.Value.Moves[0].To);
    }

    [Fact]
    public async Task GetReorganizePreview_WithNullVolumeNumber_AlwaysFlatRegardlessOfLayout()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Vinland Saga", library);
        manga.LibraryLayout = API.Schema.SeriesContext.LibraryLayout.VolumeFolder;
        ctx.Series.Add(manga);

        // Chapter with null volume number — must not go into a volume subfolder
        var ch = new SchemaChapter(manga, "1", null);
        ch.FileName = "wrong.cbz";
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetReorganizePreview(manga.Key);

        var ok = Assert.IsType<Ok<ReorganizePreviewResult>>(result.Result);
        Assert.Single(ok.Value!.Moves);
        // Target path should be directly under manga dir, NOT in a Vol subfolder
        string toPath = ok.Value.Moves[0].To;
        string mangaDir = manga.FullDirectoryPath;
        string relative = Path.GetRelativePath(mangaDir, toPath);
        // relative path must have no directory separator (i.e., it's flat)
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), relative);
    }

    // ──────────────────────────────────────────────────────
    // GET /volumes — LibraryLayout in response
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetVolumes_IncludesLibraryLayoutInResponse()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Berserk", library);
        manga.LibraryLayout = API.Schema.SeriesContext.LibraryLayout.VolumeFolder;
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        var (controller, _) = CreateController(ctx);
        var result = await controller.GetVolumes(manga.Key);

        var ok = Assert.IsType<Ok<VolumeListResult>>(result.Result);
        Assert.Equal(API.Schema.SeriesContext.LibraryLayout.VolumeFolder, ok.Value!.Layout);
    }
}
