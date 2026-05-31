using API;
using API.Notifications;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.SeriesContext;
using API.Workers.PeriodicWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

public class NotifyOnNewDownloadsWorkerTests
{
    private static (SeriesContext seriesCtx, ActionsContext actionsCtx, Chapter chapter, IServiceScope scope, ServiceProvider sp)
        BuildFixture()
    {
        var seriesOptions = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase("series-" + Guid.NewGuid().ToString("N")).Options;
        var actionsOptions = new DbContextOptionsBuilder<ActionsContext>()
            .UseInMemoryDatabase("actions-" + Guid.NewGuid().ToString("N")).Options;

        var seriesCtx = new SeriesContext(seriesOptions);
        var actionsCtx = new ActionsContext(actionsOptions);

        var library = new FileLibrary("/tmp", "L");
        seriesCtx.FileLibraries.Add(library);
        var series = new Series("Saga", "d", "c", SeriesReleaseStatus.Continuing,
            new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
            library, 0f, 2024, "en");
        seriesCtx.Series.Add(series);
        var chapter = new Chapter(series, "60", null, "T") { FileName = "saga-60.cbz", Downloaded = true };
        seriesCtx.Chapters.Add(chapter);
        seriesCtx.SaveChanges();

        var services = new ServiceCollection();
        services.AddSingleton(seriesCtx);
        services.AddSingleton(actionsCtx);
        var sp = services.BuildServiceProvider();
        return (seriesCtx, actionsCtx, chapter, sp.CreateScope(), sp);
    }

    [Fact]
    public async Task DoWork_DispatchesNotification_ForNewActionRecordSinceLastRun()
    {
        var (seriesCtx, actionsCtx, chapter, scope, sp) = BuildFixture();
        try
        {
            // Action record produced AFTER the worker's LastExecution
            actionsCtx.Actions.Add(new ChapterDownloadedActionRecord(
                Actions.ChapterDownloaded, DateTime.UtcNow, chapter.ParentManga.Key, chapter.Key));
            await actionsCtx.SaveChangesAsync();

            string? title = null, body = null;
            var dispatcher = new Mock<INotificationDispatcher>();
            dispatcher.Setup(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .Callback<string, string, CancellationToken>((t, b, _) => { title = t; body = b; })
                      .Returns(Task.CompletedTask);

            var worker = new NotifyOnNewDownloadsWorker(dispatcher.Object)
            {
                LastExecution = DateTime.UtcNow.AddMinutes(-1)  // forces the worker to actually process records
            };

            await worker.DoWork(scope);

            Assert.Equal("Chapter downloaded", title);
            Assert.Contains("Saga", body);
            Assert.Contains("60", body);
            dispatcher.Verify(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public async Task DoWork_SkipsBacklog_OnFirstRunAfterStartup()
    {
        var (seriesCtx, actionsCtx, chapter, scope, sp) = BuildFixture();
        try
        {
            // Pre-existing record (backlog)
            actionsCtx.Actions.Add(new ChapterDownloadedActionRecord(
                Actions.ChapterDownloaded, DateTime.UtcNow.AddDays(-1), chapter.ParentManga.Key, chapter.Key));
            await actionsCtx.SaveChangesAsync();

            var dispatcher = new Mock<INotificationDispatcher>();
            // LastExecution starts at UnixEpoch — the worker should treat this as first run and skip backlog
            var worker = new NotifyOnNewDownloadsWorker(dispatcher.Object);

            await worker.DoWork(scope);

            dispatcher.Verify(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { sp.Dispose(); }
    }

    [Fact]
    public async Task DoWork_DoesNothing_WhenNoActionRecordsExist()
    {
        var (seriesCtx, actionsCtx, chapter, scope, sp) = BuildFixture();
        try
        {
            var dispatcher = new Mock<INotificationDispatcher>();
            var worker = new NotifyOnNewDownloadsWorker(dispatcher.Object)
            {
                LastExecution = DateTime.UtcNow.AddMinutes(-1)
            };

            await worker.DoWork(scope);

            dispatcher.Verify(d => d.DispatchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { sp.Dispose(); }
    }
}
