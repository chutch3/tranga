using System.Collections.Concurrent;

namespace API.Workers;

public interface IBatchWorkerFactory<TItem>
{
    BaseWorker Create(ConcurrentQueue<TItem> queue);
}
