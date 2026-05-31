using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Chapter = API.Schema.SeriesContext.Chapter;
using Series = API.Schema.SeriesContext.Series;
using SourceId = API.Schema.SeriesContext.SourceId<API.Schema.SeriesContext.Series>;
using ChapterConnectorId = API.Schema.SeriesContext.SourceId<API.Schema.SeriesContext.Chapter>;

namespace API.Tests.Workers;

public class RetrieveChaptersFromSourceWorkerTests : IDisposable
{
    private readonly Mock<IServiceScope> _mockScope;
    private readonly SeriesContext _mangaContext;
    private readonly ActionsContext _actionsContext;

    public RetrieveChaptersFromSourceWorkerTests()
    {
        var mangaOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _mangaContext = new SeriesContext(mangaOptions);

        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _actionsContext = new ActionsContext(actionsOptions);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(SeriesContext))).Returns(_mangaContext);
        serviceProvider.Setup(x => x.GetService(typeof(ActionsContext))).Returns(_actionsContext);

        _mockScope = new Mock<IServiceScope>();
        _mockScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);
    }

    public void Dispose()
    {
        _mangaContext.Dispose();
        _actionsContext.Dispose();
    }

    [Fact]
    public async Task DoWork_UpdatesExistingChapterWithMissingVolume()
    {
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], []);
        _mangaContext.Series.Add(manga);

        var mockConnector = new Mock<SeriesSource>("MangaDex", new[] { "en" }, new[] { "mangadex.org" }, "icon.png", new TrangaSettings());
        
        var mangaMcId = new SourceId(manga, "MangaDex", "manga-id", "url");
        manga.SourceIds.Add(mangaMcId);
        _mangaContext.MangaConnectorToManga.Add(mangaMcId);

        // Existing chapter with NO volume
        var existingChapter = new Chapter(manga, "1", null, "Title");
        var existingChMcId = new ChapterConnectorId(existingChapter, "MangaDex", "chap-1", "url");
        existingChapter.SourceIds.Add(existingChMcId);
        _mangaContext.Chapters.Add(existingChapter);
        _mangaContext.MangaConnectorToChapter.Add(existingChMcId);
        
        await _mangaContext.SaveChangesAsync();

        // Connector returns the SAME chapter but WITH a volume
        var fetchedChapter = new Chapter(manga, "1", 5, "Title");
        var fetchedChMcId = new ChapterConnectorId(fetchedChapter, "MangaDex", "chap-1", "url");
        fetchedChapter.SourceIds.Add(fetchedChMcId);

        mockConnector.Setup(c => c.GetChapters(It.IsAny<SourceId>(), It.IsAny<string>()))
            .ReturnsAsync([(fetchedChapter, fetchedChMcId)]);
        // Name is set via constructor parameter

        var worker = new RetrieveChaptersFromSourceWorker(mangaMcId, "en", new[] { mockConnector.Object });

        await worker.DoWork(_mockScope.Object);

        var chapterInDb = await _mangaContext.Chapters.FirstAsync();
        Assert.Equal(5, chapterInDb.VolumeNumber); // This will fail until we fix the worker
    }
}
