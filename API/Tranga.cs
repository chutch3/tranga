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

    public IEnumerable<MangaConnector> Connectors { get; }
    public IEnumerable<MetadataFetcher> MetadataFetchers { get; }

    // 2. State collections are now instance variables, not static
    internal readonly ConcurrentDictionary<IPeriodic, Task> PeriodicWorkers = new();
    private readonly HashSet<BaseWorker> KnownWorkers = new();
    private readonly ConcurrentDictionary<BaseWorker, Task<BaseWorker[]>> RunningWorkers = new();

    public Tranga(
        IServiceProvider serviceProvider,
        IEnumerable<MangaConnector> connectors,
        IEnumerable<MetadataFetcher> fetchers,
        RateLimitHandler rateLimitHandler,
        TrangaSettings settings)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
        _rateLimitHandler = rateLimitHandler;
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
        while (RunningWorkers.Any(w => w.Key.State < WorkerExecutionState.Completed))
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
    public void AddWorker(BaseWorker worker)
    {
        Log.DebugFormat("Adding Worker {0}", worker);
        KnownWorkers.Add(worker);
        if(worker is not IPeriodic)
            StartWorker(worker, RemoveFromKnownWorkers(worker));
        else
            StartWorker(worker);

        if(worker is IPeriodic periodic)
            AddPeriodicWorker(worker, periodic);
    }

    private void AddPeriodicWorker(BaseWorker worker, IPeriodic periodic)
    {
        Log.DebugFormat("Adding Periodic {0}", worker);
        Task periodicTask = RefreshedPeriodicTask(worker, periodic);
        PeriodicWorkers.TryAdd((worker as IPeriodic)!, periodicTask);
        periodicTask.Start();
    }

    private Task RefreshedPeriodicTask(BaseWorker worker, IPeriodic periodic) => new (() =>
    {
        Log.DebugFormat("Waiting {0} for next run of {1}", periodic.Interval, worker);
        Thread.Sleep(periodic.Interval);
        StartWorker(worker, RefreshTask(worker, periodic));
    });

    private Action RefreshTask(BaseWorker worker, IPeriodic periodic) => () =>
    {
        if (worker.State < WorkerExecutionState.Created) //Failed
        {
            Log.DebugFormat("Task {0} failed. Not refreshing.", worker);
            return;
        }
        Log.DebugFormat("Refreshing {0}", worker);
        Task periodicTask = RefreshedPeriodicTask(worker, periodic);
        PeriodicWorkers.AddOrUpdate((worker as IPeriodic)!, periodicTask, (_, _) => periodicTask);
        periodicTask.Start();
    };

    private Action RemoveFromKnownWorkers(BaseWorker worker) => () =>
    {
        if (KnownWorkers.Contains(worker))
            KnownWorkers.Remove(worker);
    };

    public void AddWorkers(IEnumerable<BaseWorker> workers)
    {
        foreach (BaseWorker baseWorker in workers)
            AddWorker(baseWorker);
    }

    public BaseWorker[] GetKnownWorkers() => KnownWorkers.ToArray();
    public BaseWorker[] GetRunningWorkers() => RunningWorkers.Keys.ToArray();

    internal void StartWorker(BaseWorker worker, Action? finishedCallback = null)
    {
        Log.DebugFormat("Starting {0}", worker);
        if (_serviceProvider is null)
        {
            Log.Fatal("ServiceProvider is null");
            return;
        }
        Action afterWorkCallback = DefaultAfterWork(worker, finishedCallback);

        // Uses injected _settings
        while (RunningWorkers.Count > _settings.MaxConcurrentWorkers)
        {
            Log.WarnFormat("{0}: Max worker concurrency reached ({1})! Waiting {2}ms...", worker, _settings.MaxConcurrentWorkers, _settings.WorkCycleTimeoutMs);
            Thread.Sleep(_settings.WorkCycleTimeoutMs);
        }

        if (worker is BaseWorkerWithContexts withContexts)
        {
            // Uses injected _serviceProvider
            RunningWorkers.TryAdd(withContexts, withContexts.DoWork(_serviceProvider.CreateScope(), afterWorkCallback));
        }
        else
        {
            RunningWorkers.TryAdd(worker, worker.DoWork(afterWorkCallback));
        }
    }

    private Action DefaultAfterWork(BaseWorker worker, Action? callback = null) => () =>
    {
        Log.DebugFormat("DefaultAfterWork {0}", worker);
        try
        {
            if (RunningWorkers.TryGetValue(worker, out Task<BaseWorker[]>? task))
            {
                if (!task.IsCompleted)
                {
                    Log.DebugFormat("Waiting for Children to exit {0}", worker);
                    task.Wait();
                }
                if (task.IsCompletedSuccessfully)
                {
                    Log.DebugFormat("Children done {0}", worker);
                    BaseWorker[] newWorkers = task.Result;
                    Log.DebugFormat("{0} created {1} new Workers.", worker, newWorkers.Length);
                    AddWorkers(newWorkers);
                }else
                    Log.WarnFormat("Children failed: {0}", worker);
            }
            RunningWorkers.Remove(worker, out _);
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
        callback?.Invoke();
    };

    internal void StopWorker(BaseWorker worker)
    {
        Log.DebugFormat("Stopping {0}", worker);
        if(worker is IPeriodic periodicWorker)
            PeriodicWorkers.Remove(periodicWorker, out _);
        worker.Cancel();
        RunningWorkers.Remove(worker, out _);
    }

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
