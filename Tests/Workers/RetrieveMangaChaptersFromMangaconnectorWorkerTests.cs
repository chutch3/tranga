using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Chapter = API.Schema.MangaContext.Chapter;
using Manga = API.Schema.MangaContext.Manga;
using MangaConnectorId = API.Schema.MangaContext.MangaConnectorId<API.Schema.MangaContext.Manga>;
using ChapterConnectorId = API.Schema.MangaContext.MangaConnectorId<API.Schema.MangaContext.Chapter>;

namespace API.Tests.Workers;

public class RetrieveMangaChaptersFromMangaconnectorWorkerTests : IDisposable
{
    private readonly Mock<IServiceScope> _mockScope;
    private readonly MangaContext _mangaContext;
    private readonly ActionsContext _actionsContext;

    public RetrieveMangaChaptersFromMangaconnectorWorkerTests()
    {
        var mangaOptions = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _mangaContext = new MangaContext(mangaOptions);

        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _actionsContext = new ActionsContext(actionsOptions);

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(MangaContext))).Returns(_mangaContext);
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
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], []);
        _mangaContext.Mangas.Add(manga);

        var mockConnector = new Mock<MangaConnector>("MangaDex", new[] { "en" }, new[] { "mangadex.org" }, "icon.png", new TrangaSettings());
        
        var mangaMcId = new MangaConnectorId(manga, "MangaDex", "manga-id", "url");
        manga.MangaConnectorIds.Add(mangaMcId);
        _mangaContext.MangaConnectorToManga.Add(mangaMcId);

        // Existing chapter with NO volume
        var existingChapter = new Chapter(manga, "1", null, "Title");
        var existingChMcId = new ChapterConnectorId(existingChapter, "MangaDex", "chap-1", "url");
        existingChapter.MangaConnectorIds.Add(existingChMcId);
        _mangaContext.Chapters.Add(existingChapter);
        _mangaContext.MangaConnectorToChapter.Add(existingChMcId);
        
        await _mangaContext.SaveChangesAsync();

        // Connector returns the SAME chapter but WITH a volume
        var fetchedChapter = new Chapter(manga, "1", 5, "Title");
        var fetchedChMcId = new ChapterConnectorId(fetchedChapter, "MangaDex", "chap-1", "url");
        fetchedChapter.MangaConnectorIds.Add(fetchedChMcId);

        mockConnector.Setup(c => c.GetChapters(It.IsAny<MangaConnectorId>(), It.IsAny<string>()))
            .Returns([(fetchedChapter, fetchedChMcId)]);
        // Name is set via constructor parameter

        var worker = new RetrieveMangaChaptersFromMangaconnectorWorker(mangaMcId, "en", new[] { mockConnector.Object });

        await worker.DoWork(_mockScope.Object);

        var chapterInDb = await _mangaContext.Chapters.FirstAsync();
        Assert.Equal(5, chapterInDb.VolumeNumber); // This will fail until we fix the worker
    }
}
