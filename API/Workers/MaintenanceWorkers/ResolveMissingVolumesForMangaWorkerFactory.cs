using System.Collections.Concurrent;

namespace API.Workers.MaintenanceWorkers;

public class ResolveMissingVolumesForMangaWorkerFactory(TrangaSettings settings, IMangaDexVolumeResolver resolver)
    : IBatchWorkerFactory<string>
{
    public BaseWorker Create(ConcurrentQueue<string> queue) =>
        new ResolveMissingVolumesForMangaWorker(queue, settings, resolver);
}
