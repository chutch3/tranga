using API.Controllers;
using API.Schema.ActionsContext;
using API.Schema.MangaContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Tests;

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
}
