using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

public class DownloadChapterFromMangaconnectorWorkerTests
{
    [Fact]
    public async Task DoWorkInternal_OnFailure_DoesNotMarkAsDownloaded()
    {
        // 1. Setup - Create a real DB but mock the Connector
        var options = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(databaseName: "DownloadWorkerFailure")
            .Options;

        using var context = new MangaContext(options);
        
        var library = new FileLibrary("/tmp/manga", "Test Lib");
        context.FileLibraries.Add(library);
        
        var manga = new Manga("Test Manga", "Desc", "http://cover.com", MangaReleaseStatus.Continuing, 
            new List<Author>(), new List<MangaTag>(), new List<Link>(), new List<AltTitle>(),
            library, 0f, 2024, "en");
        context.Mangas.Add(manga);
        
        var chapter = new Chapter(manga, "1", null, "Title");
        context.Chapters.Add(chapter);
        
        var connectorId = new MangaConnectorId<Chapter>(chapter, "MockConnector", "site1", "url1", true);
        context.MangaConnectorToChapter.Add(connectorId);
        await context.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = "/tmp", ChapterNamingScheme = "%M - %C" };
        
        var mockConnector = new Mock<MangaConnector>("MockConnector", new[] { "en" }, new[] { "mock.com" }, "icon", settings);
        
        // Simulate a crash during image URL retrieval
        mockConnector.Setup(c => c.GetChapterImageUrls(It.IsAny<MangaConnectorId<Chapter>>()))
            .ThrowsAsync(new Exception("Network failure during image retrieval"));

        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddDbContext<API.Schema.ActionsContext.ActionsContext>(o => o.UseInMemoryDatabase("Actions"));
        services.AddDbContext<API.Schema.NotificationsContext.NotificationsContext>(o => o.UseInMemoryDatabase("Notifications"));

        var serviceProvider = services.BuildServiceProvider();

        var worker = new DownloadChapterFromMangaconnectorWorker(connectorId, new[] { mockConnector.Object }, settings);
        
        // 2. Act - Try to download
        await worker.DoWork(serviceProvider.CreateScope());

        // 3. Assert - The chapter should NOT be marked as downloaded
        var updatedChapter = await context.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.False(updatedChapter.Downloaded, "Chapter should NOT be marked as downloaded after a failure.");
    }
}
