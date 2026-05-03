using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.MangaDownloadClients;
using API.Schema.MangaContext;
using API.Schema.MangaContext.MetadataFetchers;
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

    public IEnumerable<MangaConnector> Connectors { get; }
    public IEnumerable<MetadataFetcher> MetadataFetchers { get; }

    public Tranga(
        IServiceProvider serviceProvider,
        IEnumerable<MangaConnector> connectors,
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

    public void StartupTasks()
    {
        // 3. Pulling workers directly from the DI container
        AddWorker(GetWorker<SendNotificationsWorker>());
        AddWorker(GetWorker<CleanupMangaconnectorIdsWithoutConnector>());
        AddWorker(GetWorker<CleanupMangaCoversWorker>());

        if(Constants.UpdateChaptersDownloadedBeforeStarting)
            AddWorker(GetWorker<UpdateChaptersDownloadedWorker>());

        Log.Info("Waiting for startup to complete...");
        while (_workerQueue.GetRunningWorkers().Any(w => w.State < WorkerExecutionState.Completed))
            Thread.Sleep(1000);
        Log.Info("Start complete!");
    }

    internal void AddDefaultWorkers()
    {
        AddWorker(GetWorker<UpdateMetadataWorker>());
        AddWorker(GetWorker<CheckForNewChaptersWorker>());
        AddWorker(GetWorker<StartNewChapterDownloadsWorker>());
        AddWorker(GetWorker<RemoveOldNotificationsWorker>());
        AddWorker(GetWorker<UpdateCoversWorker>());
        AddWorker(GetWorker<CleanupOrphanedFilesWorker>());

        if(Constants.UpdateChaptersDownloadedBeforeStarting)
            AddWorker(GetWorker<UpdateChaptersDownloadedWorker>());
    }

    internal bool TryGetMangaConnector(string name, [NotNullWhen(true)]out MangaConnector? mangaConnector)
    {
        mangaConnector = Connectors.FirstOrDefault(c => c.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase));
        return mangaConnector != null;
    }

    // 4. Removed 'static' from all these operational methods
    public void AddWorker(BaseWorker worker) => _workerQueue.AddWorker(worker);

    public void AddWorkers(IEnumerable<BaseWorker> workers) => _workerQueue.AddWorkers(workers);

    public BaseWorker[] GetKnownWorkers() => _workerQueue.GetKnownWorkers();
    public BaseWorker[] GetRunningWorkers() => _workerQueue.GetRunningWorkers();

    internal void StopWorker(BaseWorker worker) => _workerQueue.StopWorker(worker);

    // 5. Removed 'this' from MangaContext. It is now just a normal method you call on Tranga.
    internal async Task<(Manga manga, MangaConnectorId<Manga> id)?> AddMangaToContext(MangaContext context, (Manga, MangaConnectorId<Manga>) addManga, CancellationToken token) =>
        await AddMangaToContext(context, addManga.Item1, addManga.Item2, token);

    internal async Task<(Manga manga, MangaConnectorId<Manga> id)?> AddMangaToContext(MangaContext context, Manga addManga, MangaConnectorId<Manga> addMcId, CancellationToken token)
    {
        context.ChangeTracker.Clear();
        Log.DebugFormat("Adding Manga to Context: {0}", addManga);
        (Manga, MangaConnectorId<Manga>)? result;
        if (await context.FindMangaLike(addManga, token) is { } mangaId)
        {
            Manga manga = await context.MangaIncludeAll().FirstAsync(m => m.Key == mangaId, token);
            Log.DebugFormat("Merging with existing Manga: {0}", manga);

            var existingMcId = manga.MangaConnectorIds
                .FirstOrDefault(id => id.MangaConnectorName == addMcId.MangaConnectorName
                                      && id.IdOnConnectorSite == addMcId.IdOnConnectorSite);

            MangaConnectorId<Manga> mcIdToUse;
            if (existingMcId == null)
            {
                mcIdToUse = new MangaConnectorId<Manga>(manga, addMcId.MangaConnectorName, addMcId.IdOnConnectorSite, addMcId.WebsiteUrl, addMcId.UseForDownload);
                manga.MangaConnectorIds.Add(mcIdToUse);
                Log.DebugFormat("Added new MangaConnectorId for {0}", addMcId.MangaConnectorName);
            }
            else
            {
                mcIdToUse = existingMcId;
                if (existingMcId.WebsiteUrl != addMcId.WebsiteUrl)
                {
                    var updatedMcId = new MangaConnectorId<Manga>(manga, existingMcId.MangaConnectorName, existingMcId.IdOnConnectorSite, addMcId.WebsiteUrl, existingMcId.UseForDownload);
                    manga.MangaConnectorIds.Remove(existingMcId);
                    manga.MangaConnectorIds.Add(updatedMcId);
                    mcIdToUse = updatedMcId;
                    Log.DebugFormat("Updated/Recreated MangaConnectorId for {0} (URL changed)", addMcId.MangaConnectorName);
                }
            }

            result = (manga, mcIdToUse);
        }
        else
        {
            Log.Debug("Manga does not exist yet.");
            IEnumerable<MangaTag> mergedTags = addManga.MangaTags.Select(mt =>
            {
                MangaTag? inDb = context.Tags.Find(mt.Tag);
                return inDb ?? mt;
            });
            addManga.MangaTags = mergedTags.ToList();

            IEnumerable<Author> mergedAuthors = addManga.Authors.Select(ma =>
            {
                Author? inDb = context.Authors.Find(ma.Key);
                return inDb ?? ma;
            });
            addManga.Authors = mergedAuthors.ToList();

            context.Mangas.Add(addManga);
            context.Set<MangaConnectorId<Manga>>().Add(addMcId);
            result = (addManga, addMcId);
        }

        if (await context.Sync(token, reason: "AddMangaToContext") is { success: false })
            return null;

        DownloadCoverFromMangaconnectorWorker downloadCoverWorker = new (result.Value.Item2, Connectors);
        AddWorker(downloadCoverWorker);

        return result;
    }
}
