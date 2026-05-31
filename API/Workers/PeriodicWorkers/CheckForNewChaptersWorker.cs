using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Creates Jobs to update available Chapters for all Series that are marked for Download
/// </summary>
public class CheckForNewChaptersWorker(TrangaSettings settings, IEnumerable<SeriesSource> connectors, TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval??Constants.CheckForNewChaptersInterval;
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private SeriesContext SeriesContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        SeriesContext = GetContext<SeriesContext>(serviceScope);
    }
    
    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Debug("Checking for new chapters...");
        List<SourceId<Series>> connectorIdsManga = await SeriesContext.MangaConnectorToManga
            .Include(id => id.Obj)
            .Where(id => id.UseForDownload)
            .ToListAsync(CancellationToken);
        Log.DebugFormat("Creating {0} update jobs...", connectorIdsManga.Count);

        List<BaseWorker> newWorkers = connectorIdsManga.Select(id => new RetrieveChaptersFromSourceWorker(id, settings.DownloadLanguage, connectors))
            .ToList<BaseWorker>();

        return newWorkers.ToArray();
    }
}