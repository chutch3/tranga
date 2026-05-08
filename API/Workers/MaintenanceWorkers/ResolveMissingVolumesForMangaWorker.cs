using System.Collections.Concurrent;
using System.IO.Compression;
using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace API.Workers.MaintenanceWorkers;

public class ResolveMissingVolumesForMangaWorker(
    ConcurrentQueue<string> queue,
    TrangaSettings settings,
    IMangaDexVolumeResolver mangaDexVolumeResolver,
    IEnumerable<BaseWorker>? dependsOn = null)
    : PoolWorker<string>(queue, dependsOn)
{
    private MangaContext _mangaContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<MangaContext>(serviceScope);
    }

    protected override async Task<IEnumerable<BaseWorker>> ProcessItem(string mangaId)
    {
        var manga = await _mangaContext.Mangas
            .Include(m => m.MangaConnectorIds)
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Key == mangaId, CancellationToken);

        if (manga is null)
        {
            Log.Warn($"Manga {mangaId} not found in database; skipping.");
            return [];
        }

        var chapters = await _mangaContext.Chapters
            .Where(c => c.ParentMangaId == mangaId &&
                        c.Downloaded &&
                        c.FileName != null &&
                        c.VolumeNumber == null)
            .ToListAsync(CancellationToken);

        if (chapters.Count == 0)
            return [];

        chapters = chapters.OrderBy(c => c, new Chapter.ChapterComparer()).ToList();
        Log.Info($"Resolving volumes for {manga.Name} ({chapters.Count} chapters missing volume)...");

        bool resolvedExact = false;

        if (settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactOnly ||
            settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactThenGuess)
        {
            resolvedExact = await TryResolveWithMangaDex(manga, chapters);
        }

        if (!resolvedExact && settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactThenGuess)
        {
            Log.Info($"Exact resolution failed for {manga.Name}. Falling back to color heuristic...");
            int startVolume = (await _mangaContext.Chapters
                .Where(c => c.ParentMangaId == mangaId && c.VolumeNumber != null)
                .Select(c => c.VolumeNumber)
                .DefaultIfEmpty()
                .MaxAsync(CancellationToken)) ?? 0;
            await TryResolveWithColorHeuristic(chapters, startVolume);
        }

        int updatedCount = chapters.Count(c => c.VolumeNumber != null);
        if (updatedCount > 0)
        {
            if (await _mangaContext.Sync(CancellationToken, GetType(), nameof(ProcessItem)) is { success: false } err)
                Log.Error($"Failed to save volume updates for {manga.Name}: {err.exceptionMessage}");
            else
                Log.Info($"Saved {updatedCount} volume updates for {manga.Name}.");
        }

        return [];
    }

    private async Task<bool> TryResolveWithMangaDex(Manga manga, List<Chapter> chapters)
    {
        try
        {
            var map = await mangaDexVolumeResolver.GetChapterToVolumeMapAsync(manga, CancellationToken);
            if (map.Count == 0) return false;

            int mapped = 0;
            foreach (var chapter in chapters)
            {
                if (map.TryGetValue(chapter.ChapterNumber, out int vol))
                {
                    AssignVolume(chapter, vol);
                    mapped++;
                }
            }

            Log.Info($"Mapped {mapped}/{chapters.Count} chapters for {manga.Name} via MangaDex.");
            return mapped > 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Error resolving volumes via MangaDex for {manga.Name}: {ex.Message}");
            return false;
        }
    }

    private async Task TryResolveWithColorHeuristic(List<Chapter> chapters, int startVolume)
    {
        int currentVolume = startVolume;
        bool isFirstChapter = true;
        bool prevWasColor = false;

        foreach (var chapter in chapters)
        {
            string? filePath = chapter.FullArchiveFilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Log.Warn($"File not found for chapter {chapter.ChapterNumber}, skipping.");
                continue;
            }

            try
            {
                using var archive = ZipFile.OpenRead(filePath);
                var images = archive.Entries
                    .Where(e => e.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => Path.GetFileNameWithoutExtension(e.FullName).Equals("cover", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(e => e.FullName)
                    .ToList();

                if (images.Count == 0) continue;

                using var stream = images[0].Open();
                using var image = await Image.LoadAsync<Rgb24>(stream, CancellationToken);
                image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(200, 200), Mode = ResizeMode.Max }));

                long colorDiffSum = 0;
                int pixelCount = image.Width * image.Height;
                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        Span<Rgb24> row = accessor.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            ref Rgb24 pixel = ref row[x];
                            colorDiffSum += Math.Abs(pixel.R - pixel.G) +
                                            Math.Abs(pixel.G - pixel.B) +
                                            Math.Abs(pixel.B - pixel.R);
                        }
                    }
                });

                double avgDiff = (double)colorDiffSum / pixelCount;
                bool isColor = avgDiff > 10;

                if (isFirstChapter)
                {
                    isFirstChapter = false;
                    if (!isColor && currentVolume == 0)
                    {
                        Log.Info($"First chapter ({chapter.ChapterNumber}) has no color cover. Aborting heuristic.");
                        break;
                    }
                }
                else if (isColor && prevWasColor)
                {
                    Log.Info($"Consecutive color covers at chapter {chapter.ChapterNumber}. Aborting heuristic.");
                    break;
                }

                prevWasColor = isColor;

                if (isColor)
                {
                    currentVolume++;
                    Log.Debug($"Color cover on chapter {chapter.ChapterNumber} (avgDiff={avgDiff:F2}). Starting volume {currentVolume}.");
                }

                if (currentVolume > 0)
                    AssignVolume(chapter, currentVolume);
            }
            catch (Exception ex)
            {
                Log.Error($"Error in color heuristic for chapter {chapter.ChapterNumber}: {ex.Message}");
                if (currentVolume > 0)
                    AssignVolume(chapter, currentVolume);
            }
        }
    }

    private void AssignVolume(Chapter chapter, int volume)
    {
        chapter.VolumeNumber = volume;
    }
}
