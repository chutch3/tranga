using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.RegularExpressions;
using API.Schema.SeriesContext;
using API.Services;
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
    IMangaDexSearchService mangaDexSearchService,
    IEnumerable<BaseWorker>? dependsOn = null)
    : PoolWorker<string>(queue, dependsOn)
{
    private SeriesContext _mangaContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<SeriesContext>(serviceScope);
    }

    protected override async Task<IEnumerable<BaseWorker>> ProcessItem(string mangaId)
    {
        var manga = await _mangaContext.Series
            .Include(m => m.SourceIds)
            .Include(m => m.Library)
            .Include(m => m.MetadataSource)
            .FirstOrDefaultAsync(m => m.Key == mangaId, CancellationToken);

        if (manga is null)
        {
            Log.Warn($"Series {mangaId} not found in database; skipping.");
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

        // Step 4: If MetadataSource.Status is Unlinked, attempt auto-match first
        if (manga.MetadataSource?.Status == MetadataSourceStatus.Unlinked)
        {
            await TryAutoMatch(manga, chapters);
        }

        bool resolvedExact = false;

        // Step 5: If Confirmed or AutoMatched, use MangaDex
        if (manga.MetadataSource?.Status is MetadataSourceStatus.Confirmed or MetadataSourceStatus.AutoMatched)
        {
            if (settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactOnly ||
                settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactThenGuess)
            {
                resolvedExact = await TryResolveWithMangaDex(manga, chapters);
            }
        }
        else if (settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactOnly ||
                 settings.VolumeResolutionStrategy == VolumeResolutionStrategy.ExactThenGuess)
        {
            // Status is Ambiguous/NoMatch or no MetadataSource — still attempt exact if strategy allows
            // (handles the case where MetadataSource was already confirmed before this run)
            resolvedExact = await TryResolveWithMangaDex(manga, chapters);
        }

        // Step 6: Heuristic fallback
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

    /// <summary>
    /// Attempts to auto-match the manga against MangaDex using weighted scoring.
    /// Sets MetadataSource.Status to AutoMatched, Ambiguous, or NoMatch and saves.
    /// If AutoMatched, immediately resolves volumes via the new ExternalId.
    /// If the volume fetch returns 0 mappings, rolls back to Unlinked.
    /// </summary>
    private async Task TryAutoMatch(Series manga, List<Chapter> allChapters)
    {
        string normalizedTitle = NormalizeTitle(manga.Name);

        List<MangaDexSearchResult> candidates;
        try
        {
            candidates = await mangaDexSearchService.SearchAsync(normalizedTitle, CancellationToken);
        }
        catch (Exception ex)
        {
            Log.Error($"Auto-match search failed for {manga.Name}: {ex.Message}");
            return;
        }

        if (candidates.Count == 0)
        {
            Log.Info($"Auto-match: no candidates found for {manga.Name}. Setting NoMatch.");
            manga.MetadataSource!.Status = MetadataSourceStatus.NoMatch;
            await _mangaContext.Sync(CancellationToken, GetType(), nameof(TryAutoMatch));
            return;
        }

        int ourChapterCount = allChapters.Count;

        // Score each candidate
        var scored = candidates
            .Select(c => (candidate: c, score: ScoreCandidate(normalizedTitle, ourChapterCount, c)))
            .OrderByDescending(x => x.score)
            .ToList();

        float topScore = scored[0].score;
        float secondScore = scored.Count > 1 ? scored[1].score : 0f;
        string topId = scored[0].candidate.MangaDexId;
        string decision;

        MetadataSourceStatus newStatus;

        if (topScore >= 0.90f && (scored.Count == 1 || topScore - secondScore >= 0.10f))
        {
            newStatus = MetadataSourceStatus.AutoMatched;
            decision = "AutoMatched";
        }
        else if (topScore >= 0.50f && scored.Count > 1 && topScore - secondScore < 0.10f)
        {
            newStatus = MetadataSourceStatus.Ambiguous;
            decision = "Ambiguous";
        }
        else if (topScore < 0.50f)
        {
            newStatus = MetadataSourceStatus.NoMatch;
            decision = "NoMatch";
        }
        else
        {
            // Single candidate above 0.90: AutoMatched (already handled above)
            // Single candidate between 0.50 and 0.90: NoMatch (not confident enough)
            newStatus = MetadataSourceStatus.NoMatch;
            decision = "NoMatch";
        }

        Log.Info($"Auto-match decision: mangaId={manga.Key} topCandidateId={topId} topScore={topScore:F4} secondScore={secondScore:F4} decision={decision}");

        if (newStatus == MetadataSourceStatus.AutoMatched)
        {
            // Attempt to fetch volumes with the matched ExternalId
            Dictionary<string, int> map;
            try
            {
                map = await mangaDexSearchService.GetChapterToVolumeMapAsync(topId, CancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error($"Auto-match volume fetch failed for {manga.Name} (externalId={topId}): {ex.Message}");
                // Roll back: leave status as Unlinked
                return;
            }

            if (map.Count == 0)
            {
                Log.Info($"Auto-match succeeded for {manga.Name} but volume fetch returned 0 mappings. Rolling back to Unlinked.");
                // Leave MetadataSource as Unlinked (no mutation needed)
                await _mangaContext.Sync(CancellationToken, GetType(), nameof(TryAutoMatch));
                return;
            }

            // Commit the auto-match
            manga.MetadataSource!.ExternalId = topId;
            manga.MetadataSource.Status = MetadataSourceStatus.AutoMatched;
            manga.MetadataSource.MatchScore = topScore;

            // Apply volume assignments with Exact confidence
            int mapped = 0;
            foreach (var chapter in allChapters)
            {
                if (map.TryGetValue(chapter.ChapterNumber, out int vol))
                {
                    AssignVolume(chapter, vol, MetadataConfidence.Exact);
                    mapped++;
                }
            }

            Log.Info($"Auto-match applied {mapped}/{allChapters.Count} volume assignments for {manga.Name}.");
        }
        else
        {
            manga.MetadataSource!.Status = newStatus;
        }

        await _mangaContext.Sync(CancellationToken, GetType(), nameof(TryAutoMatch));
    }

    private async Task<bool> TryResolveWithMangaDex(Series manga, List<Chapter> chapters)
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
                    AssignVolume(chapter, vol, MetadataConfidence.Exact);
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
                    AssignVolume(chapter, currentVolume, MetadataConfidence.Heuristic);
            }
            catch (Exception ex)
            {
                Log.Error($"Error in color heuristic for chapter {chapter.ChapterNumber}: {ex.Message}");
                if (currentVolume > 0)
                    AssignVolume(chapter, currentVolume, MetadataConfidence.Heuristic);
            }
        }
    }

    private static void AssignVolume(Chapter chapter, int volume, MetadataConfidence confidence)
    {
        chapter.VolumeNumber = volume;
        chapter.MetadataConfidence = confidence;
    }

    // ─── Jaro-Winkler implementation ─────────────────────────────────────────

    private static float JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1.0f;
        if (s1.Length == 0 || s2.Length == 0) return 0.0f;

        int matchWindow = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchWindow < 0) matchWindow = 0;

        bool[] s1Matches = new bool[s1.Length];
        bool[] s2Matches = new bool[s2.Length];

        int matches = 0;
        int transpositions = 0;

        for (int i = 0; i < s1.Length; i++)
        {
            int start = Math.Max(0, i - matchWindow);
            int end = Math.Min(i + matchWindow + 1, s2.Length);

            for (int j = start; j < end; j++)
            {
                if (s2Matches[j] || s1[i] != s2[j]) continue;
                s1Matches[i] = true;
                s2Matches[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0.0f;

        int k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matches[i]) continue;
            while (!s2Matches[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        float jaro = ((float)matches / s1.Length +
                      (float)matches / s2.Length +
                      (float)(matches - transpositions / 2) / matches) / 3.0f;

        // Winkler prefix bonus (up to 4 chars)
        int prefix = 0;
        for (int i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] == s2[i]) prefix++;
            else break;
        }

        return jaro + prefix * 0.1f * (1.0f - jaro);
    }

    private static float ScoreCandidate(string normalizedMangaTitle, int ourChapterCount, MangaDexSearchResult candidate)
    {
        string candidateTitle = NormalizeTitle(candidate.Title);
        float titleSim = JaroWinkler(normalizedMangaTitle, candidateTitle);

        float countProximity = 0f;
        if (ourChapterCount > 0 && candidate.ChapterCount > 0)
        {
            int maxCount = Math.Max(ourChapterCount, candidate.ChapterCount);
            countProximity = 1.0f - (float)Math.Abs(ourChapterCount - candidate.ChapterCount) / maxCount;
        }

        // Author data is unavailable in MangaDexSearchResult when Author is null
        if (candidate.Author is null)
        {
            // Redistribute: title 0.65 / count 0.35
            return titleSim * 0.65f + countProximity * 0.35f;
        }
        else
        {
            // Author match is binary 0/1 — we don't have our manga's author here,
            // so treat it as unavailable (Author field from search is the candidate's author,
            // not something we can compare against without manga author data in scope).
            // Use the no-author weights.
            return titleSim * 0.65f + countProximity * 0.35f;
        }
    }

    private static readonly Regex PunctuationRegex = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static string NormalizeTitle(string title)
    {
        string lower = title.ToLowerInvariant();
        string noPunct = PunctuationRegex.Replace(lower, " ");
        return WhitespaceRegex.Replace(noPunct, " ").Trim();
    }
}
