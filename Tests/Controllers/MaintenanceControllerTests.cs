using API.Controllers;
using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace API.Tests.Controllers;

public class MaintenanceControllerTests
{
    private (SeriesContext, ActionsContext) CreateContexts()
    {
        var mangaOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return (new SeriesContext(mangaOptions), new ActionsContext(actionsOptions));
    }

    private static MaintenanceController CreateController(SeriesContext mangaCtx, ActionsContext actionsCtx)
    {
        var controller = new MaintenanceController(mangaCtx, actionsCtx);
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    [Fact]
    public async Task CleanupActions_DeletesAllActions()
    {
        var (mangaCtx, actionsCtx) = CreateContexts();
        actionsCtx.Actions.Add(new API.Schema.ActionsContext.Actions.StartupActionRecord());
        await actionsCtx.SaveChangesAsync();

        var controller = CreateController(mangaCtx, actionsCtx);
        var result = await controller.CleanupActions();

        Assert.Equal(1, result.Value);
        Assert.Empty(await actionsCtx.Actions.ToListAsync());
    }

    [Fact]
    public async Task CleanupNoDownloadManga_RemovesUntrackedManga()
    {
        var (mangaCtx, actionsCtx) = CreateContexts();
        var untracked = new Series("Untracked", "Desc", "http://example.com/cover.jpg", SeriesReleaseStatus.Continuing, [], [], [], []);
        mangaCtx.Series.Add(untracked);
        await mangaCtx.SaveChangesAsync();

        var controller = CreateController(mangaCtx, actionsCtx);
        var result = await controller.CleanupNoDownloadManga();

        Assert.IsType<Ok>(result.Result);
        Assert.Empty(await mangaCtx.Series.ToListAsync());
    }

    [Fact]
    public void CleanupOrphanedFiles_QueuesWorker()
    {
        var (mangaCtx, actionsCtx) = CreateContexts();
        var mockQueue = new Mock<IWorkerQueue>();
        var controller = CreateController(mangaCtx, actionsCtx);

        var result = controller.CleanupOrphanedFiles(mockQueue.Object, dryRun: true);

        Assert.IsType<Ok>(result);
        mockQueue.Verify(x => x.AddWorker(It.Is<CleanupOrphanedFilesWorker>(w => w.ToString().Contains("DryRun=True"))), Times.Once);
    }

    [Fact]
    public async Task ResetAndResolveVolumes_ClearsAllVolumeNumbers()
    {
        var (mangaCtx, actionsCtx) = CreateContexts();
        var library = new FileLibrary("/tmp/test", "Test Library");
        mangaCtx.FileLibraries.Add(library);
        var manga = new Series("Test Series", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        mangaCtx.Series.Add(manga);
        mangaCtx.Chapters.Add(new Chapter(manga, "1", 3, null) { Downloaded = true, FileName = "test1.cbz" });
        mangaCtx.Chapters.Add(new Chapter(manga, "2", 3, null) { Downloaded = true, FileName = "test2.cbz" });
        await mangaCtx.SaveChangesAsync();

        var controller = CreateController(mangaCtx, actionsCtx);
        var result = await controller.ResetAndResolveVolumes(
            new Mock<IWorkerQueue>().Object,
            new TrangaSettings(),
            new Mock<IBatchWorkerFactory<string>>().Object);

        Assert.IsType<Ok>(result.Result);
        var chapters = await mangaCtx.Chapters.ToListAsync();
        Assert.All(chapters, c => Assert.Null(c.VolumeNumber));
    }

    [Fact]
    public async Task ResetAndResolveVolumes_WhenStrategyDisabled_StillClearsVolumesAndQueuesWorker()
    {
        // Documents the current behavior: endpoint clears volumes regardless of strategy,
        // then queues a worker that will immediately exit (Disabled check is in the worker, not the endpoint).
        // The worker being queued is intentional — the endpoint is a repair tool and always queues.
        var (mangaCtx, actionsCtx) = CreateContexts();
        var library = new FileLibrary("/tmp/test", "Test Library");
        mangaCtx.FileLibraries.Add(library);
        var manga = new Series("Test", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        mangaCtx.Series.Add(manga);
        mangaCtx.Chapters.Add(new Chapter(manga, "1", 3, null) { Downloaded = true, FileName = "test1.cbz" });
        await mangaCtx.SaveChangesAsync();

        var mockQueue = new Mock<IWorkerQueue>();
        var controller = CreateController(mangaCtx, actionsCtx);
        var result = await controller.ResetAndResolveVolumes(
            mockQueue.Object,
            new TrangaSettings { VolumeResolutionStrategy = VolumeResolutionStrategy.Disabled },
            new Mock<IBatchWorkerFactory<string>>().Object);

        Assert.IsType<Ok>(result.Result);
        Assert.All(await mangaCtx.Chapters.ToListAsync(), c => Assert.Null(c.VolumeNumber));
        mockQueue.Verify(x => x.AddWorker(It.IsAny<ResolveMissingVolumesWorker>()), Times.Once);
    }

    [Fact]
    public async Task ResetAndResolveVolumes_AlsoClearsVolumesOnNonDownloadedChapters()
    {
        // The clear is a bulk operation on all chapters — even undownloaded ones.
        // The resolver only re-populates downloaded chapters, so non-downloaded chapters
        // permanently lose their volume metadata after a reset.
        var (mangaCtx, actionsCtx) = CreateContexts();
        var library = new FileLibrary("/tmp/test", "Test Library");
        mangaCtx.FileLibraries.Add(library);
        var manga = new Series("Test", "Desc", "url", SeriesReleaseStatus.Continuing, [], [], [], [], library);
        mangaCtx.Series.Add(manga);
        mangaCtx.Chapters.Add(new Chapter(manga, "1", 2, null) { Downloaded = true, FileName = "test1.cbz" });
        mangaCtx.Chapters.Add(new Chapter(manga, "2", 2, null) { Downloaded = false, FileName = null });
        await mangaCtx.SaveChangesAsync();

        var controller = CreateController(mangaCtx, actionsCtx);
        await controller.ResetAndResolveVolumes(
            new Mock<IWorkerQueue>().Object,
            new TrangaSettings(),
            new Mock<IBatchWorkerFactory<string>>().Object);

        var chapters = await mangaCtx.Chapters.ToListAsync();
        Assert.All(chapters, c => Assert.Null(c.VolumeNumber));
    }

    [Fact]
    public async Task ResetAndResolveVolumes_QueuesResolveMissingVolumesWorker()
    {
        var (mangaCtx, actionsCtx) = CreateContexts();
        var mockQueue = new Mock<IWorkerQueue>();
        var controller = CreateController(mangaCtx, actionsCtx);

        var result = await controller.ResetAndResolveVolumes(
            mockQueue.Object,
            new TrangaSettings(),
            new Mock<IBatchWorkerFactory<string>>().Object);

        Assert.IsType<Ok>(result.Result);
        mockQueue.Verify(x => x.AddWorker(It.IsAny<ResolveMissingVolumesWorker>()), Times.Once);
    }
}
