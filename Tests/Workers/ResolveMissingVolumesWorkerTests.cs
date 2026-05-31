using System.Collections.Concurrent;
using API;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

public class ResolveMissingVolumesWorkerTests : IDisposable
{
    private readonly SeriesContext _mangaContext;
    private readonly ActionsContext _actionsContext;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IBatchWorkerFactory<string>> _mockFactory;

    public ResolveMissingVolumesWorkerTests()
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

        _mockFactory = new Mock<IBatchWorkerFactory<string>>();
        _mockFactory.Setup(f => f.Create(It.IsAny<ConcurrentQueue<string>>()))
            .Returns(() => new CleanupOrphanedFilesWorker(false));
    }

    public void Dispose()
    {
        _mangaContext.Dispose();
        _actionsContext.Dispose();
    }

    private ResolveMissingVolumesWorker MakeCoordinator(TrangaSettings settings) =>
        new(settings, _mockFactory.Object);

    [Fact]
    public async Task DoWork_WhenStrategyDisabled_ReturnsEmpty_AndFactoryNeverCalled()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.Disabled };

        var result = await MakeCoordinator(settings).DoWork(_mockScope.Object);

        Assert.Empty(result);
        _mockFactory.Verify(f => f.Create(It.IsAny<ConcurrentQueue<string>>()), Times.Never);
    }

    [Fact]
    public async Task DoWork_WhenNoChaptersMissingVolumes_ReturnsEmpty_AndFactoryNeverCalled()
    {
        var settings = new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly };
        var library = new FileLibrary("/tmp/test", "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", 1, "Title") { Downloaded = true, FileName = "ch1.cbz" });
        await _mangaContext.SaveChangesAsync();

        var result = await MakeCoordinator(settings).DoWork(_mockScope.Object);

        Assert.Empty(result);
        _mockFactory.Verify(f => f.Create(It.IsAny<ConcurrentQueue<string>>()), Times.Never);
    }

    [Fact]
    public async Task DoWork_WhenOneMangaWithHighParallelism_ReturnsOneWorker()
    {
        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly,
            VolumeResolutionParallelism = 3
        };
        var library = new FileLibrary("/tmp/test", "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "Title") { Downloaded = true, FileName = "ch1.cbz" });
        await _mangaContext.SaveChangesAsync();

        var result = await MakeCoordinator(settings).DoWork(_mockScope.Object);

        // 1 manga, parallelism=3 → capped to min(3,1)=1 worker
        Assert.Single(result);
        _mockFactory.Verify(f => f.Create(It.IsAny<ConcurrentQueue<string>>()), Times.Once);
    }

    [Fact]
    public async Task DoWork_WhenThreeMangaWithParallelism2_ReturnsTwoWorkers()
    {
        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly,
            VolumeResolutionParallelism = 2
        };
        var library = new FileLibrary("/tmp/test", "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga1 = new Series("Series 1", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var manga2 = new Series("Series 2", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var manga3 = new Series("Series 3", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.AddRange(manga1, manga2, manga3);
        _mangaContext.Chapters.Add(new Chapter(manga1, "1", null, "T") { Downloaded = true, FileName = "c1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga2, "1", null, "T") { Downloaded = true, FileName = "c1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga3, "1", null, "T") { Downloaded = true, FileName = "c1.cbz" });
        await _mangaContext.SaveChangesAsync();

        var result = await MakeCoordinator(settings).DoWork(_mockScope.Object);

        // 3 manga, parallelism=2 → min(2,3)=2 workers
        Assert.Equal(2, result.Length);
        _mockFactory.Verify(f => f.Create(It.IsAny<ConcurrentQueue<string>>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DoWork_AllWorkersShareSameQueueInstance()
    {
        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly,
            VolumeResolutionParallelism = 3
        };
        var library = new FileLibrary("/tmp/test", "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga1 = new Series("Series A", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var manga2 = new Series("Series B", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.AddRange(manga1, manga2);
        _mangaContext.Chapters.Add(new Chapter(manga1, "1", null, "T") { Downloaded = true, FileName = "c1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga2, "1", null, "T") { Downloaded = true, FileName = "c1.cbz" });
        await _mangaContext.SaveChangesAsync();

        var capturedQueues = new List<ConcurrentQueue<string>>();
        _mockFactory.Setup(f => f.Create(It.IsAny<ConcurrentQueue<string>>()))
            .Callback<ConcurrentQueue<string>>(q => capturedQueues.Add(q))
            .Returns(() => new CleanupOrphanedFilesWorker(false));

        await MakeCoordinator(settings).DoWork(_mockScope.Object);

        Assert.Equal(2, capturedQueues.Count);
        Assert.Same(capturedQueues[0], capturedQueues[1]);
    }

    [Fact]
    public async Task DoWork_WhenParallelismIsZero_ClampsToAtLeastOneWorker()
    {
        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly,
            VolumeResolutionParallelism = 0
        };
        var library = new FileLibrary("/tmp/test", "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga = new Series("Test", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.Add(manga);
        _mangaContext.Chapters.Add(new Chapter(manga, "1", null, "T") { Downloaded = true, FileName = "c1.cbz" });
        await _mangaContext.SaveChangesAsync();

        var result = await MakeCoordinator(settings).DoWork(_mockScope.Object);

        // Parallelism=0 must clamp to 1, not silently produce zero workers
        Assert.Single(result);
        _mockFactory.Verify(f => f.Create(It.IsAny<ConcurrentQueue<string>>()), Times.Once);
    }

    [Fact]
    public async Task DoWork_QueueContainsAllDistinctMangaIds()
    {
        var settings = new TrangaSettings
        {
            VolumeResolutionStrategy = VolumeResolutionStrategy.ExactOnly,
            VolumeResolutionParallelism = 1
        };
        var library = new FileLibrary("/tmp/test", "Test Library");
        _mangaContext.FileLibraries.Add(library);
        var manga1 = new Series("Series X", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        var manga2 = new Series("Series Y", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        _mangaContext.Series.AddRange(manga1, manga2);
        // manga1 has 2 chapters — should still appear only once in the queue
        _mangaContext.Chapters.Add(new Chapter(manga1, "1", null, "T") { Downloaded = true, FileName = "c1.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga1, "2", null, "T") { Downloaded = true, FileName = "c2.cbz" });
        _mangaContext.Chapters.Add(new Chapter(manga2, "1", null, "T") { Downloaded = true, FileName = "c1.cbz" });
        await _mangaContext.SaveChangesAsync();

        ConcurrentQueue<string>? capturedQueue = null;
        _mockFactory.Setup(f => f.Create(It.IsAny<ConcurrentQueue<string>>()))
            .Callback<ConcurrentQueue<string>>(q => capturedQueue ??= q)
            .Returns(() => new CleanupOrphanedFilesWorker(false));

        await MakeCoordinator(settings).DoWork(_mockScope.Object);

        Assert.NotNull(capturedQueue);
        var items = capturedQueue.ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(manga1.Key, items);
        Assert.Contains(manga2.Key, items);
    }
}
