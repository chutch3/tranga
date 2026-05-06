using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using API.MangaConnectors;
using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace API.Workers.MaintenanceWorkers;

public class ResolveMissingVolumesWorker(TrangaSettings settings, IEnumerable<MangaConnector> connectors, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    private MangaContext _mangaContext = null!;
    private readonly HttpClient _httpClient = new();
    private readonly IEnumerable<MangaConnector> _connectors = connectors;
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

        foreach (var mangaGroup in chaptersByManga)
        {
            Manga manga = mangaGroup.Key;
            List<Chapter> chapters = mangaGroup.OrderBy(c => c, new Chapter.ChapterComparer()).ToList();
            Log.Info($"Resolving volumes for {manga.Name} ({chapters.Count} chapters missing volume)...");

            bool resolvedExact = false;

            if (_settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactOnly || 
                _settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactThenGuess)
            {
                resolvedExact = await TryResolveWithMangaDex(manga, chapters);
            }

            if (!resolvedExact && _settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactThenGuess)
            {
                Log.Info($"Exact resolution failed or returned no data for {manga.Name}. Falling back to Color Heuristic guess...");
                await TryResolveWithColorHeuristic(chapters);
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
        return [];
    }

    private async Task<bool> TryResolveWithMangaDex(Manga manga, List<Chapter> chapters)
    {
        try
        {
            string? mangadexUuid = null;

            // Check if it already has a MangaDex connector
            var mdConnector = manga.MangaConnectorIds.FirstOrDefault(c => c.MangaConnectorName.Equals("MangaDex", StringComparison.OrdinalIgnoreCase));
            if (mdConnector != null)
            {
                mangadexUuid = mdConnector.IdOnConnectorSite; // Will be renamed to ObjId later, but using current DB schema prop for now if not refactored yet, assuming it's ObjId or IdOnConnectorSite based on recent refactoring. Let's use ObjId since we refactored it.
            }
            else
            {
                // Try to search MangaDex by name
                Log.Debug($"No MangaDex connector found for {manga.Name}. Searching MangaDex API...");
                var searchResponse = await _httpClient.GetAsync($"https://api.mangadex.org/manga?title={Uri.EscapeDataString(manga.Name)}&limit=1", CancellationToken);
                if (searchResponse.IsSuccessStatusCode)
                {
                    var searchJson = JObject.Parse(await searchResponse.Content.ReadAsStringAsync(CancellationToken));
                    var dataArray = searchJson["data"] as JArray;
                    if (dataArray != null && dataArray.Count > 0)
                    {
                        mangadexUuid = dataArray[0]["id"]?.ToString();
                        Log.Info($"Found MangaDex UUID {mangadexUuid} for {manga.Name}.");
                    }
                }
            }

            if (string.IsNullOrEmpty(mangadexUuid))
            {
                Log.Warn($"Could not find a MangaDex UUID for {manga.Name}.");
                return false;
            }

            // Fetch aggregate data
            var aggResponse = await _httpClient.GetAsync($"https://api.mangadex.org/manga/{mangadexUuid}/aggregate?translatedLanguage[]=en", CancellationToken);
            if (!aggResponse.IsSuccessStatusCode)
            {
                 Log.Warn($"Failed to fetch aggregate data for {manga.Name} from MangaDex.");
                 return false;
            }

            var aggJson = JObject.Parse(await aggResponse.Content.ReadAsStringAsync(CancellationToken));
            var volumesToken = aggJson["volumes"];
            
            if (volumesToken == null || volumesToken.Type == JTokenType.Array)
            {
                // MangaDex returns an empty array if no volumes/chapters exist for the language (e.g. DMCA takedown)
                Log.Warn($"MangaDex returned no English volume data for {manga.Name}.");
                return false;
            }

            var volumesObj = volumesToken as JObject;
            if (volumesObj == null) return false;

            Dictionary<string, int> chapterToVolumeMap = new();

            foreach (var volProp in volumesObj.Properties())
            {
                var volEntry = volProp.Value as JObject;
                if (volEntry == null) continue;

                string volStr = volEntry["volume"]?.ToString() ?? "";
                if (!int.TryParse(volStr, out int volNum)) continue; // Skip non-numeric or missing volumes

                var chaptersObj = volEntry["chapters"] as JObject;
                if (chaptersObj == null) continue;

                foreach (var chapProp in chaptersObj.Properties())
                {
                    var chapEntry = chapProp.Value as JObject;
                    if (chapEntry == null) continue;

                    string chapStr = chapEntry["chapter"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(chapStr))
                    {
                        chapterToVolumeMap[chapStr] = volNum;
                    }
                }
            }

            if (chapterToVolumeMap.Count == 0) return false;

            // Apply mapping
            int mappedCount = 0;
            foreach (var chapter in chapters)
            {
                if (chapterToVolumeMap.TryGetValue(chapter.ChapterNumber, out int vol))
                {
                    chapter.VolumeNumber = vol;
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

    private async Task TryResolveWithColorHeuristic(List<Chapter> chapters)
    {
        int currentVolume = 0;
        bool isFirstChapter = true;
        
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
                    .OrderBy(e => e.FullName) // Basic sort
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
                    if (!isColor)
                    {
                        Log.Info($"First chapter ({chapter.ChapterNumber}) does not have a color cover. Aborting color heuristic for this manga.");
                        break;
                    }
                }

                if (isColor)
                {
                    currentVolume++;
                    Log.Debug($"Detected color cover on chapter {chapter.ChapterNumber} (Variance: {avgDiff:F2}). Starting Volume {currentVolume}.");
                }

                if (currentVolume > 0)
                {
                    chapter.VolumeNumber = currentVolume;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error running color heuristic on chapter {chapter.ChapterNumber} ({filePath}): {ex.Message}");
                // If we fail to read the zip, just assign it to the current volume to keep the waterfall going
                if (currentVolume > 0)
                {
                    chapter.VolumeNumber = currentVolume;
                }
            }
        }
    }

    public override string ToString() => $"{base.ToString()} Strategy={_settings.VolumeResolutionStrategy}";
}
