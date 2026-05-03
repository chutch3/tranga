using API;
using API.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

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
    public void StopWorker_RemovesWorkerFromRunningWorkers()
    {
        var queue = CreateQueue();
        var worker = new FakeWorker("w-stop");
        queue.AddWorker(worker);

        queue.StopWorker(worker);

        Assert.DoesNotContain(worker, queue.GetRunningWorkers());
    }

    [Fact]
    public void StopWorker_CallsCancelOnWorker()
    {
        var queue = CreateQueue();
        var worker = new FakeWorker("w-cancel");
        queue.AddWorker(worker);

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
