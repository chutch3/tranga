using API.Schema.SeriesContext;
using API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace API.Workers.MaintenanceWorkers;

/// <summary>
/// Worker that fetches the MangaDex aggregate for a manga's confirmed external ID,
/// updates chapter VolumeNumbers using the mapping, and sets MetadataConfidence = Exact
/// on all resolved chapters. Updates LastSyncedAt on completion.
/// </summary>
public class RefreshMetadataSourceWorker(
    string mangaId,
    IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    private SeriesContext _mangaContext = null!;
    private IMangaDexSearchService _searchService = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<SeriesContext>(serviceScope);
        _searchService = serviceScope.ServiceProvider.GetRequiredService<IMangaDexSearchService>();
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        var manga = await _mangaContext.Series
            .Include(m => m.MetadataSource)
            .Include(m => m.Chapters)
            .FirstOrDefaultAsync(m => m.Key == mangaId, CancellationToken);

        if (manga is null)
        {
            Log.WarnFormat("Series {0} not found; skipping refresh.", mangaId);
            return [];
        }

        if (manga.MetadataSource is not { Status: MetadataSourceStatus.Confirmed, ExternalId: { Length: > 0 } externalId })
        {
            Log.WarnFormat("Series {0} has no confirmed external ID; skipping refresh.", mangaId);
            return [];
        }

        Dictionary<string, int> map;
        try
        {
            map = await _searchService.GetChapterToVolumeMapAsync(externalId, CancellationToken);
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Failed to fetch volume map for manga {0} (externalId={1}): {2}", mangaId, externalId, ex.Message);
            return [];
        }

        if (map.Count == 0)
        {
            Log.InfoFormat("No chapter-volume mapping returned for manga {0}.", mangaId);
            return [];
        }

        int updated = 0;
        foreach (var chapter in manga.Chapters)
        {
            if (map.TryGetValue(chapter.ChapterNumber, out int vol))
            {
                chapter.VolumeNumber = vol;
                chapter.MetadataConfidence = MetadataConfidence.Exact;
                updated++;
            }
        }

        manga.MetadataSource.LastSyncedAt = DateTime.UtcNow;

        if (await _mangaContext.Sync(CancellationToken, GetType(), nameof(DoWorkInternal)) is { success: false } err)
            Log.ErrorFormat("Failed to save refresh results for manga {0}: {1}", mangaId, err.exceptionMessage);
        else
            Log.InfoFormat("Refreshed {0} chapters for manga {1}.", updated, mangaId);

        return [];
    }
}
