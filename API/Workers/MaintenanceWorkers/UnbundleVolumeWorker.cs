using System.IO.Compression;
using API.Schema.SeriesContext;
using API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace API.Workers.MaintenanceWorkers;

public class UnbundleVolumeWorker(string mangaId, int volumeNumber, TrangaSettings settings, IEnumerable<BaseWorker>? dependsOn = null)
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

        var maps = await _mangaContext.BundleChapterMaps
            .Where(m => m.VolumeKey == volumeMetadata.Key)
            .OrderBy(m => m.StartPage)
            .ToListAsync(CancellationToken);

        if (maps.Count == 0)
        {
            Log.Warn($"No BundleChapterMap rows found for volume {volumeNumber}; nothing to unbundle.");
            return [];
        }

        var manga = volumeMetadata.Series;

        if (volumeMetadata.ArchiveFileName is null)
        {
            Log.Warn($"Volume {volumeNumber} has no ArchiveFileName; cannot unbundle.");
            return [];
        }

        string bundlePath = Path.Join(manga.FullDirectoryPath, volumeMetadata.ArchiveFileName);
        if (!File.Exists(bundlePath))
        {
            Log.Warn($"Bundle file not found at {bundlePath}");
            return [];
        }

        // Load chapters referenced by the map
        var chapterKeys = maps.Select(m => m.ChapterKey).ToHashSet();
        var chapters = await _mangaContext.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .Where(c => chapterKeys.Contains(c.Key))
            .ToListAsync(CancellationToken);

        var chapterByKey = chapters.ToDictionary(c => c.Key);

        // Track extracted output paths for post-DB deletion of bundle
        var extractedPaths = new List<string>();

        try
        {
            using var bundleZip = ZipFile.OpenRead(bundlePath);

            // Sort all image entries by name with NaturalSortComparer
            var allImageEntries = bundleZip.Entries
                .Where(e => IsImageEntry(e.FullName))
                .OrderBy(e => e.Name, NaturalSortComparer.Instance)
                .ToList();

            foreach (var map in maps)
            {
                if (!chapterByKey.TryGetValue(map.ChapterKey, out var chapter))
                {
                    Log.Warn($"Chapter {map.ChapterKey} not found in DB; skipping.");
                    continue;
                }

                // Determine output path using naming scheme
                string outputFileName = chapter.GetArchiveFileName(settings.ChapterNamingScheme);
                string outputPath = Path.Join(manga.FullDirectoryPath, outputFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

                var pageEntries = allImageEntries
                    .Skip(map.StartPage)
                    .Take(map.PageCount)
                    .ToList();

                using var chapterStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                using var chapterZip = new ZipArchive(chapterStream, ZipArchiveMode.Create, leaveOpen: false);

                foreach (var entry in pageEntries)
                {
                    var newEntry = chapterZip.CreateEntry(entry.Name);
                    using var destStream = newEntry.Open();
                    using var srcStream = entry.Open();
                    await srcStream.CopyToAsync(destStream, CancellationToken);
                }

                chapter.IsBundled = false;
                chapter.FileName = outputFileName;
                extractedPaths.Add(outputPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error unbundling volume {volumeNumber} for manga {mangaId}: {ex.Message}", ex);
            return [];
        }

        volumeMetadata.ArchiveFileName = null;
        _mangaContext.BundleChapterMaps.RemoveRange(maps);

        await _mangaContext.Sync(CancellationToken, GetType(), nameof(DoWorkInternal));

        // Only after successful DB sync: delete bundle file
        if (File.Exists(bundlePath))
        {
            try
            {
                File.Delete(bundlePath);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not delete bundle file '{bundlePath}': {ex.Message}");
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

    public override string ToString() => $"{base.ToString()} manga={mangaId} vol={volumeNumber}";
}
