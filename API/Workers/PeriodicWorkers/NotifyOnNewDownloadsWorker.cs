using System.Diagnostics.CodeAnalysis;
using API.Notifications;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Periodic observer over ChapterDownloadedActionRecord rows. For every action record created
/// since the worker last ran, emits a user-visible notification via INotificationDispatcher.
/// This centralises notification fan-out so both image-list and torrent download paths converge
/// on a single emission point.
/// </summary>
public class NotifyOnNewDownloadsWorker(
    INotificationDispatcher dispatcher,
    TimeSpan? interval = null,
    IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromSeconds(15);

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private ActionsContext ActionsContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private SeriesContext SeriesContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        ActionsContext = GetContext<ActionsContext>(serviceScope);
        SeriesContext = GetContext<SeriesContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        // On the very first run after startup we deliberately skip the backlog — otherwise every
        // historical download would re-notify the user. LastExecution is updated to "now" by the
        // worker framework after this run completes, so subsequent runs see only fresh records.
        DateTime since = LastExecution == DateTime.UnixEpoch ? DateTime.UtcNow : LastExecution;

        List<ChapterDownloadedActionRecord> records = await ActionsContext.Actions
            .OfType<ChapterDownloadedActionRecord>()
            .Where(r => r.PerformedAt > since)
            .ToListAsync(CancellationToken);

        if (records.Count == 0) return [];

        // Look up series + chapter metadata in one go to format readable notifications.
        var chapterIds = records.Select(r => r.ChapterId).ToHashSet();
        Dictionary<string, Chapter> chapters = await SeriesContext.Chapters
            .Include(c => c.ParentManga)
            .Where(c => chapterIds.Contains(c.Key))
            .ToDictionaryAsync(c => c.Key, CancellationToken);

        foreach (ChapterDownloadedActionRecord record in records)
        {
            if (!chapters.TryGetValue(record.ChapterId, out Chapter? chapter)) continue;
            Series series = chapter.ParentManga;
            string title = "Chapter downloaded";
            string body = $"{series.Name} Ch. {chapter.ChapterNumber}" +
                          (string.IsNullOrEmpty(chapter.FileName) ? "" : $" - {chapter.FileName}");
            await dispatcher.DispatchAsync(title, body, CancellationToken);
        }

        return [];
    }
}
