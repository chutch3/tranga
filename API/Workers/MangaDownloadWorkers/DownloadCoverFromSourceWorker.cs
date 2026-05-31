using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.MangaDownloadWorkers;

/// <summary>
/// Downloads the cover for Series from Mangaconnector
/// </summary>
public class DownloadCoverFromSourceWorker(SourceId<Series> mcId, IEnumerable<SeriesSource> connectors, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    private readonly string _mangaConnectorIdId = mcId.Key;

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
        Log.Debug($"Getting Cover for SourceId {_mangaConnectorIdId}...");
        // Getting SeriesSource info
        if (await SeriesContext.MangaConnectorToManga
                .Include(id => id.Obj)
                .FirstOrDefaultAsync(c => c.Key == _mangaConnectorIdId, CancellationToken) is not { } mangaConnectorId)
        {
            Log.Error("Could not get SourceId.");
            return []; //TODO Exception?
        }
        SeriesSource? seriesSource = connectors.FirstOrDefault(c => c.Name.Equals(mangaConnectorId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (seriesSource is null)
        {
            Log.Error("Could not get SeriesSource.");
            return []; //TODO Exception?
        }
        Log.Debug($"Getting Cover for SourceId {mangaConnectorId}...");

        string? coverFileName = await seriesSource.SaveCoverImageToCache(mangaConnectorId);
        if (coverFileName is null)
        {
            Log.Error($"Could not get Cover for SourceId {mangaConnectorId}.");
            return [];
        }
        
        await SeriesContext.Entry(mangaConnectorId).Reference(m => m.Obj).LoadAsync(CancellationToken);
        mangaConnectorId.Obj.CoverFileNameInCache = coverFileName;

        if(await SeriesContext.Sync(CancellationToken, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } mangaContextException)
            Log.Error($"Failed to save database changes: {mangaContextException.exceptionMessage}");
        
        ActionsContext.Actions.Add(new CoverDownloadedActionRecord(mcId.Obj, coverFileName));
        if(await ActionsContext.Sync(CancellationToken, GetType(), "Download complete") is { success: false } actionsContextException)
            Log.Error($"Failed to save database changes: {actionsContextException.exceptionMessage}");
        
        return [];
    }
    
    public override string ToString() => $"{base.ToString()} {_mangaConnectorIdId}";
}