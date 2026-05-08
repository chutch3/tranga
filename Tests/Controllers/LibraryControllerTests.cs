using API;
using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.MangaContext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchemaManga = API.Schema.MangaContext.Manga;
using SchemaFileLibrary = API.Schema.MangaContext.FileLibrary;
using SchemaChapter = API.Schema.MangaContext.Chapter;

namespace API.Tests.Controllers;

public class LibraryControllerTests : IDisposable
{
    private readonly string _tempDir;

    public LibraryControllerTests()
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

    private LibraryController CreateController(MangaContext ctx)
    {
        var controller = new LibraryController(ctx);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
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
    // GET /v2/Library/unresolved
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetUnresolved_ReturnsEmptyResult_WhenNoMangaHasIssues()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Perfect Manga", library);
        ctx.Mangas.Add(manga);

        // Chapter downloaded with volume number set — no issues
        var ch = new SchemaChapter(manga, "1", 1);
        ch.Downloaded = true;
        ch.FileName = "chapter1.cbz";
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx);
        var result = await controller.GetUnresolved();

        var ok = Assert.IsType<Ok<UnresolvedDashboardResult>>(result);
        Assert.NotNull(ok.Value);
        Assert.Empty(ok.Value!.Manga);
    }

    [Fact]
    public async Task GetUnresolved_IncludesManga_WithUnresolvedChapters()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Unresolved Manga", library);
        ctx.Mangas.Add(manga);

        // Downloaded but no volume number — unresolved
        var ch1 = new SchemaChapter(manga, "1", null);
        ch1.Downloaded = true;
        ch1.FileName = "chapter1.cbz";

        var ch2 = new SchemaChapter(manga, "2", null);
        ch2.Downloaded = true;
        ch2.FileName = "chapter2.cbz";

        // Downloaded with volume — not unresolved
        var ch3 = new SchemaChapter(manga, "3", 1);
        ch3.Downloaded = true;
        ch3.FileName = "chapter3.cbz";

        ctx.Chapters.AddRange(ch1, ch2, ch3);
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx);
        var result = await controller.GetUnresolved();

        var ok = Assert.IsType<Ok<UnresolvedDashboardResult>>(result);
        Assert.Single(ok.Value!.Manga);
        var entry = ok.Value.Manga[0];
        Assert.Equal(manga.Key, entry.MangaId);
        Assert.Equal(manga.Name, entry.MangaName);
        Assert.Equal(2, entry.UnresolvedChapterCount);
        Assert.Equal(0, entry.MissingFileCount);
    }

    [Fact]
    public async Task GetUnresolved_CorrectMissingFileCount()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Missing Files Manga", library);
        ctx.Mangas.Add(manga);

        // Downloaded but FileName is null — broken/missing
        var ch1 = new SchemaChapter(manga, "1", 1);
        ch1.Downloaded = true;
        // FileName stays null

        var ch2 = new SchemaChapter(manga, "2", 1);
        ch2.Downloaded = true;
        // FileName stays null

        // Downloaded with FileName — good
        var ch3 = new SchemaChapter(manga, "3", 1);
        ch3.Downloaded = true;
        ch3.FileName = "chapter3.cbz";

        ctx.Chapters.AddRange(ch1, ch2, ch3);
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx);
        var result = await controller.GetUnresolved();

        var ok = Assert.IsType<Ok<UnresolvedDashboardResult>>(result);
        Assert.Single(ok.Value!.Manga);
        var entry = ok.Value.Manga[0];
        Assert.Equal(2, entry.MissingFileCount);
        Assert.Equal(0, entry.UnresolvedChapterCount);
    }

    [Fact]
    public async Task GetUnresolved_ExcludesManga_NotInLibrary()
    {
        using var ctx = CreateContext();

        // Manga with no library (search result / not tracked)
        var manga = new SchemaManga("Search Result", "", "http://example.com/img.jpg",
            MangaReleaseStatus.Continuing, [], [], [], []);
        ctx.Mangas.Add(manga);

        var ch = new SchemaChapter(manga, "1", null);
        ch.Downloaded = true;
        ch.FileName = "chapter1.cbz";
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx);
        var result = await controller.GetUnresolved();

        var ok = Assert.IsType<Ok<UnresolvedDashboardResult>>(result);
        Assert.Empty(ok.Value!.Manga);
    }

    [Fact]
    public async Task GetUnresolved_ExcludesNotDownloadedChapters()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Manga With Not Downloaded", library);
        ctx.Mangas.Add(manga);

        // Not downloaded — should not count
        var ch = new SchemaChapter(manga, "1", null);
        ch.Downloaded = false;
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx);
        var result = await controller.GetUnresolved();

        var ok = Assert.IsType<Ok<UnresolvedDashboardResult>>(result);
        Assert.Empty(ok.Value!.Manga);
    }

    [Fact]
    public async Task GetUnresolved_MultipleManga_OnlyIncludesThoseWithIssues()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);

        var goodManga = MakeTestManga("Good Manga", library);
        var badManga = MakeTestManga("Bad Manga", library);
        ctx.Mangas.AddRange(goodManga, badManga);

        // Good manga — all resolved
        var ch1 = new SchemaChapter(goodManga, "1", 1);
        ch1.Downloaded = true;
        ch1.FileName = "chapter1.cbz";

        // Bad manga — unresolved chapter
        var ch2 = new SchemaChapter(badManga, "1", null);
        ch2.Downloaded = true;
        ch2.FileName = "chapter1.cbz";

        ctx.Chapters.AddRange(ch1, ch2);
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx);
        var result = await controller.GetUnresolved();

        var ok = Assert.IsType<Ok<UnresolvedDashboardResult>>(result);
        Assert.Single(ok.Value!.Manga);
        Assert.Equal(badManga.Key, ok.Value.Manga[0].MangaId);
    }
}
