using System.Diagnostics.CodeAnalysis;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.MangaDownloadWorkers;

/// <summary>
/// Retrieves the metadata of available chapters on the Mangaconnector
/// </summary>
/// <param name="mcId"></param>
/// <param name="language"></param>
/// <param name="dependsOn"></param>
public class RetrieveChaptersFromSourceWorker(SourceId<Series> mcId, string language, IEnumerable<SeriesSource> connectors, IEnumerable<BaseWorker>? dependsOn = null)
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
        Log.DebugFormat("Getting Chapters for SourceId {0}...", _mangaConnectorIdId);
        // Getting SeriesSource info
        if (await SeriesContext.MangaConnectorToManga
                .Include(id => id.Obj)
                .ThenInclude(m => m.Chapters)
                .ThenInclude(ch => ch.SourceIds)
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
        Log.DebugFormat("Getting Chapters for SourceId {0}...", mangaConnectorId);
        
        Series manga = mangaConnectorId.Obj;
        
        // Retrieve available Chapters from Connector
        (Chapter chapter, SourceId<Chapter> chapterId)[] allChapters =
            (await seriesSource.GetChapters(mangaConnectorId, language)).DistinctBy(c => c.Item1.Key).ToArray();
        Log.DebugFormat("Got {0} chapters from connector.", allChapters.Length);
        
        // Filter for new Chapters
        List<(Chapter chapter, SourceId<Chapter> chapterId)> newChapters = allChapters.Where<(Chapter chapter, SourceId<Chapter> chapterId)>(ch =>
            manga.Chapters.All(c => c.Key != ch.chapter.Key)).ToList();
        Log.DebugFormat("Got {0} new chapters.", newChapters.Count);

        // Update existing chapters with metadata if it was missing
        foreach(var (fetchedChapter, _) in allChapters)
        {
            var existingChapter = manga.Chapters.FirstOrDefault(c => c.Key == fetchedChapter.Key);
            if (existingChapter != null && existingChapter.VolumeNumber == null && fetchedChapter.VolumeNumber != null)
            {
                existingChapter.VolumeNumber = fetchedChapter.VolumeNumber;
                Log.DebugFormat("Updated volume for existing chapter {0} to {1}", existingChapter.ChapterNumber, existingChapter.VolumeNumber);
            }
        }

        // Add Chapters to Series
        manga.Chapters = manga.Chapters.Union(newChapters.Select(ch => ch.chapter)).ToList();
        
        // Filter for new ChapterIds
        List<SourceId<Chapter>> existingChapterIds = manga.Chapters.SelectMany(c => c.SourceIds).ToList();
        List<SourceId<Chapter>> newIds = allChapters.Select(ch => ch.chapterId)
            .Where(newCh => !existingChapterIds.Any(existing =>
                existing.MangaConnectorName == newCh.MangaConnectorName &&
                existing.IdOnConnectorSite == newCh.IdOnConnectorSite))
            .ToList();
        // Match tracked entities of Chapters
        foreach (SourceId<Chapter> newId in newIds)
            newId.Obj = manga.Chapters.First(ch => ch.Key == newId.ObjId);
        Log.DebugFormat("Got {0} new download-Ids.", newIds.Count);
        
        // Add new ChapterIds to Database
        SeriesContext.MangaConnectorToChapter.AddRange(newIds);

        // If Series is marked for Download from Connector, mark the new Chapters as UseForDownload
        if (mangaConnectorId.UseForDownload)
        {
            foreach ((Chapter _, SourceId<Chapter> chapterId) in newChapters)
            {
                chapterId.UseForDownload = mangaConnectorId.UseForDownload;
            }
        }

        if(await SeriesContext.Sync(CancellationToken, GetType(), "Chapters retrieved") is { success: false } mangaContextException)
            Log.ErrorFormat("Failed to save database changes: {0}", mangaContextException.exceptionMessage);

        ActionsContext.Actions.Add(new ChaptersRetrievedActionRecord(manga));
        if(await ActionsContext.Sync(CancellationToken, GetType(), "Chapters retrieved") is { success: false } actionsContextException)
            Log.ErrorFormat("Failed to save database changes: {0}", actionsContextException.exceptionMessage);

        return [];
    }

    public override string ToString() => $"{base.ToString()} {_mangaConnectorIdId}";
}