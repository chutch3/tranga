using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace Tests.Schema;

public class BundleChapterMapTests
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
    public void BundleChapterMap_ObjectInitializer_SetsAllFields()
    {
        var map = new BundleChapterMap
        {
            VolumeKey = "vol-key-123",
            ChapterKey = "ch-key-456",
            StartPage = 0,
            PageCount = 42
        };

        Assert.Equal("vol-key-123", map.VolumeKey);
        Assert.Equal("ch-key-456", map.ChapterKey);
        Assert.Equal(0, map.StartPage);
        Assert.Equal(42, map.PageCount);
    }

    [Fact]
    public async Task BundleChapterMap_PersistsToDatabaseWithCompositePK()
    {
        string dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        string volKey, chKey;
        await using (var ctx = new SeriesContext(options))
        {
            var manga = MakeTestManga("Berserk");
            ctx.Series.Add(manga);
            var vol = new VolumeMetadata(manga, 1, "Black Swordsman");
            ctx.VolumeMetadata.Add(vol);
            var chapter = new Chapter(manga, "1", 1, "The Black Swordsman");
            ctx.Chapters.Add(chapter);
            await ctx.SaveChangesAsync();

            volKey = vol.Key;
            chKey = chapter.Key;

            var map = new BundleChapterMap
            {
                VolumeKey = volKey,
                ChapterKey = chKey,
                StartPage = 0,
                PageCount = 30
            };
            ctx.BundleChapterMaps.Add(map);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = new SeriesContext(options);
        var loaded = await ctx2.BundleChapterMaps
            .FirstAsync(m => m.VolumeKey == volKey && m.ChapterKey == chKey);
        Assert.Equal(0, loaded.StartPage);
        Assert.Equal(30, loaded.PageCount);
    }

    [Fact]
    public async Task BundleChapterMap_NoDuplicateCompositePK_ThrowsOnAdd()
    {
        await using var ctx = CreateContext();

        var manga = MakeTestManga("Bleach");
        ctx.Series.Add(manga);
        var vol = new VolumeMetadata(manga, 1);
        ctx.VolumeMetadata.Add(vol);
        var chapter = new Chapter(manga, "1", 1);
        ctx.Chapters.Add(chapter);
        await ctx.SaveChangesAsync();

        ctx.BundleChapterMaps.Add(new BundleChapterMap
        {
            VolumeKey = vol.Key,
            ChapterKey = chapter.Key,
            StartPage = 0,
            PageCount = 20
        });

        // Adding a second entity with the same composite PK throws at tracking time
        Assert.Throws<InvalidOperationException>(() =>
            ctx.BundleChapterMaps.Add(new BundleChapterMap
            {
                VolumeKey = vol.Key,
                ChapterKey = chapter.Key,
                StartPage = 20,
                PageCount = 20
            })
        );
    }
}
