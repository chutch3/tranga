using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace Tests.Schema;

public class VolumeMetadataTests
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
    public void VolumeMetadata_Constructor_SetsFieldsCorrectly()
    {
        var manga = MakeTestManga("Berserk");
        var vol = new VolumeMetadata(manga, 3, "Conviction");

        Assert.Equal(manga.Key, vol.MangaId);
        Assert.Same(manga, vol.Series);
        Assert.Equal(3, vol.VolumeNumber);
        Assert.Equal("Conviction", vol.Title);
        Assert.Null(vol.ArchiveFileName);
        Assert.NotNull(vol.Key);
    }

    [Fact]
    public void VolumeMetadata_Constructor_NullTitle_IsAllowed()
    {
        var manga = MakeTestManga("Berserk");
        var vol = new VolumeMetadata(manga, 1);

        Assert.Null(vol.Title);
        Assert.Equal(1, vol.VolumeNumber);
    }

    [Fact]
    public void VolumeMetadata_Key_IsDeterministic()
    {
        var manga = MakeTestManga("One Piece");
        var vol1 = new VolumeMetadata(manga, 5, "East Blue");
        var vol2 = new VolumeMetadata(manga, 5, "Different Title");

        // Same manga + same volume number → same key regardless of title
        Assert.Equal(vol1.Key, vol2.Key);
    }

    [Fact]
    public void VolumeMetadata_DifferentVolumes_HaveDifferentKeys()
    {
        var manga = MakeTestManga("Naruto");
        var vol1 = new VolumeMetadata(manga, 1);
        var vol2 = new VolumeMetadata(manga, 2);

        Assert.NotEqual(vol1.Key, vol2.Key);
    }

    [Fact]
    public async Task VolumeMetadata_PersistsToDatabase()
    {
        string dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        string volKey;
        await using (var ctx = new SeriesContext(options))
        {
            var manga = MakeTestManga("Bleach");
            ctx.Series.Add(manga);
            var vol = new VolumeMetadata(manga, 1, "Substitute Shinigami");
            ctx.VolumeMetadata.Add(vol);
            await ctx.SaveChangesAsync();
            volKey = vol.Key;
        }

        await using var ctx2 = new SeriesContext(options);
        var loaded = await ctx2.VolumeMetadata.FirstAsync(v => v.Key == volKey);
        Assert.Equal(1, loaded.VolumeNumber);
        Assert.Equal("Substitute Shinigami", loaded.Title);
    }
}
