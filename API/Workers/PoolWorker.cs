using System.Collections.Concurrent;

namespace API.Workers;

public abstract class PoolWorker<TItem>(ConcurrentQueue<TItem> queue, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    private readonly ConcurrentQueue<TItem> _queue = queue;

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        List<BaseWorker> results = new();
        while (_queue.TryDequeue(out TItem? item))
            results.AddRange(await ProcessItem(item));
        return results.ToArray();
    }

    protected abstract Task<IEnumerable<BaseWorker>> ProcessItem(TItem item);
}
