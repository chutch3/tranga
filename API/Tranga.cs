using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using API.Schema.SeriesContext.MetadataFetchers;
using API.Workers;
using API.Workers.MangaDownloadWorkers;
using API.Workers.PeriodicWorkers;
using API.Workers.PeriodicWorkers.MaintenanceWorkers;
using API.Workers.MaintenanceWorkers;
using log4net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection; // Required for GetRequiredService

namespace API;

// 1. Removed 'static' from the class definition
public class Tranga
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(Tranga));

    private readonly IServiceProvider _serviceProvider;
    private readonly RateLimitHandler _rateLimitHandler;
    private readonly TrangaSettings _settings;
    private readonly IWorkerQueue _workerQueue;

    public IEnumerable<SeriesSource> Connectors { get; }
    public IEnumerable<MetadataFetcher> MetadataFetchers { get; }

    public Tranga(
        IServiceProvider serviceProvider,
        IEnumerable<SeriesSource> connectors,
        IEnumerable<MetadataFetcher> fetchers,
        RateLimitHandler rateLimitHandler,
        TrangaSettings settings,
        IWorkerQueue workerQueue)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
        _rateLimitHandler = rateLimitHandler;
        _workerQueue = workerQueue;
        Connectors = connectors;
        MetadataFetchers = fetchers;
    }

    // Helper to keep the startup lists clean
    private T GetWorker<T>() where T : BaseWorker => _serviceProvider.GetRequiredService<T>();

    public async Task StartupTasks()
    {
        // 3. Pulling workers directly from the DI container
        _workerQueue.AddWorker(GetWorker<SendNotificationsWorker>());
        _workerQueue.AddWorker(GetWorker<CleanupSourceIdsWithoutSource>());
        _workerQueue.AddWorker(GetWorker<CleanupMangaCoversWorker>());

        // Moved to AddDefaultWorkers

        Log.Info("Waiting for startup to complete...");
        while (_workerQueue.GetRunningWorkers().Any(w => w.State < WorkerExecutionState.Completed))
            await Task.Delay(1000);
        Log.Info("Start complete!");
    }

    internal void AddDefaultWorkers()
    {
        _workerQueue.AddWorker(GetWorker<UpdateMetadataWorker>());
        _workerQueue.AddWorker(GetWorker<NotifyOnNewDownloadsWorker>());
        _workerQueue.AddWorker(GetWorker<CheckForNewChaptersWorker>());
        _workerQueue.AddWorker(GetWorker<StartNewChapterDownloadsWorker>());
        _workerQueue.AddWorker(GetWorker<RemoveOldNotificationsWorker>());
        _workerQueue.AddWorker(GetWorker<UpdateCoversWorker>());
        _workerQueue.AddWorker(GetWorker<CleanupOrphanedFilesWorker>());
        _workerQueue.AddWorker(GetWorker<ResolveMissingVolumesWorker>());
        _workerQueue.AddWorker(GetWorker<SyncChapterFileNamesWorker>());

        // Torrent completion worker is registered only when the torrent path is configured;
        // skip silently if absent so deployments without Prowlarr+qBittorrent are unaffected.
        if (_serviceProvider.GetService<TorrentCompletionWorker>() is { } torrentWorker)
            _workerQueue.AddWorker(torrentWorker);

        if(Constants.UpdateChaptersDownloadedBeforeStarting)
            _workerQueue.AddWorker(GetWorker<UpdateChaptersDownloadedWorker>());
    }

    internal bool TryGetSeriesSource(string name, [NotNullWhen(true)]out SeriesSource? seriesSource)
    {
        seriesSource = Connectors.FirstOrDefault(c => c.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        return seriesSource != null;
    }

    // 5. Removed 'this' from SeriesContext. It is now just a normal method you call on Tranga.
    internal async Task<(Series manga, SourceId<Series> id)?> AddMangaToContext(SeriesContext context, (Series, SourceId<Series>) addManga, CancellationToken token) =>
        await AddMangaToContext(context, addManga.Item1, addManga.Item2, token);

    internal async Task<(Series manga, SourceId<Series> id)?> AddMangaToContext(SeriesContext context, Series addManga, SourceId<Series> addMcId, CancellationToken token)
    {
        context.ChangeTracker.Clear();
        Log.DebugFormat("Adding Series to Context: {0}", addManga);
        (Series, SourceId<Series>)? result;
        if (await context.FindMangaLike(addManga, token) is { } mangaId)
        {
            Series manga = await context.MangaIncludeAll().FirstAsync(m => m.Key == mangaId, token);
            Log.DebugFormat("Merging with existing Series: {0}", manga);

            var existingMcId = manga.SourceIds
                .FirstOrDefault(id => id.MangaConnectorName == addMcId.MangaConnectorName
                                      && id.IdOnConnectorSite == addMcId.IdOnConnectorSite);

            SourceId<Series> mcIdToUse;
            if (existingMcId == null)
            {
                mcIdToUse = new SourceId<Series>(manga, addMcId.MangaConnectorName, addMcId.IdOnConnectorSite, addMcId.WebsiteUrl, addMcId.UseForDownload);
                manga.SourceIds.Add(mcIdToUse);
                Log.DebugFormat("Added new SourceId for {0}", addMcId.MangaConnectorName);
            }
            else
            {
                mcIdToUse = existingMcId;
                if (existingMcId.WebsiteUrl != addMcId.WebsiteUrl)
                {
                    var updatedMcId = new SourceId<Series>(manga, existingMcId.MangaConnectorName, existingMcId.IdOnConnectorSite, addMcId.WebsiteUrl, existingMcId.UseForDownload);
                    manga.SourceIds.Remove(existingMcId);
                    manga.SourceIds.Add(updatedMcId);
                    mcIdToUse = updatedMcId;
                    Log.DebugFormat("Updated/Recreated SourceId for {0} (URL changed)", addMcId.MangaConnectorName);
                }
            }

            result = (manga, mcIdToUse);
        }
        else
        {
            Log.Debug("Series does not exist yet.");
            IEnumerable<SeriesTag> mergedTags = addManga.MangaTags.Select(mt =>
            {
                SeriesTag? inDb = context.Tags.Find(mt.Tag);
                return inDb ?? mt;
            });
            addManga.MangaTags = mergedTags.ToList();

            IEnumerable<Author> mergedAuthors = addManga.Authors.Select(ma =>
            {
                Author? inDb = context.Authors.Find(ma.Key);
                return inDb ?? ma;
            });
            addManga.Authors = mergedAuthors.ToList();

            context.Series.Add(addManga);
            context.Set<SourceId<Series>>().Add(addMcId);
            result = (addManga, addMcId);
        }

        if (await context.Sync(token, reason: "AddMangaToContext") is { success: false })
            return null;

        DownloadCoverFromSourceWorker downloadCoverWorker = new (result.Value.Item2, Connectors);
        _workerQueue.AddWorker(downloadCoverWorker);

        return result;
    }
}
