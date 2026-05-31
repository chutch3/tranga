using API;
using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace API.Tests.Schema;

public class ChapterTests : IDisposable
{
    private readonly string _tmpDir;

    public ChapterTests()
    {
        _tmpDir = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose() => Directory.Delete(_tmpDir, true);

    private SeriesContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SeriesContext(options);
    }

    [Fact]
    public void GetArchiveFileName_VolumeSubdirectoryScheme_IncludesSubdirectory()
    {
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], []);
        var chapter = new Chapter(manga, "1", 1);

        Assert.Equal("Dandadan Vol 1/Dandadan - Ch.1.cbz",
            chapter.GetArchiveFileName("?V(%M Vol %V/)%M - Ch.%C"));
    }

    [Fact]
    public void GetArchiveFileName_NullVolume_OmitsNullableVolumeSection()
    {
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], []);
        var chapter = new Chapter(manga, "1", null);

        Assert.Equal("Dandadan - Ch.1.cbz",
            chapter.GetArchiveFileName("?V(%M Vol %V/)%M - Ch.%C"));
    }

    [Fact]
    public void GetArchiveFileName_WithTitle_IncludesTitleSection()
    {
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], []);
        var chapter = new Chapter(manga, "1", 1, "Dragon Dance");

        Assert.Equal("Dandadan - Ch.1 - Dragon Dance.cbz",
            chapter.GetArchiveFileName("%M - Ch.%C?T( - %T)"));
    }

    [Fact]
    public void GetArchiveFileName_NullTitle_OmitsTitleSection()
    {
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], []);
        var chapter = new Chapter(manga, "1", 1);

        Assert.Equal("Dandadan - Ch.1.cbz",
            chapter.GetArchiveFileName("%M - Ch.%C?T( - %T)"));
    }

    [Fact]
    public void GetArchiveFileName_FlatScheme_NoSubdirectory()
    {
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], []);
        var chapter = new Chapter(manga, "1", 1);

        Assert.Equal("Dandadan - Ch.1.cbz",
            chapter.GetArchiveFileName("%M - Ch.%C"));
    }

    [Fact]
    public void GetArchiveFileName_SlashInMangaName_SlashStrippedFromValueNotSeparator()
    {
        var manga = new Series("A/B", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], []);
        var chapter = new Chapter(manga, "1", 1);

        Assert.Equal("AB Vol 1/AB - Ch.1.cbz",
            chapter.GetArchiveFileName("?V(%M Vol %V/)%M - Ch.%C"));
    }

    [Fact]
    public void GetFullFilepath_WhenFileNameAlreadySet_UsesStoredNameNotScheme()
    {
        var library = new FileLibrary(_tmpDir, "Test");
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var chapter = new Chapter(manga, "1", 1);
        chapter.FileName = "Vol 1/Dandadan - Ch.1.cbz";

        string? fullPath = chapter.GetFullFilepath("irrelevant scheme");

        Assert.NotNull(fullPath);
        Assert.Equal("Vol 1/Dandadan - Ch.1.cbz",
            Path.GetRelativePath(manga.FullDirectoryPath, fullPath));
    }

    [Fact]
    public void GetFullFilepath_VolumeSubdirectoryScheme_IncludesSubdirectoryInFullPath()
    {
        var library = new FileLibrary(_tmpDir, "Test");
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var chapter = new Chapter(manga, "1", 1);

        string? fullPath = chapter.GetFullFilepath("?V(%M Vol %V/)%M - Ch.%C");

        Assert.NotNull(fullPath);
        Assert.Equal("Dandadan Vol 1/Dandadan - Ch.1.cbz",
            Path.GetRelativePath(manga.FullDirectoryPath, fullPath));
    }

    [Fact]
    public async Task CheckDownloaded_NullLibrary_ReturnsFalse()
    {
        using var context = CreateContext();

        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], []);
        var chapter = new Chapter(manga, "1", 1);

        context.Series.Add(manga);
        context.Chapters.Add(chapter);
        await context.SaveChangesAsync();

        bool result = await chapter.CheckDownloaded(context, "%M - Ch.%C");

        Assert.False(result);
        Assert.False(chapter.Downloaded);
    }

    [Fact]
    public async Task CheckDownloaded_FileInSubdirectory_FuzzyMatchFindsFileRecursivelyAndStoresRelativePath()
    {
        using var context = CreateContext();

        var library = new FileLibrary(_tmpDir, "Test");
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var chapter = new Chapter(manga, "1", 1);

        context.FileLibraries.Add(library);
        context.Series.Add(manga);
        context.Chapters.Add(chapter);
        await context.SaveChangesAsync();

        string mangaDir = Path.Join(_tmpDir, manga.DirectoryName);
        Directory.CreateDirectory(Path.Join(mangaDir, "Vol 1"));
        await File.WriteAllTextAsync(Path.Join(mangaDir, "Vol 1", "Dandadan - Ch.1.cbz"), "dummy");

        await chapter.CheckDownloaded(context, "?V(%M Vol %V/)%M - Ch.%C", exactMatch: false);

        Assert.True(chapter.Downloaded);
        Assert.Equal("Vol 1/Dandadan - Ch.1.cbz", chapter.FileName);
    }

    [Fact]
    public async Task CheckDownloaded_FileAtSubdirectoryPath_StoresRelativePath()
    {
        using var context = CreateContext();

        var library = new FileLibrary(_tmpDir, "Test");
        var manga = new Series("Dandadan", "", "http://example.com/img.jpg",
            SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var chapter = new Chapter(manga, "1", 1);
        chapter.FileName = "Dandadan Vol 1/Dandadan - Ch.1.cbz";

        context.FileLibraries.Add(library);
        context.Series.Add(manga);
        context.Chapters.Add(chapter);
        await context.SaveChangesAsync();

        string filePath = Path.Join(_tmpDir, manga.DirectoryName, "Dandadan Vol 1", "Dandadan - Ch.1.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "dummy");

        await chapter.CheckDownloaded(context, "?V(%M Vol %V/)%M - Ch.%C");

        Assert.True(chapter.Downloaded);
        Assert.Equal("Dandadan Vol 1/Dandadan - Ch.1.cbz", chapter.FileName);
    }
}
