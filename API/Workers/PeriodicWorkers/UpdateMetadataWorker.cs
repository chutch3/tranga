using System.Diagnostics.CodeAnalysis;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.SeriesContext;
using API.Schema.SeriesContext.MetadataFetchers;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Updates Metadata for all Series
/// </summary>
/// <param name="metadataFetchers"></param>
/// <param name="interval"></param>
/// <param name="dependsOn"></param>
public class UpdateMetadataWorker(IEnumerable<MetadataFetcher> metadataFetchers, TimeSpan? interval = null, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{

    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromHours(12);

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private SeriesContext SeriesContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private ActionsContext ActionsContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        SeriesContext = GetContext<SeriesContext>(serviceScope);
        ActionsContext = GetContext<ActionsContext>(serviceScope);
    }
    
    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Debug("Updating metadata...");
        // Get MetadataEntries of Series marked for download
        List<MetadataEntry> metadataEntriesToUpdate = await SeriesContext.MangaConnectorToManga
            .Where(m => m.UseForDownload) // Get marked Series
            .Join(
                SeriesContext.MetadataEntries.Include(e => e.Series),
                mcId => mcId.ObjId,
                e => e.MangaId,
                (mcId, e) => e) // return MetadataEntry
            .ToListAsync(CancellationToken);
        Log.DebugFormat("Updating metadata of {0} manga...", metadataEntriesToUpdate.Count);

        foreach (MetadataEntry metadataEntry in metadataEntriesToUpdate)
        {
            Log.DebugFormat("Updating metadata of {0}...", metadataEntry);
            if(metadataFetchers.FirstOrDefault(f => f.Name == metadataEntry.MetadataFetcherName) is not { } fetcher)
                continue;
            await fetcher.UpdateMetadata(metadataEntry, SeriesContext, CancellationToken);
            ActionsContext.Actions.Add(new MetadataUpdatedActionRecord(metadataEntry.Series, fetcher));
        }
        Log.Debug("Updated metadata.");

        if(await SeriesContext.Sync(CancellationToken, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } e)
            Log.ErrorFormat("Failed to save database changes: {0}", e.exceptionMessage);
        
        if(await ActionsContext.Sync(CancellationToken, GetType(), "Metadata Updated") is { success: false } actionsContextException)
            Log.ErrorFormat("Failed to save database changes: {0}", actionsContextException.exceptionMessage);

        return [];
    }
}