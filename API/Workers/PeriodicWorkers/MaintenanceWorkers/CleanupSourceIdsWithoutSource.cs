using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers.MaintenanceWorkers;

public class CleanupSourceIdsWithoutSource : BaseWorkerWithContexts
{
    private readonly IEnumerable<SeriesSource> _connectors;
    private readonly TrangaSettings _settings;

    public CleanupSourceIdsWithoutSource(IEnumerable<SeriesSource> connectors, TrangaSettings settings)
    {
        _connectors = connectors;
        _settings = settings;
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private SeriesContext SeriesContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        SeriesContext = GetContext<SeriesContext>(serviceScope);
    }
    
    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Info("Cleaning up old connector-data...");
        string[] connectorNames = _connectors.Select(c => c.Name).ToArray();
        int deletedChapterIds = await SeriesContext.MangaConnectorToChapter.Where(chId => connectorNames.All(n => n != chId.MangaConnectorName)).ExecuteDeleteAsync(CancellationToken);
        Log.InfoFormat("Deleted {0} chapterIds.", deletedChapterIds);
        
        // Series without Connector get printed to file, to not lose data...
        if (await SeriesContext.MangaConnectorToManga.Include(id => id.Obj) .Where(mcId => connectorNames.All(name => name != mcId.MangaConnectorName)).ToListAsync() is { Count: > 0 } list)
        {
            string filePath = Path.Join(_settings.WorkingDirectory, $"deletedManga-{DateTime.UtcNow.Ticks}.txt");
            Log.DebugFormat("Writing deleted manga to {0}.", filePath);
            await File.WriteAllLinesAsync(filePath, list.Select(id => string.Join('-', id.MangaConnectorName, id.IdOnConnectorSite, id.Obj.Name, id.WebsiteUrl)), CancellationToken);
        }
        int deletedMangaIds = await SeriesContext.MangaConnectorToManga.Where(mcId => connectorNames.All(name => name != mcId.MangaConnectorName)).ExecuteDeleteAsync(CancellationToken);
        Log.InfoFormat("Deleted {0} mangaIds.", deletedMangaIds);
        
        
        if(await SeriesContext.Sync(CancellationToken, GetType(), "Cleanup done") is { success: false } e)
            Log.ErrorFormat("Failed to save database changes: {0}", e.exceptionMessage);
        
        return [];
    }
}