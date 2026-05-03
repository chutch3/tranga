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
    private readonly HashSet<BaseWorker> _knownWorkers = new();
    private readonly ConcurrentDictionary<BaseWorker, Task<BaseWorker[]>> _runningWorkers = new();

    public WorkerQueue(IServiceProvider serviceProvider, TrangaSettings settings)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
    }

    public void AddWorker(BaseWorker worker)
    {
        Log.DebugFormat("Adding Worker {0}", worker);
        _knownWorkers.Add(worker);
        if (worker is not IPeriodic)
            StartWorker(worker, RemoveFromKnownWorkers(worker));
        else
            StartWorker(worker);

        if (worker is IPeriodic periodic)
            AddPeriodicWorker(worker, periodic);
    }

    public void AddWorkers(IEnumerable<BaseWorker> workers)
    {
        foreach (BaseWorker worker in workers)
            AddWorker(worker);
    }

    public BaseWorker[] GetKnownWorkers() => _knownWorkers.ToArray();

    public BaseWorker[] GetRunningWorkers() => _runningWorkers.Keys.ToArray();

    public void StopWorker(BaseWorker worker)
    {
        Log.DebugFormat("Stopping {0}", worker);
        if (worker is IPeriodic periodicWorker)
            PeriodicWorkers.Remove(periodicWorker, out _);
        worker.Cancel();
        _runningWorkers.Remove(worker, out _);
    }

    internal void StartWorker(BaseWorker worker, Action? finishedCallback = null)
    {
        Log.DebugFormat("Starting {0}", worker);
        Action afterWorkCallback = DefaultAfterWork(worker, finishedCallback);

        while (_runningWorkers.Count > _settings.MaxConcurrentWorkers)
        {
            Log.WarnFormat("{0}: Max worker concurrency reached ({1})! Waiting {2}ms...", worker, _settings.MaxConcurrentWorkers, _settings.WorkCycleTimeoutMs);
            Thread.Sleep(_settings.WorkCycleTimeoutMs);
        }

        if (worker is BaseWorkerWithContexts withContexts)
        {
            _runningWorkers.TryAdd(withContexts, withContexts.DoWork(_serviceProvider.CreateScope(), afterWorkCallback));
        }
        else
        {
            _runningWorkers.TryAdd(worker, worker.DoWork(afterWorkCallback));
        }
    }

    private void AddPeriodicWorker(BaseWorker worker, IPeriodic periodic)
    {
        Log.DebugFormat("Adding Periodic {0}", worker);
        Task periodicTask = RefreshedPeriodicTask(worker, periodic);
        PeriodicWorkers.TryAdd((worker as IPeriodic)!, periodicTask);
        periodicTask.Start();
    }

    private Task RefreshedPeriodicTask(BaseWorker worker, IPeriodic periodic) => new(() =>
    {
        Log.DebugFormat("Waiting {0} for next run of {1}", periodic.Interval, worker);
        Thread.Sleep(periodic.Interval);
        StartWorker(worker, RefreshTask(worker, periodic));
    });

    private Action RefreshTask(BaseWorker worker, IPeriodic periodic) => () =>
    {
        if (worker.State < WorkerExecutionState.Created) // Failed
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
        if (_knownWorkers.Contains(worker))
            _knownWorkers.Remove(worker);
    };

    private Action DefaultAfterWork(BaseWorker worker, Action? callback = null) => () =>
    {
        Log.DebugFormat("DefaultAfterWork {0}", worker);
        try
        {
            if (_runningWorkers.TryGetValue(worker, out Task<BaseWorker[]>? task))
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
                }
                else
                    Log.WarnFormat("Children failed: {0}", worker);
            }
            _runningWorkers.Remove(worker, out _);
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
        callback?.Invoke();
    };
}
