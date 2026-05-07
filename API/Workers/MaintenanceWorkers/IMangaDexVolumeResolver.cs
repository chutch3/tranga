using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using API.Schema.MangaContext;

namespace API.Workers.MaintenanceWorkers;

public interface IMangaDexVolumeResolver
{
    Task<Dictionary<string, int>> GetChapterToVolumeMapAsync(Manga manga, CancellationToken cancellationToken = default);
}
