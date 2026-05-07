using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace API.Workers.MaintenanceWorkers;

public class ResolveMissingVolumesWorker(TrangaSettings settings, IMangaDexVolumeResolver mangaDexVolumeResolver, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    private MangaContext _mangaContext = null!;
    private readonly IMangaDexVolumeResolver _mangaDexVolumeResolver = mangaDexVolumeResolver!;
    private readonly TrangaSettings _settings = settings;

    public DateTime LastExecution { get; set; } = DateTime.MinValue;
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<MangaContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        if (_settings.VolumeResolutionStrategy == VolumeResolutionStrategy.Disabled)
        {
            Log.Info("Volume resolution is disabled in settings. Skipping.");
            return [];
        }

        Log.Info($"Starting Volume Resolution Worker (Strategy: {_settings.VolumeResolutionStrategy})...");

        // Find all downloaded chapters where volume is null
        var chaptersMissingVolumes = await _mangaContext.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.MangaConnectorIds)
            .Include(c => c.ParentManga.Library)
            .Where(c => c.Downloaded && c.FileName != null && c.VolumeNumber == null)
            .ToListAsync(CancellationToken);

        if (chaptersMissingVolumes.Count == 0)
        {
            Log.Info("No downloaded chapters are missing volume numbers.");
            LastExecution = DateTime.UtcNow;
            return [];
        }

        var chaptersByManga = chaptersMissingVolumes.GroupBy(c => c.ParentManga);
        int updatedCount = 0;
        List<BaseWorker> newJobs = new();

        foreach (var mangaGroup in chaptersByManga)
        {
            Manga manga = mangaGroup.Key;
            List<Chapter> chapters = mangaGroup.OrderBy(c => c, new Chapter.ChapterComparer()).ToList();
            Log.Info($"Resolving volumes for {manga.Name} ({chapters.Count} chapters missing volume)...");

            bool resolvedExact = false;

            if (_settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactOnly || 
                _settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactThenGuess)
            {
                resolvedExact = await TryResolveWithMangaDex(manga, chapters, newJobs);
            }

            if (!resolvedExact && _settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactThenGuess)
            {
                Log.Info($"Exact resolution failed or returned no data for {manga.Name}. Falling back to Color Heuristic guess...");
                int startVolume = (await _mangaContext.Chapters
                    .Where(c => c.ParentMangaId == manga.Key && c.VolumeNumber != null)
                    .Select(c => c.VolumeNumber)
                    .DefaultIfEmpty()
                    .MaxAsync(CancellationToken)) ?? 0;
                await TryResolveWithColorHeuristic(chapters, startVolume, newJobs);
            }
            
            // Count how many we actually updated in memory for reporting
            updatedCount += chapters.Count(c => c.VolumeNumber != null);
        }

        if (updatedCount > 0)
        {
            Log.Info($"Saving {updatedCount} updated volume numbers to database...");
            if (await _mangaContext.Sync(CancellationToken, GetType(), "Resolve Missing Volumes") is { success: false } err)
            {
                Log.Error($"Failed to save volume updates: {err.exceptionMessage}");
            }
            else
            {
                Log.Info("Successfully saved volume updates. Tranga will automatically move files if ChapterNamingScheme includes volume.");
            }
        }

        LastExecution = DateTime.UtcNow;
        return newJobs.ToArray();
    }

    private async Task<bool> TryResolveWithMangaDex(Manga manga, List<Chapter> chapters, List<BaseWorker> newJobs)
    {
        try
        {
            var chapterToVolumeMap = await _mangaDexVolumeResolver.GetChapterToVolumeMapAsync(manga, CancellationToken);
            if (chapterToVolumeMap.Count == 0) return false;

            int mappedCount = 0;
            foreach (var chapter in chapters)
            {
                if (chapterToVolumeMap.TryGetValue(chapter.ChapterNumber, out int vol))
                {
                    AssignVolumeAndQueueMove(chapter, vol, newJobs);
                    mappedCount++;
                }
            }

            Log.Info($"Successfully mapped {mappedCount}/{chapters.Count} chapters for {manga.Name} using MangaDex Exact match.");
            return mappedCount > 0;
        }
        catch (Exception ex)
        {
            Log.Error($"Error resolving volumes via MangaDex for {manga.Name}: {ex.Message}");
            return false;
        }
    }

    private async Task TryResolveWithColorHeuristic(List<Chapter> chapters, int startVolume, List<BaseWorker> newJobs)
    {
        int currentVolume = startVolume;
        bool isFirstChapter = true;
        bool prevWasColor = false;
        
        foreach (var chapter in chapters)
        {
            string? filePath = chapter.FullArchiveFilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Log.Warn($"File not found for chapter {chapter.ChapterNumber}, cannot run heuristic.");
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

                var firstImageEntry = images[0];
                using var stream = firstImageEntry.Open();
                
                // Read image
                using var image = await Image.LoadAsync<Rgb24>(stream, CancellationToken);
                
                // Resize for faster analysis
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(200, 200),
                    Mode = ResizeMode.Max
                }));

                // Calculate sum of absolute differences between R,G,B channels
                long colorDiffSum = 0;
                int pixelCount = image.Width * image.Height;

                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        Span<Rgb24> pixelRow = accessor.GetRowSpan(y);
                        for (int x = 0; x < pixelRow.Length; x++)
                        {
                            ref Rgb24 pixel = ref pixelRow[x];
                            colorDiffSum += Math.Abs(pixel.R - pixel.G) + 
                                            Math.Abs(pixel.G - pixel.B) + 
                                            Math.Abs(pixel.B - pixel.R);
                        }
                    }
                });

                double avgDiff = (double)colorDiffSum / pixelCount;
                
                bool isColor = avgDiff > 10; // Threshold

                if (isFirstChapter)
                {
                    isFirstChapter = false;
                    if (!isColor && currentVolume == 0)
                    {
                        Log.Info($"First chapter ({chapter.ChapterNumber}) does not have a color cover. Aborting color heuristic for this manga.");
                        break;
                    }
                }
                else if (isColor && prevWasColor)
                {
                    Log.Info($"Consecutive color covers detected at chapter {chapter.ChapterNumber}. Cannot determine volume structure (full-color manga, missing chapters, or single-chapter volumes), aborting heuristic.");
                    break;
                }

                prevWasColor = isColor;

                if (isColor)
                {
                    currentVolume++;
                    Log.Debug($"Detected color cover on chapter {chapter.ChapterNumber} (Variance: {avgDiff:F2}). Starting Volume {currentVolume}.");
                }

                if (currentVolume > 0)
                    AssignVolumeAndQueueMove(chapter, currentVolume, newJobs);
            }
            catch (Exception ex)
            {
                Log.Error($"Error running color heuristic on chapter {chapter.ChapterNumber} ({filePath}): {ex.Message}");
                if (currentVolume > 0)
                    AssignVolumeAndQueueMove(chapter, currentVolume, newJobs);
            }
        }
    }

    private void AssignVolumeAndQueueMove(Chapter chapter, int volume, List<BaseWorker> newJobs)
    {
        chapter.VolumeNumber = volume;
        string newFileName = chapter.GetArchiveFileName(_settings.ChapterNamingScheme);
        if (chapter.FileName != newFileName)
            newJobs.Add(new RenameChapterFileWorker(chapter.Key, newFileName, _settings));
    }

    public override string ToString() => $"{base.ToString()} Strategy={_settings.VolumeResolutionStrategy}";
}
