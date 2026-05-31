using API;
using API.Controllers;
using API.Controllers.DTOs;
using API.Schema.SeriesContext;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SchemaManga = API.Schema.SeriesContext.Series;
using SchemaFileLibrary = API.Schema.SeriesContext.FileLibrary;
using SchemaChapter = API.Schema.SeriesContext.Chapter;

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

    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    private LibraryController CreateController(SeriesContext ctx)
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
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], [], library);

    // ──────────────────────────────────────────────────────
    // GET /v2/Library/unresolved
    // ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetUnresolved_ReturnsEmptyResult_WhenNoMangaHasIssues()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Perfect Series", library);
        ctx.Series.Add(manga);

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
        Assert.Empty(ok.Value!.Series);
    }

    [Fact]
    public async Task GetUnresolved_IncludesManga_WithUnresolvedChapters()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Unresolved Series", library);
        ctx.Series.Add(manga);

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
        Assert.Single(ok.Value!.Series);
        var entry = ok.Value.Series[0];
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
        var manga = MakeTestManga("Missing Files Series", library);
        ctx.Series.Add(manga);

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
        Assert.Single(ok.Value!.Series);
        var entry = ok.Value.Series[0];
        Assert.Equal(2, entry.MissingFileCount);
        Assert.Equal(0, entry.UnresolvedChapterCount);
    }

    [Fact]
    public async Task GetUnresolved_ExcludesManga_NotInLibrary()
    {
        using var ctx = CreateContext();

        // Series with no library (search result / not tracked)
        var manga = new SchemaManga("Search Result", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], []);
        ctx.Series.Add(manga);

        var ch = new SchemaChapter(manga, "1", null);
        ch.Downloaded = true;
        ch.FileName = "chapter1.cbz";
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx);
        var result = await controller.GetUnresolved();

        var ok = Assert.IsType<Ok<UnresolvedDashboardResult>>(result);
        Assert.Empty(ok.Value!.Series);
    }

    [Fact]
    public async Task GetUnresolved_ExcludesNotDownloadedChapters()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);
        var manga = MakeTestManga("Series With Not Downloaded", library);
        ctx.Series.Add(manga);

        // Not downloaded — should not count
        var ch = new SchemaChapter(manga, "1", null);
        ch.Downloaded = false;
        ctx.Chapters.Add(ch);
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx);
        var result = await controller.GetUnresolved();

        var ok = Assert.IsType<Ok<UnresolvedDashboardResult>>(result);
        Assert.Empty(ok.Value!.Series);
    }

    [Fact]
    public async Task GetUnresolved_MultipleManga_OnlyIncludesThoseWithIssues()
    {
        using var ctx = CreateContext();
        var library = MakeLibrary();
        ctx.FileLibraries.Add(library);

        var goodManga = MakeTestManga("Good Series", library);
        var badManga = MakeTestManga("Bad Series", library);
        ctx.Series.AddRange(goodManga, badManga);

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
        Assert.Single(ok.Value!.Series);
        Assert.Equal(badManga.Key, ok.Value.Series[0].MangaId);
    }
}
