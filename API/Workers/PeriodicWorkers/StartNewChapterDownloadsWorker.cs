using System.Diagnostics.CodeAnalysis;
using API.Acquirers;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Create new Workers for Chapters on Series marked for Download, that havent been downloaded yet.
/// </summary>
public class StartNewChapterDownloadsWorker(TrangaSettings settings, IWorkerQueue workerQueue, IEnumerable<SeriesSource> connectors, IEnumerable<IChapterAcquirer>? acquirers = null, TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    private readonly IChapterAcquirer[] _acquirers = acquirers?.ToArray() ?? [];

    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromSeconds(10);
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private SeriesContext SeriesContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        SeriesContext = GetContext<SeriesContext>(serviceScope);
    }
    
    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Debug("Checking for missing chapters...");
        
        // Get missing chapters
        List<SourceId<Chapter>> missingChapters = await GetMissingChapters(SeriesContext, CancellationToken);
        
        Log.DebugFormat("Found {0} missing chapters.", missingChapters.Count);

        // Consider ALL in-flight download workers (queued AND running), not just running ones. A worker
        // created by a previous tick may still be queued (registration is asynchronous); de-duping only
        // against running workers would schedule a duplicate download for the same chapter.
        List<DownloadChapterFromSourceWorker> inFlightDownloadWorkers = workerQueue.GetKnownWorkers()
            .OfType<DownloadChapterFromSourceWorker>()
            .ToList();
        HashSet<string> inFlightChapterIds = inFlightDownloadWorkers.Select(w => w.ChapterIdId).ToHashSet();
        missingChapters.RemoveAll(ch => inFlightChapterIds.Contains(ch.Key));
        Log.DebugFormat("{0} chapter not being downloaded", missingChapters.Count);

        // Maximum Concurrent workers. Clamp at 0: if more downloads are already in-flight than the limit
        // we must not schedule more (and must never pass a negative count downstream).
        int downloadWorkers = inFlightDownloadWorkers.Count;
        int amountNewWorkers = Math.Max(0, settings.MaxConcurrentDownloads - downloadWorkers);

        Log.DebugFormat("{0} in-flight download Workers. {1} available new download Workers.", downloadWorkers, amountNewWorkers);
        IEnumerable<SourceId<Chapter>> newDownloadChapters = missingChapters.OrderBy(ch => ch.Obj, new Chapter.ChapterComparer()).Take(amountNewWorkers);

        // Create new jobs. Each download worker gets the acquirer that matches its source's Kind;
        // if none is registered the worker falls back to the default image-list path.
        List<BaseWorker> newWorkers = newDownloadChapters.Select(mcId =>
        {
            SeriesSource? source = connectors.FirstOrDefault(c =>
                c.Name.Equals(mcId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
            IChapterAcquirer? acquirer = source is null
                ? null
                : _acquirers.FirstOrDefault(a => a.Kind == source.Kind);
            return (BaseWorker)new DownloadChapterFromSourceWorker(mcId, connectors, settings, acquirer);
        }).ToList();

        return newWorkers.ToArray();
    }
    
    internal static async Task<List<SourceId<Chapter>>> GetMissingChapters(SeriesContext ctx, CancellationToken cancellationToken) => await ctx.MangaConnectorToChapter
        .Include(id => id.Obj)
        .Where(id => !id.Obj.Downloaded && id.UseForDownload)
        .ToListAsync(cancellationToken);
}