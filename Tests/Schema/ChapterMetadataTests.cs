using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace Tests.Schema;

public class ChapterMetadataTests
{
    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    private static Series MakeTestManga(string name = "Test Series")
        => new(name, "", "http://example.com/img.jpg", SeriesReleaseStatus.Continuing, [], [], [], []);

    [Fact]
    public void NewChapter_MetadataConfidence_IsNull_ByDefault()
    {
        var manga = MakeTestManga();
        var chapter = new Chapter(manga, "1", 1);

        Assert.Null(chapter.MetadataConfidence);
    }

    [Fact]
    public void NewChapter_IsBundled_IsFalse_ByDefault()
    {
        var manga = MakeTestManga();
        var chapter = new Chapter(manga, "1", 1);

        Assert.False(chapter.IsBundled);
    }

    [Fact]
    public void Chapter_MetadataConfidence_CanBeSet()
    {
        var manga = MakeTestManga();
        var chapter = new Chapter(manga, "1", 1);

        chapter.MetadataConfidence = MetadataConfidence.Exact;
        Assert.Equal(MetadataConfidence.Exact, chapter.MetadataConfidence);

        chapter.MetadataConfidence = MetadataConfidence.Heuristic;
        Assert.Equal(MetadataConfidence.Heuristic, chapter.MetadataConfidence);

        chapter.MetadataConfidence = MetadataConfidence.Manual;
        Assert.Equal(MetadataConfidence.Manual, chapter.MetadataConfidence);

        chapter.MetadataConfidence = null;
        Assert.Null(chapter.MetadataConfidence);
    }

    [Fact]
    public void Chapter_IsBundled_CanBeSet()
    {
        var manga = MakeTestManga();
        var chapter = new Chapter(manga, "1", 1);

        chapter.IsBundled = true;
        Assert.True(chapter.IsBundled);
    }

    [Fact]
    public async Task Chapter_MetadataConfidence_PersistsToDatabase()
    {
        await using var ctx = CreateContext();
        var manga = MakeTestManga("Berserk");
        var chapter = new Chapter(manga, "42", 7);
        chapter.MetadataConfidence = MetadataConfidence.Exact;
        chapter.IsBundled = true;

        ctx.Series.Add(manga);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        var loaded = await ctx.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.Equal(MetadataConfidence.Exact, loaded.MetadataConfidence);
        Assert.True(loaded.IsBundled);
    }
}
