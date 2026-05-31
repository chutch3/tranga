using System.IO.Compression;
using System.Text;
using API.Schema.SeriesContext;
using API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace API.Workers.MaintenanceWorkers;

public class BundleVolumeWorker(string mangaId, int volumeNumber, TrangaSettings settings, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    private SeriesContext _mangaContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<SeriesContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        var volumeMetadata = await _mangaContext.VolumeMetadata
            .Include(v => v.Series)
            .ThenInclude(m => m.Library)
            .FirstOrDefaultAsync(v => v.MangaId == mangaId && v.VolumeNumber == volumeNumber, CancellationToken);

        if (volumeMetadata is null)
        {
            Log.Warn($"VolumeMetadata not found for manga {mangaId} volume {volumeNumber}");
            return [];
        }

        var manga = volumeMetadata.Series;

        var chapters = await _mangaContext.Chapters
            .Where(c => c.ParentMangaId == mangaId
                        && c.VolumeNumber == volumeNumber
                        && !c.IsBundled
                        && c.FileName != null)
            .ToListAsync(CancellationToken);

        if (chapters.Count == 0)
        {
            Log.Info($"No unbundled chapters found for manga {mangaId} volume {volumeNumber}");
            return [];
        }

        // Sort by ChapterNumber (parse as float for natural sort)
        chapters = chapters
            .OrderBy(c => float.TryParse(c.ChapterNumber, out float v) ? v : float.MaxValue)
            .ThenBy(c => c.ChapterNumber)
            .ToList();

        string outputPath = Path.Join(manga.FullDirectoryPath, $"Vol {volumeNumber}.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // Track: chapter key → (original file path, startPage, pageCount)
        var chapterInfo = new List<(Chapter chapter, string originalPath, int startPage, int pageCount)>();
        int globalOffset = 0;

        try
        {
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            using var outputZip = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false);

            foreach (var chapter in chapters)
            {
                string chapterPath = Path.Join(manga.FullDirectoryPath, chapter.FileName!);
                if (!File.Exists(chapterPath))
                {
                    Log.Warn($"Chapter file not found: {chapterPath}; skipping chapter {chapter.ChapterNumber}");
                    continue;
                }

                using var chapterZip = ZipFile.OpenRead(chapterPath);
                var imageEntries = chapterZip.Entries
                    .Where(e => IsImageEntry(e.FullName))
                    .OrderBy(e => e.Name, NaturalSortComparer.Instance)
                    .ToList();

                int startPage = globalOffset;
                int pageCount = imageEntries.Count;

                for (int i = 0; i < imageEntries.Count; i++)
                {
                    string ext = Path.GetExtension(imageEntries[i].Name).ToLowerInvariant();
                    string entryName = $"{globalOffset + i:D5}{ext}";
                    var newEntry = outputZip.CreateEntry(entryName);
                    using var entryStream = newEntry.Open();
                    using var sourceStream = imageEntries[i].Open();
                    await sourceStream.CopyToAsync(entryStream, CancellationToken);
                }

                chapterInfo.Add((chapter, chapterPath, startPage, pageCount));
                globalOffset += pageCount;
            }

            // Write ComicInfo.xml
            string comicInfoXml = BuildComicInfoXml(manga.Name, volumeNumber, globalOffset);
            var comicInfoEntry = outputZip.CreateEntry("ComicInfo.xml");
            using var comicInfoStream = comicInfoEntry.Open();
            var xmlBytes = Encoding.UTF8.GetBytes(comicInfoXml);
            await comicInfoStream.WriteAsync(xmlBytes, CancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error($"Error building bundle for manga {mangaId} volume {volumeNumber}: {ex.Message}", ex);
            if (File.Exists(outputPath))
            {
                try { File.Delete(outputPath); } catch { /* best effort */ }
            }
            return [];
        }

        // Write DB state
        foreach (var (chapter, _, startPage, pageCount) in chapterInfo)
        {
            _mangaContext.BundleChapterMaps.Add(new BundleChapterMap
            {
                VolumeKey = volumeMetadata.Key,
                ChapterKey = chapter.Key,
                StartPage = startPage,
                PageCount = pageCount
            });
            chapter.IsBundled = true;
            chapter.FileName = null;
        }

        volumeMetadata.ArchiveFileName = Path.GetFileName(outputPath);

        await _mangaContext.Sync(CancellationToken, GetType(), nameof(DoWorkInternal));

        // Only after successful DB sync: delete original chapter files
        foreach (var (_, originalPath, _, _) in chapterInfo)
        {
            if (File.Exists(originalPath))
            {
                try
                {
                    File.Delete(originalPath);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not delete original chapter file '{originalPath}': {ex.Message}");
                }
            }
        }

        return [];
    }

    private static bool IsImageEntry(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") ||
               lower.EndsWith(".png") || lower.EndsWith(".webp");
    }

    private static string BuildComicInfoXml(string mangaName, int vol, int pageCount)
    {
        return $"""
                <?xml version="1.0"?>
                <ComicInfo>
                  <Series>{EscapeXml(mangaName)}</Series>
                  <Volume>{vol}</Volume>
                  <PageCount>{pageCount}</PageCount>
                </ComicInfo>
                """;
    }

    private static string EscapeXml(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public override string ToString() => $"{base.ToString()} manga={mangaId} vol={volumeNumber}";
}
