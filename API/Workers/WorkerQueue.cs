using System.Collections.Concurrent;
using log4net;
using Microsoft.Extensions.DependencyInjection;

namespace API.Workers;

/// <summary>
/// Manages the lifecycle of all background workers: adding, starting, tracking, and stopping them.
/// </summary>
public class WorkerQueue : IWorkerQueue
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(WorkerQueue));

    private readonly IServiceProvider _serviceProvider;
    private readonly TrangaSettings _settings;

    internal readonly ConcurrentDictionary<IPeriodic, Task> PeriodicWorkers = new();
    // Thread-safe set: known workers are mutated from background start/cleanup tasks while being read
    // concurrently by worker threads (e.g. StartNewChapterDownloadsWorker). A plain HashSet would tear.
    private readonly ConcurrentDictionary<BaseWorker, byte> _knownWorkers = new();
    private readonly ConcurrentDictionary<BaseWorker, Task<BaseWorker[]>> _runningWorkers = new();
    private readonly SemaphoreSlim _concurrencySemaphore;

    public WorkerQueue(IServiceProvider serviceProvider, TrangaSettings settings)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
        _concurrencySemaphore = new SemaphoreSlim(settings.MaxConcurrentWorkers, settings.MaxConcurrentWorkers);
    }

    public void AddWorker(BaseWorker worker)
    {
        AddWorkers([worker]);
    }

    public void AddWorkers(IEnumerable<BaseWorker> workers)
    {
        var workerList = workers.ToList();
        foreach (var worker in workerList)
        {
            Log.DebugFormat("Registering Worker {0}", worker);
            _knownWorkers.TryAdd(worker, 0);
        }

        // Fire and forget ONE task to start the workers sequentially in background
        Task.Run(async () => 
        {
            foreach (var worker in workerList)
            {
                try 
                {
                    if (worker is not IPeriodic)
                        await StartWorker(worker, RemoveFromKnownWorkers(worker));
                    else
                        await StartWorker(worker);

                    if (worker is IPeriodic periodic)
                        AddPeriodicWorker(worker, periodic);
                }
                catch (Exception e)
                {
                    Log.ErrorFormat("Failed to add worker {0}: {1}", worker, e);
                }
            }
        });
    }

    public BaseWorker[] GetKnownWorkers() => _knownWorkers.Keys.ToArray();

    public BaseWorker[] GetRunningWorkers() => _runningWorkers.Keys.ToArray();

    public void StopWorker(BaseWorker worker)
    {
        Log.DebugFormat("Stopping {0}", worker);
        if (worker is IPeriodic periodicWorker)
            PeriodicWorkers.Remove(periodicWorker, out _);
        worker.Cancel();
        _runningWorkers.Remove(worker, out _);
    }

    internal async Task StartWorker(BaseWorker worker, Action? finishedCallback = null)
    {
        if (_runningWorkers.ContainsKey(worker))
        {
            Log.DebugFormat("Worker {0} is already running. Skipping startup.", worker);
            finishedCallback?.Invoke();
            return;
        }

        Log.DebugFormat("Queueing {0}", worker);
        await _concurrencySemaphore.WaitAsync();
        
        try 
        {
            if (_runningWorkers.ContainsKey(worker))
            {
                Log.DebugFormat("Worker {0} started by another thread while queueing. Skipping.", worker);
                _concurrencySemaphore.Release();
                finishedCallback?.Invoke();
                return;
            }

            Log.DebugFormat("Starting {0}", worker);

            Task<BaseWorker[]> workTask = worker is BaseWorkerWithContexts withContexts
                ? withContexts.DoWork(_serviceProvider.CreateScope())
                : worker.DoWork();

            if (!_runningWorkers.TryAdd(worker, workTask))
            {
                Log.WarnFormat("Failed to add worker {0} to running list. It might be a duplicate.", worker);
                _concurrencySemaphore.Release();
                finishedCallback?.Invoke();
                return;
            }

            // Attach cleanup + follow-up scheduling to the returned task itself. This fires for EVERY
            // return path of DoWork (work done, dependency-waiting, dependency-starting), so the
            // concurrency slot is always released and follow-up workers are always queued.
            _ = workTask.ContinueWith(t => OnWorkerFinished(worker, t, finishedCallback), TaskScheduler.Default);
        }
        catch (Exception e)
        {
            Log.ErrorFormat("Failed to start worker {0}: {1}", worker, e);
            _concurrencySemaphore.Release();
            throw;
        }
    }

    private async Task OnWorkerFinished(BaseWorker worker, Task<BaseWorker[]> task, Action? finishedCallback)
    {
        Log.DebugFormat("Worker finished {0}", worker);
        BaseWorker[] newWorkers = [];
        try
        {
            if (task.IsCompletedSuccessfully)
            {
                newWorkers = await task;
                Log.DebugFormat("{0} created {1} new Workers.", worker, newWorkers.Length);
            }
            else
            {
                Log.WarnFormat("Worker did not complete successfully: {0} ({1})", worker, task.Exception?.Message);
            }
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
        finally
        {
            // Free the slot and de-register BEFORE enqueuing follow-up work, so the new workers can
            // acquire the slot and a re-queued worker is not skipped as "already running".
            _runningWorkers.Remove(worker, out _);
            _concurrencySemaphore.Release();
        }

        if (newWorkers.Length > 0)
            AddWorkers(newWorkers);

        finishedCallback?.Invoke();
    }

    private void AddPeriodicWorker(BaseWorker worker, IPeriodic periodic)
    {
        Log.DebugFormat("Adding Periodic {0}", worker);
        Task periodicTask = Task.Run(() => RefreshedPeriodicTask(worker, periodic));
        PeriodicWorkers.TryAdd((worker as IPeriodic)!, periodicTask);
    }

    private async Task RefreshedPeriodicTask(BaseWorker worker, IPeriodic periodic)
    {
        Log.DebugFormat("Waiting {0} for next run of {1}", periodic.Interval, worker);
        await Task.Delay(periodic.Interval);
        await StartWorker(worker, RefreshTask(worker, periodic));
    }

    private Action RefreshTask(BaseWorker worker, IPeriodic periodic) => () =>
    {
        if (worker.State < WorkerExecutionState.Created) // Failed
        {
            Log.DebugFormat("Task {0} failed. Not refreshing.", worker);
            return;
        }
        Log.DebugFormat("Refreshing {0}", worker);
        Task periodicTask = Task.Run(() => RefreshedPeriodicTask(worker, periodic));
        PeriodicWorkers.AddOrUpdate((worker as IPeriodic)!, periodicTask, (_, _) => periodicTask);
    };

    private Action RemoveFromKnownWorkers(BaseWorker worker) => () =>
    {
        _knownWorkers.TryRemove(worker, out _);
    };

}
