using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.MangaContext;
using API.Schema.NotificationsContext;
using API.Workers.PeriodicWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Binarization;
using static System.IO.UnixFileMode;

namespace API.Workers.MangaDownloadWorkers;

/// <summary>
/// Downloads single chapter for Manga from Mangaconnector
/// </summary>
/// <param name="chId"></param>
/// <param name="dependsOn"></param>
public class DownloadChapterFromMangaconnectorWorker(MangaConnectorId<Chapter> chId, IEnumerable<MangaConnector> connectors, TrangaSettings settings, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    public readonly string ChapterIdId = chId.Key;

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private MangaContext MangaContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private ActionsContext ActionsContext = null!;
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    private NotificationsContext NotificationsContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        MangaContext = GetContext<MangaContext>(serviceScope);
        ActionsContext = GetContext<ActionsContext>(serviceScope);
        NotificationsContext = GetContext<NotificationsContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Debug($"Downloading chapter for MangaConnectorId {ChapterIdId}...");
        // Getting MangaConnector info
        if (await MangaContext.MangaConnectorToChapter
                .Include(id => id.Obj)
                .ThenInclude(c => c.ParentManga)
                .ThenInclude(m => m.Library)
                .FirstOrDefaultAsync(c => c.Key == ChapterIdId, CancellationToken) is not { } mangaConnectorId)
        {
            Log.Error("Could not get MangaConnectorId.");
            return [];
        }

        // Check if Chapter already exists...
        if (await mangaConnectorId.Obj.CheckDownloaded(MangaContext, settings.ChapterNamingScheme, token: CancellationToken))
        {
            Log.Warn("Chapter already exists!");
            return [];
        }

        MangaConnector? mangaConnector = connectors.FirstOrDefault(c => c.Name.Equals(mangaConnectorId.MangaConnectorName, StringComparison.InvariantCultureIgnoreCase));
        if (mangaConnector is null)
        {
            Log.Error("Could not get MangaConnector.");
            return [];
        }

        Log.Debug($"Downloading chapter for MangaConnectorId {mangaConnectorId}...");

        Chapter chapter = mangaConnectorId.Obj;
        if (chapter.ParentManga.LibraryId is null)
        {
            Log.Info($"Library is not set for {chapter.ParentManga} {chapter}");
            return [];
        }

        List<Stream> images = new();
        try
        {
            string[] imageUrls = await mangaConnector.GetChapterImageUrls(mangaConnectorId);
            foreach (string imageUrl in imageUrls)
            {
                Stream? imageStream = await mangaConnector.DownloadImage(imageUrl, CancellationToken);
                if (imageStream is not null)
                    images.Add(await ProcessImage(imageStream, CancellationToken));
            }

            Log.Debug($"Images downloaded for chapter {chapter}. Packaging...");

            string saveArchiveFilePath = chapter.GetFullFilepath(settings.ChapterNamingScheme);
            string? directoryPath = Path.GetDirectoryName(saveArchiveFilePath);
            if (directoryPath != null && !Directory.Exists(directoryPath))
                Directory.CreateDirectory(directoryPath);

            //ZIP-it and ship-it
            using (ZipArchive archive = ZipFile.Open(saveArchiveFilePath, ZipArchiveMode.Create))
            {
                if (Constants.CreateComicInfoXml)
                {
                    Log.Debug("Writing ComicInfo.xml");
                    Stream comicStream = archive.CreateEntry("ComicInfo.xml").Open();
                    string comicInfo = chapter.GetComicInfoXmlString();
                    await comicStream.WriteAsync(Encoding.UTF8.GetBytes(comicInfo), CancellationToken);
                    await comicStream.DisposeAsync();
                }

                for (int i = 0; i < images.Count; i++)
                {
                    Log.Debug($"Packaging images to archive {chapter} , image {i}");
                    Stream zipStream = archive.CreateEntry($"{i}.jpg").Open();
                    Stream imageStream = images[i];
                    imageStream.Position = 0;
                    await imageStream.CopyToAsync(zipStream, CancellationToken);
                    await zipStream.DisposeAsync();
                }
            }

            chapter.Downloaded = true;
            chapter.FileName = new FileInfo(saveArchiveFilePath).Name;
            // Sync moved to end

            Log.Debug($"Downloaded chapter {chapter}.");

            await ActionsContext.Actions.AddAsync(new ChapterDownloadedActionRecord(chapter.ParentManga, chapter));
            if (await ActionsContext.Sync(CancellationToken, GetType(), "Download complete") is { success: false } actionsContextException)
                Log.Error($"Failed to save database changes: {actionsContextException.exceptionMessage}");

            await NotificationsContext.Notifications.AddAsync(new Notification(
                "Chapter downloaded",
                $"{chapter.ParentManga.Name} Ch. {chapter.ChapterNumber} - {chapter.FileName}"
                ), CancellationToken);

            // Consolidated sync for all contexts
            var syncTasks = new List<Task<(bool success, string? exceptionMessage)>>
            {
                MangaContext.Sync(CancellationToken, GetType(), "Download Success"),
                ActionsContext.Sync(CancellationToken, GetType(), "Download Success"),
                NotificationsContext.Sync(CancellationToken, GetType(), "Download Success")
            };
            var results = await Task.WhenAll(syncTasks);
            foreach (var result in results)
            {
                if (!result.success) Log.Error($"Failed to save database changes: {result.exceptionMessage}");
            }
            
            if (directoryPath != null)
            {
                var mangaConnectorIdForManga = chapter.ParentManga.MangaConnectorIds.FirstOrDefault(id => id.MangaConnectorName == mangaConnector.Name);
                if (mangaConnectorIdForManga != null)
                    await EnsureCoverInPublicationFolder(chapter.ParentManga, mangaConnector, mangaConnectorIdForManga, directoryPath);
            }
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Failed to download chapter {0}: {1}", chapter, ex);
            return []; // Fail early!
        }
        finally
        {
            images.ForEach(i => i.Dispose());
        }

        bool refreshLibrary = await CheckLibraryRefresh();
        if (refreshLibrary)
            Log.Info($"Condition {settings.LibraryRefreshSetting} met.");
        return refreshLibrary ? [new RefreshLibrariesWorker()] : [];
    }

    private async Task EnsureCoverInPublicationFolder(Manga manga, MangaConnector mangaConnector, MangaConnectorId<Manga> mangaConnectorId, string publicationFolder)
    {
        if (File.Exists(Path.Join(publicationFolder, "cover.jpg"))) return;
        
        string? coverFileNameInCache = manga.CoverFileNameInCache;
        if (coverFileNameInCache is null)
        {
            Log.Debug("Cover filename in cache is null. Attempting to download...");
            coverFileNameInCache = await mangaConnector.SaveCoverImageToCache(mangaConnectorId);
            manga.CoverFileNameInCache = coverFileNameInCache;
            if (await MangaContext.Sync(CancellationToken, reason: "Update cover filename") is { success: false } result)
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
        LibraryRefreshSetting.AfterMangaFinished => await MangaContext.MangaConnectorToChapter.Include(chId => chId.Obj).Where(chId => chId.UseForDownload).AllAsync(chId => chId.Obj.Downloaded, CancellationToken),
        LibraryRefreshSetting.AfterEveryChapter => true,
        LibraryRefreshSetting.WhileDownloading => await AllDownloadsFinished() || DateTime.UtcNow.Subtract(RefreshLibrariesWorker.LastRefresh).TotalMinutes > settings.RefreshLibraryWhileDownloadingEveryMinutes,
        _ => true
    };
    private async Task<bool> AllDownloadsFinished() => (await StartNewChapterDownloadsWorker.GetMissingChapters(MangaContext, CancellationToken)).Count == 0;

    private async Task<Stream> ProcessImage(Stream imageStream, CancellationToken? cancellationToken = null)
    {
        Log.Debug("Processing image");
        imageStream.Position = 0;
        if (!settings.BlackWhiteImages && settings.ImageCompression == 100)
        {
            Log.Debug("No processing requested for image");
            return imageStream;
        }

        MemoryStream processedImage = new();
        try
        {
            using Image image = await Image.LoadAsync(imageStream, cancellationToken ?? CancellationToken.None);
            Log.Debug("Image loaded");
            if (settings.BlackWhiteImages)
                image.Mutate(i => i.ApplyProcessor(new AdaptiveThresholdProcessor()));
            await image.SaveAsJpegAsync(processedImage, new JpegEncoder()
            {
                Quality = settings.ImageCompression
            }, cancellationToken ?? CancellationToken.None);
            Log.Debug("Image processed");
        }
        catch (Exception e)
        {
            Log.Error(e);
            return imageStream;
        }
        processedImage.Position = 0;
        return processedImage;
    }

    public override string ToString() => $"{base.ToString()} {ChapterIdId}";
}
