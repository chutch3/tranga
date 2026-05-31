using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace API.Tests.Workers;

public class DownloadChapterFromSourceWorkerTests
{
    /// <summary>A MemoryStream that records whether it was disposed.</summary>
    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        public TrackingStream(byte[] buffer) : base(buffer.Length)
        {
            Write(buffer, 0, buffer.Length);
            Position = 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }

    private static byte[] CreateJpegBytes()
    {
        using var image = new Image<Rgba32>(8, 8);
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task DoWorkInternal_DisposesSourceImageStream_AfterProcessing()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tranga-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settings = new TrangaSettings { AppData = tempRoot, ImageCompression = 40 }; // 40 => processing path
            var libraryPath = Path.Combine(tempRoot, "library");
            Directory.CreateDirectory(libraryPath);

            var options = new DbContextOptionsBuilder<SeriesContext>()
                .UseInMemoryDatabase(databaseName: "DownloadWorkerDispose-" + Guid.NewGuid().ToString("N"))
                .Options;
            using var context = new SeriesContext(options);

            var library = new FileLibrary(libraryPath, "Test Lib");
            context.FileLibraries.Add(library);
            var manga = new Series("Test Series", "Desc", "http://cover.com/c.jpg", SeriesReleaseStatus.Continuing,
                new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
                library, 0f, 2024, "en");

            // Provide a cached cover so EnsureCoverInPublicationFolder does not make a network request.
            Directory.CreateDirectory(settings.CoverImageCacheOriginal);
            string cachedCover = "cached-cover.jpg";
            await File.WriteAllBytesAsync(Path.Combine(settings.CoverImageCacheOriginal, cachedCover), CreateJpegBytes());
            manga.CoverFileNameInCache = cachedCover;

            context.Series.Add(manga);
            var chapter = new Chapter(manga, "1", null, "Title");
            context.Chapters.Add(chapter);
            var connectorId = new SourceId<Chapter>(chapter, "MockConnector", "site1", "url1", true);
            context.MangaConnectorToChapter.Add(connectorId);
            await context.SaveChangesAsync();

            var mockConnector = new Mock<SeriesSource>("MockConnector", new[] { "en" }, new[] { "mock.com" }, "icon", settings);
            mockConnector.Setup(c => c.GetChapterImageUrls(It.IsAny<SourceId<Chapter>>()))
                .ReturnsAsync(["http://img/1.jpg"]);

            var sourceStream = new TrackingStream(CreateJpegBytes());
            mockConnector.Setup(c => c.DownloadImage(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(sourceStream);

            var services = new ServiceCollection();
            services.AddSingleton(context);
            services.AddDbContext<API.Schema.ActionsContext.ActionsContext>(o => o.UseInMemoryDatabase("Actions-" + Guid.NewGuid().ToString("N")));
            services.AddDbContext<API.Schema.NotificationsContext.NotificationsContext>(o => o.UseInMemoryDatabase("Notifications-" + Guid.NewGuid().ToString("N")));
            var serviceProvider = services.BuildServiceProvider();

            var worker = new DownloadChapterFromSourceWorker(connectorId, new[] { mockConnector.Object }, settings);

            await worker.DoWork(serviceProvider.CreateScope());

            var updated = await context.Chapters.FirstAsync(c => c.Key == chapter.Key);
            Assert.True(updated.Downloaded, "Chapter should be marked downloaded on the happy path.");
            Assert.True(sourceStream.Disposed, "The source image stream returned by DownloadImage must be disposed (no leak).");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DoWorkInternal_OnFailure_DoesNotMarkAsDownloaded()
    {
        // 1. Setup - Create a real DB but mock the Connector
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(databaseName: "DownloadWorkerFailure")
            .Options;

        using var context = new SeriesContext(options);
        
        var library = new FileLibrary("/tmp/manga", "Test Lib");
        context.FileLibraries.Add(library);
        
        var manga = new Series("Test Series", "Desc", "http://cover.com", SeriesReleaseStatus.Continuing, 
            new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
            library, 0f, 2024, "en");
        context.Series.Add(manga);
        
        var chapter = new Chapter(manga, "1", null, "Title");
        context.Chapters.Add(chapter);
        
        var connectorId = new SourceId<Chapter>(chapter, "MockConnector", "site1", "url1", true);
        context.MangaConnectorToChapter.Add(connectorId);
        await context.SaveChangesAsync();

        var settings = new TrangaSettings { AppData = "/tmp", ChapterNamingScheme = "%M - %C" };
        
        var mockConnector = new Mock<SeriesSource>("MockConnector", new[] { "en" }, new[] { "mock.com" }, "icon", settings);
        
        // Simulate a crash during image URL retrieval
        mockConnector.Setup(c => c.GetChapterImageUrls(It.IsAny<SourceId<Chapter>>()))
            .ThrowsAsync(new Exception("Network failure during image retrieval"));

        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddDbContext<API.Schema.ActionsContext.ActionsContext>(o => o.UseInMemoryDatabase("Actions"));
        services.AddDbContext<API.Schema.NotificationsContext.NotificationsContext>(o => o.UseInMemoryDatabase("Notifications"));

        var serviceProvider = services.BuildServiceProvider();

        var worker = new DownloadChapterFromSourceWorker(connectorId, new[] { mockConnector.Object }, settings);
        
        // 2. Act - Try to download
        await worker.DoWork(serviceProvider.CreateScope());

        // 3. Assert - The chapter should NOT be marked as downloaded
        var updatedChapter = await context.Chapters.FirstAsync(c => c.Key == chapter.Key);
        Assert.False(updatedChapter.Downloaded, "Chapter should NOT be marked as downloaded after a failure.");
    }
}
