using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.Schema.MangaContext;
using API.Workers.MangaDownloadWorkers;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Create new Workers for Chapters on Series marked for Download, that havent been downloaded yet.
/// </summary>
public class StartNewChapterDownloadsWorker(TrangaSettings settings, IWorkerQueue workerQueue, IEnumerable<SeriesSource> connectors, TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{

    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromSeconds(10);
    
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
    }
    
    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Debug("Checking for missing chapters...");
        
        // Get missing chapters
        List<SourceId<Chapter>> missingChapters = await GetMissingChapters(MangaContext, CancellationToken);
        
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

        // Create new jobs
        List<BaseWorker> newWorkers = newDownloadChapters.Select(mcId => new DownloadChapterFromSourceWorker(mcId, connectors, settings)).ToList<BaseWorker>();
        
        return newWorkers.ToArray();
    }
    
    internal static async Task<List<SourceId<Chapter>>> GetMissingChapters(MangaContext ctx, CancellationToken cancellationToken) => await ctx.MangaConnectorToChapter
        .Include(id => id.Obj)
        .Where(id => !id.Obj.Downloaded && id.UseForDownload)
        .ToListAsync(cancellationToken);
}