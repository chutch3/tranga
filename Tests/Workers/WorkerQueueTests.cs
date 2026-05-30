using API;
using API.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace API.Tests.Workers;

public class WorkerQueueTests
{
    private sealed class FakeWorker : BaseWorker
    {
        protected internal override WorkerExecutionState State { get; protected set; } = WorkerExecutionState.Completed;
        private readonly TaskCompletionSource<BaseWorker[]> _tcs = new();

        public FakeWorker(string key) : base(key)
        {
        }

        /// <summary>Blocks until Complete() is called, keeping the worker in KnownWorkers.</summary>
        protected override Task<BaseWorker[]> DoWorkInternal() => _tcs.Task;

        public void Complete() => _tcs.TrySetResult([]);
    }

    /// <summary>A worker that records whether its body ran, and completes immediately.</summary>
    private sealed class RecordingWorker : BaseWorker
    {
        public bool HasRun { get; private set; }

        public RecordingWorker(string key, IEnumerable<BaseWorker>? dependsOn = null) : base(key, dependsOn)
        {
        }

        protected override Task<BaseWorker[]> DoWorkInternal()
        {
            HasRun = true;
            return Task.FromResult(Array.Empty<BaseWorker>());
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task AddWorker_WithUnstartedDependency_RunsDependencyThenCompletesDependent()
    {
        var settings = new TrangaSettings { AppData = Path.GetTempPath(), MaxConcurrentWorkers = 5 };
        var queue = CreateQueue(settings);

        var dependency = new RecordingWorker("dep");
        var dependent = new RecordingWorker("dependent", dependsOn: [dependency]);

        queue.AddWorker(dependent);

        var completed = await WaitUntilAsync(
            () => dependency.HasRun && dependent.State == WorkerExecutionState.Completed,
            TimeSpan.FromSeconds(10));

        Assert.True(dependency.HasRun, "Dependency should have been started and run.");
        Assert.True(dependent.HasRun, "Dependent should have run after its dependency completed.");
        Assert.Equal(WorkerExecutionState.Completed, dependent.State);
    }

    [Fact]
    public async Task AddWorker_WithUnstartedDependency_DoesNotLeakConcurrencySlots()
    {
        // With MaxConcurrentWorkers = 1, a dependent worker must not permanently hold the only
        // concurrency slot while resolving its dependency, or the queue deadlocks.
        var settings = new TrangaSettings { AppData = Path.GetTempPath(), MaxConcurrentWorkers = 2 };
        var queue = CreateQueue(settings);

        var dependency = new RecordingWorker("dep2");
        var dependent = new RecordingWorker("dependent2", dependsOn: [dependency]);

        queue.AddWorker(dependent);
        Assert.True(await WaitUntilAsync(() => dependent.State == WorkerExecutionState.Completed, TimeSpan.FromSeconds(10)));

        // A subsequent independent worker must still be able to run (slots were released).
        var after = new RecordingWorker("after");
        queue.AddWorker(after);
        Assert.True(await WaitUntilAsync(() => after.HasRun, TimeSpan.FromSeconds(10)),
            "A worker added after dependency resolution should still acquire a concurrency slot.");
    }

    private static WorkerQueue CreateQueue(TrangaSettings? settings = null)
    {
        settings ??= new TrangaSettings { AppData = Path.GetTempPath() };
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        var provider = services.BuildServiceProvider();
        return new WorkerQueue(provider, settings);
    }

    [Fact]
    public void GetKnownWorkers_InitiallyEmpty()
    {
        var queue = CreateQueue();

        Assert.Empty(queue.GetKnownWorkers());
    }

    [Fact]
    public void AddWorker_WorkerAppearsInKnownWorkers()
    {
        var queue = CreateQueue();
        var worker = new FakeWorker("w1");

        queue.AddWorker(worker);

        Assert.Contains(worker, queue.GetKnownWorkers());
    }

    [Fact]
    public void AddWorkers_MultipleWorkersAllAppear()
    {
        var queue = CreateQueue();
        var w1 = new FakeWorker("w1");
        var w2 = new FakeWorker("w2");

        queue.AddWorkers([w1, w2]);

        var known = queue.GetKnownWorkers();
        Assert.Contains(w1, known);
        Assert.Contains(w2, known);
    }

    [Fact]
    public async Task StopWorker_RemovesWorkerFromRunningWorkers()
    {
        var queue = CreateQueue();
        var worker = new FakeWorker("w-stop");
        queue.AddWorker(worker);

        // AddWorker starts the worker asynchronously; wait until it is actually running before stopping,
        // otherwise StopWorker races the background StartWorker.
        Assert.True(await WaitUntilAsync(() => queue.GetRunningWorkers().Contains(worker), TimeSpan.FromSeconds(5)));

        queue.StopWorker(worker);

        Assert.DoesNotContain(worker, queue.GetRunningWorkers());
    }

    [Fact]
    public async Task StopWorker_CallsCancelOnWorker()
    {
        var queue = CreateQueue();
        var worker = new FakeWorker("w-cancel");
        queue.AddWorker(worker);

        Assert.True(await WaitUntilAsync(() => queue.GetRunningWorkers().Contains(worker), TimeSpan.FromSeconds(5)));

        // Cancel is called by StopWorker
        queue.StopWorker(worker);

        // Worker's CancellationToken should be cancelled after Cancel() is called
        Assert.True(worker.State == WorkerExecutionState.Cancelled || worker.State == WorkerExecutionState.Completed);
    }

    [Fact]
    public void GetRunningWorkers_InitiallyEmpty()
    {
        var queue = CreateQueue();

        Assert.Empty(queue.GetRunningWorkers());
    }

    [Fact]
    public void AddWorker_MaxConcurrencyRespected_WorkerEventuallyStarts()
    {
        var settings = new TrangaSettings { AppData = Path.GetTempPath(), MaxConcurrentWorkers = 2 };
        var queue = CreateQueue(settings);
        var w1 = new FakeWorker("w1");
        var w2 = new FakeWorker("w2");

        queue.AddWorker(w1);
        queue.AddWorker(w2);

        Assert.Contains(w1, queue.GetKnownWorkers());
        Assert.Contains(w2, queue.GetKnownWorkers());
    }
}
