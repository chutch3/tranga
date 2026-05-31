using System.Diagnostics.CodeAnalysis;
using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Updates the database to reflect changes made on disk
/// </summary>
public class UpdateChaptersDownloadedWorker(TrangaSettings settings, TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval??TimeSpan.FromDays(1);

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private SeriesContext SeriesContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        SeriesContext = GetContext<SeriesContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Debug("Checking chapter files...");
        List<Chapter> chapters = await SeriesContext.Chapters.ToListAsync(CancellationToken);
        Log.DebugFormat("Checking {0} chapters...", chapters.Count);
        foreach (Chapter chapter in chapters)
        {
            try
            {
                bool downloaded = await chapter.CheckDownloaded(SeriesContext, settings.ChapterNamingScheme, token: CancellationToken);
                chapter.Downloaded = downloaded;
                if (!downloaded)
                    chapter.FileName = null;
            }
            catch (Exception exception)
            {
                Log.Error(exception);
            }
        }

        if(await SeriesContext.Sync(CancellationToken, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } e)
            Log.ErrorFormat("Failed to save database changes: {0}", e.exceptionMessage);

        return [];
    }
}
