using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace API.Workers.MaintenanceWorkers;

public class SyncChapterFileNamesWorker(TrangaSettings settings, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    private SeriesContext _mangaContext = null!;

    public DateTime LastExecution { get; set; } = DateTime.MinValue;
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<SeriesContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        var chapters = await _mangaContext.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .Where(c => c.Downloaded && c.FileName != null)
            .ToListAsync(CancellationToken);

        List<BaseWorker> newJobs = new();

        foreach (var chapter in chapters)
        {
            string expected = chapter.GetArchiveFileName(settings.ChapterNamingScheme);
            if (chapter.FileName == expected) continue;
            newJobs.Add(new RenameChapterFileWorker(chapter.Key, expected, settings));
        }

        LastExecution = DateTime.UtcNow;
        return newJobs.ToArray();
    }
}
