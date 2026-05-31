using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace Tests.Schema;

public class MetadataSourceTests
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
    public void NewManga_InitializesMetadataSource_WithConnectorTypeAndUnlinkedStatus()
    {
        var manga = MakeTestManga("One Piece");

        Assert.NotNull(manga.MetadataSource);
        Assert.Equal(MetadataSourceType.Connector, manga.MetadataSource!.SourceType);
        Assert.Equal(MetadataSourceStatus.Unlinked, manga.MetadataSource!.Status);
        Assert.Null(manga.MetadataSource!.ExternalId);
        Assert.Null(manga.MetadataSource!.LastSyncedAt);
        Assert.Null(manga.MetadataSource!.MatchScore);
    }

    [Fact]
    public void NewManga_MetadataSource_HasCorrectMangaId()
    {
        var manga = MakeTestManga("Naruto");

        Assert.Equal(manga.Key, manga.MetadataSource!.MangaId);
    }

    [Fact]
    public async Task Manga_MetadataSource_PersistsToDatabase()
    {
        string dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        await using var ctx = new SeriesContext(options);
        var manga = MakeTestManga("Bleach");
        ctx.Series.Add(manga);
        await ctx.SaveChangesAsync();

        await using var ctx2 = new SeriesContext(options);
        var loaded = await ctx2.Series
            .Include(m => m.MetadataSource)
            .FirstAsync(m => m.Key == manga.Key);

        Assert.NotNull(loaded.MetadataSource);
        Assert.Equal(MetadataSourceType.Connector, loaded.MetadataSource!.SourceType);
        Assert.Equal(MetadataSourceStatus.Unlinked, loaded.MetadataSource!.Status);
    }

    [Fact]
    public void MetadataSource_CanUpdateFields()
    {
        var manga = MakeTestManga("Dragon Ball");
        var source = manga.MetadataSource!;

        source.SourceType = MetadataSourceType.MangaDex;
        source.ExternalId = "some-external-id";
        source.Status = MetadataSourceStatus.Confirmed;
        source.LastSyncedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        source.MatchScore = 0.95f;

        Assert.Equal(MetadataSourceType.MangaDex, source.SourceType);
        Assert.Equal("some-external-id", source.ExternalId);
        Assert.Equal(MetadataSourceStatus.Confirmed, source.Status);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), source.LastSyncedAt);
        Assert.Equal(0.95f, source.MatchScore);
    }
}
