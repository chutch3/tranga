using System.Collections.Concurrent;
using API.Services;

namespace API.Workers.MaintenanceWorkers;

public class ResolveMissingVolumesForMangaWorkerFactory(
    TrangaSettings settings,
    IMangaDexVolumeResolver resolver,
    IMangaDexSearchService searchService)
    : IBatchWorkerFactory<string>
{
    public BaseWorker Create(ConcurrentQueue<string> queue) =>
        new ResolveMissingVolumesForMangaWorker(queue, settings, resolver, searchService);
}
