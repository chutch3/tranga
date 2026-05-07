using API.Controllers;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
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
    private (MangaContext, ActionsContext) CreateContexts()
    {
        var mangaOptions = new DbContextOptionsBuilder<MangaContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return (new MangaContext(mangaOptions), new ActionsContext(actionsOptions));
    }

    private static MaintenanceController CreateController(MangaContext mangaCtx, ActionsContext actionsCtx)
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
        var untracked = new Manga("Untracked", "Desc", "http://example.com/cover.jpg", MangaReleaseStatus.Continuing, [], [], [], []);
        mangaCtx.Mangas.Add(untracked);
        await mangaCtx.SaveChangesAsync();

        var controller = CreateController(mangaCtx, actionsCtx);
        var result = await controller.CleanupNoDownloadManga();

        Assert.IsType<Ok>(result.Result);
        Assert.Empty(await mangaCtx.Mangas.ToListAsync());
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
        var manga = new Manga("Test Manga", "Desc", "url", MangaReleaseStatus.Continuing, [], [], [], [], library);
        mangaCtx.Mangas.Add(manga);
        mangaCtx.Chapters.Add(new Chapter(manga, "1", 3, null) { Downloaded = true, FileName = "test1.cbz" });
        mangaCtx.Chapters.Add(new Chapter(manga, "2", 3, null) { Downloaded = true, FileName = "test2.cbz" });
        await mangaCtx.SaveChangesAsync();

        var controller = CreateController(mangaCtx, actionsCtx);
        var result = await controller.ResetAndResolveVolumes(
            new Mock<IWorkerQueue>().Object,
            new TrangaSettings(),
            new Mock<IMangaDexVolumeResolver>().Object);

        Assert.IsType<Ok>(result.Result);
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
            new Mock<IMangaDexVolumeResolver>().Object);

        Assert.IsType<Ok>(result.Result);
        mockQueue.Verify(x => x.AddWorker(It.IsAny<ResolveMissingVolumesWorker>()), Times.Once);
    }
}
