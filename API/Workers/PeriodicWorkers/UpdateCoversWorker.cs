using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Creates Workers to update covers for Series
/// </summary>
/// <param name="interval"></param>
/// <param name="dependsOn"></param>
public class UpdateCoversWorker(IEnumerable<SeriesSource> connectors, TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromHours(6);
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private SeriesContext SeriesContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        SeriesContext = GetContext<SeriesContext>(serviceScope);
    }
    
    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        List<SourceId<Series>> manga = await SeriesContext.MangaConnectorToManga.Where(mcId => mcId.UseForDownload).ToListAsync(CancellationToken);
        List<BaseWorker> newWorkers = manga.Select(m => new DownloadCoverFromSourceWorker(m, connectors)).ToList<BaseWorker>();
        return newWorkers.ToArray();
    }
}