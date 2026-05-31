using System.Diagnostics.CodeAnalysis;
using API.Acquirers;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.SeriesContext;
using API.TorrentClients;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.PeriodicWorkers;

/// <summary>
/// Polls the torrent client for every undownloaded chapter whose source is Kind=Torrent. When a
/// torrent has completed, finds the .cbz inside its save directory, moves it into the chapter's
/// publication folder, marks the chapter downloaded, and removes the torrent from the client.
/// </summary>
public class TorrentCompletionWorker(
    ITorrentClient torrentClient,
    IEnumerable<SeriesSource> connectors,
    TrangaSettings settings,
    TimeSpan? interval = null,
    IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    public DateTime LastExecution { get; set; } = DateTime.UnixEpoch;
    public TimeSpan Interval { get; set; } = interval ?? TimeSpan.FromSeconds(30);

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
        var torrentSourceNames = connectors
            .Where(c => c.Kind == AcquisitionKind.Torrent)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (torrentSourceNames.Count == 0)
            return [];

        // Pull only the candidate chapters: !Downloaded, UseForDownload, and source is torrent-kind.
        List<SourceId<Chapter>> pending = await SeriesContext.MangaConnectorToChapter
            .Include(id => id.Obj).ThenInclude(c => c.ParentManga).ThenInclude(m => m.Library)
            .Where(id => !id.Obj.Downloaded && id.UseForDownload)
            .ToListAsync(CancellationToken);

        foreach (SourceId<Chapter> chId in pending)
        {
            if (!torrentSourceNames.Contains(chId.MangaConnectorName)) continue;

            TorrentStatus? status = await torrentClient.GetStatus(chId.Key, CancellationToken);
            if (status is not TorrentStatus.Completed completed) continue;

            await FinaliseAsync(chId, completed.SavePath);
        }
        return [];
    }

    private async Task FinaliseAsync(SourceId<Chapter> chId, string savePath)
    {
        Chapter chapter = chId.Obj;
        if (chapter.ParentManga.LibraryId is null)
        {
            Log.WarnFormat("Torrent for {0} completed but chapter has no library assigned; skipping.", chapter);
            return;
        }

        if (!Directory.Exists(savePath))
        {
            Log.ErrorFormat("Torrent reports completion at {0} but the directory does not exist.", savePath);
            return;
        }

        string? archive = Directory.EnumerateFiles(savePath, "*.cbz", SearchOption.AllDirectories).FirstOrDefault();
        if (archive is null)
        {
            Log.ErrorFormat("Torrent completed at {0} but contains no .cbz; cannot finalise {1}.", savePath, chapter);
            return;
        }

        string targetPath = chapter.GetFullFilepath(settings.ChapterNamingScheme);
        string? targetDir = Path.GetDirectoryName(targetPath);
        if (targetDir != null && !Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        try
        {
            File.Move(archive, targetPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Failed to move {0} → {1}: {2}", archive, targetPath, ex);
            return;
        }

        chapter.Downloaded = true;
        chapter.FileName = new FileInfo(targetPath).Name;

        await ActionsContext.Actions.AddAsync(new ChapterDownloadedActionRecord(chapter.ParentManga, chapter), CancellationToken);

        // NOTE: notification emission deferred — observers of ChapterDownloadedActionRecord (e.g. a
        // future NotifyOnNewActionsWorker) can fan out notifications in one place for both image-list
        // and torrent download paths.
        var syncs = await Task.WhenAll(
            SeriesContext.Sync(CancellationToken, GetType(), "Torrent finalised"),
            ActionsContext.Sync(CancellationToken, GetType(), "Torrent finalised"));
        foreach (var s in syncs)
            if (!s.success) Log.ErrorFormat("Sync failed during torrent finalise: {0}", s.exceptionMessage);

        // Hand the torrent back to qBittorrent for removal; keep the seeded data on disk in case
        // the user has ratio targets. The .cbz itself we already moved out.
        await torrentClient.Remove(chId.Key, deleteData: false, CancellationToken);

        Log.InfoFormat("Finalised torrent for {0} ch.{1} → {2}", chapter.ParentManga.Name, chapter.ChapterNumber, targetPath);
    }
}
