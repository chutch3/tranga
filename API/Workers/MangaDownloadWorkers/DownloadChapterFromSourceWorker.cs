using System.Diagnostics.CodeAnalysis;
using API.Acquirers;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.SeriesContext;
using API.Workers.PeriodicWorkers;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.MangaDownloadWorkers;

/// <summary>
/// Downloads a single chapter for a Series by delegating the actual file-fetch+package step to an
/// IChapterAcquirer (defaults to ImageListAcquirer for the historical image-by-image flow). The
/// worker owns resolving the chapter, persisting download state, library refresh decisions, and
/// cover propagation; the acquirer owns producing the .cbz on disk.
/// </summary>
public class DownloadChapterFromSourceWorker(
    SourceId<Chapter> chId,
    IEnumerable<SeriesSource> connectors,
    TrangaSettings settings,
    IChapterAcquirer? acquirer = null,
    IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    public readonly string ChapterIdId = chId.Key;
    private readonly IChapterAcquirer _acquirer = acquirer ?? new ImageListAcquirer(settings);

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
        Log.Debug($"Downloading chapter for SourceId {ChapterIdId}...");
        // Getting SeriesSource info
        if (await SeriesContext.MangaConnectorToChapter
                .Include(id => id.Obj)
                .ThenInclude(c => c.ParentManga)
                .ThenInclude(m => m.Library)
                .FirstOrDefaultAsync(c => c.Key == ChapterIdId, CancellationToken) is not { } mangaConnectorId)
        {
            Log.Error("Could not get SourceId.");
            return [];
        }

        // Check if Chapter already exists...
        if (await mangaConnectorId.Obj.CheckDownloaded(SeriesContext, settings.ChapterNamingScheme, token: CancellationToken))
        {
            Log.Warn("Chapter already exists!");
            return [];
        }

        SeriesSource? seriesSource = connectors.FirstOrDefault(c => c.Name.Equals(mangaConnectorId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (seriesSource is null)
        {
            Log.Error("Could not get SeriesSource.");
            return [];
        }

        Log.Debug($"Downloading chapter for SourceId {mangaConnectorId}...");

        Chapter chapter = mangaConnectorId.Obj;
        if (chapter.ParentManga.LibraryId is null)
        {
            Log.Info($"Library is not set for {chapter.ParentManga} {chapter}");
            return [];
        }

        string saveArchiveFilePath = chapter.GetFullFilepath(settings.ChapterNamingScheme);
        string? directoryPath = Path.GetDirectoryName(saveArchiveFilePath);
        if (directoryPath != null && !Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);

        // Delegate the actual fetch + package step. Acquirer logs its own errors and returns null on failure.
        string? acquiredPath = await _acquirer.AcquireAsync(mangaConnectorId, seriesSource, saveArchiveFilePath, CancellationToken);
        if (acquiredPath is null)
            return [];

        try
        {
            chapter.Downloaded = true;
            chapter.FileName = new FileInfo(acquiredPath).Name;

            Log.Debug($"Downloaded chapter {chapter}.");

            await ActionsContext.Actions.AddAsync(new ChapterDownloadedActionRecord(chapter.ParentManga, chapter));

            // Notification emission has moved to NotifyOnNewDownloadsWorker which observes the
            // ChapterDownloadedActionRecord rows produced here — keeps a single emission point that
            // covers both image-list and torrent download paths.
            var syncTasks = new List<Task<(bool success, string? exceptionMessage)>>
            {
                SeriesContext.Sync(CancellationToken, GetType(), "Download Success"),
                ActionsContext.Sync(CancellationToken, GetType(), "Download Success")
            };
            var results = await Task.WhenAll(syncTasks);
            foreach (var result in results)
            {
                if (!result.success) Log.Error($"Failed to save database changes: {result.exceptionMessage}");
            }

            if (directoryPath != null)
            {
                var sourceIdForSeries = chapter.ParentManga.SourceIds.FirstOrDefault(id => id.MangaConnectorName == seriesSource.Name);
                if (sourceIdForSeries != null)
                    await EnsureCoverInPublicationFolder(chapter.ParentManga, seriesSource, sourceIdForSeries, directoryPath);
            }
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Failed to finalise chapter {0}: {1}", chapter, ex);
            return []; // Fail early!
        }

        bool refreshLibrary = await CheckLibraryRefresh();
        if (refreshLibrary)
            Log.Info($"Condition {settings.LibraryRefreshSetting} met.");
        return refreshLibrary ? [new RefreshLibrariesWorker()] : [];
    }

    private async Task EnsureCoverInPublicationFolder(Series manga, SeriesSource seriesSource, SourceId<Series> mangaConnectorId, string publicationFolder)
    {
        if (File.Exists(Path.Join(publicationFolder, "cover.jpg"))) return;

        string? coverFileNameInCache = manga.CoverFileNameInCache;
        if (coverFileNameInCache is null)
        {
            Log.Debug("Cover filename in cache is null. Attempting to download...");
            coverFileNameInCache = await seriesSource.SaveCoverImageToCache(mangaConnectorId);
            manga.CoverFileNameInCache = coverFileNameInCache;
            if (await SeriesContext.Sync(CancellationToken, reason: "Update cover filename") is { success: false } result)
                Log.Error($"Couldn't update cover filename {result.exceptionMessage}");
        }

        if (coverFileNameInCache is null)
        {
            Log.Error("Could not retrieve cover image cache filename.");
            return;
        }

        string fullCoverPath = Path.Join(settings.CoverImageCacheOriginal, coverFileNameInCache);
        if (!File.Exists(fullCoverPath))
        {
            Log.Error($"Cached cover file {fullCoverPath} does not exist.");
            return;
        }

        string extension = Path.GetExtension(coverFileNameInCache);
        string newFilePath = Path.Join(publicationFolder, $"cover{extension}");
        File.Copy(fullCoverPath, newFilePath, true);
        Log.Debug($"Copied cover from {fullCoverPath} to {newFilePath}");
    }

    private async Task<bool> CheckLibraryRefresh() => settings.LibraryRefreshSetting switch
    {
        LibraryRefreshSetting.AfterAllFinished => await AllDownloadsFinished(),
        LibraryRefreshSetting.AfterMangaFinished => await SeriesContext.MangaConnectorToChapter.Include(chId => chId.Obj).Where(chId => chId.UseForDownload).AllAsync(chId => chId.Obj.Downloaded, CancellationToken),
        LibraryRefreshSetting.AfterEveryChapter => true,
        LibraryRefreshSetting.WhileDownloading => await AllDownloadsFinished() || DateTime.UtcNow.Subtract(RefreshLibrariesWorker.LastRefresh).TotalMinutes > settings.RefreshLibraryWhileDownloadingEveryMinutes,
        _ => true
    };
    private async Task<bool> AllDownloadsFinished() => (await StartNewChapterDownloadsWorker.GetMissingChapters(SeriesContext, CancellationToken)).Count == 0;

    public override string ToString() => $"{base.ToString()} {ChapterIdId}";
}
