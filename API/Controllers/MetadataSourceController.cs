using API.Controllers.DTOs;
using API.Controllers.Requests;
using API.Schema.SeriesContext;
using API.Services;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;

// ReSharper disable InconsistentNaming

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/Series")]
public class MetadataSourceController : ControllerBase
{
    private readonly SeriesContext context;
    private readonly IMangaDexSearchService mangaDexSearchService;
    private readonly IAniListSearchService aniListSearchService;
    private readonly IWorkerQueue workerQueue;

    public MetadataSourceController(
        SeriesContext context,
        IMangaDexSearchService mangaDexSearchService,
        IAniListSearchService aniListSearchService,
        IWorkerQueue workerQueue)
    {
        this.context = context;
        this.mangaDexSearchService = mangaDexSearchService;
        this.aniListSearchService = aniListSearchService;
        this.workerQueue = workerQueue;
    }

    /// <summary>
    /// Backward-compatible constructor for tests that do not inject <see cref="IAniListSearchService"/>.
    /// </summary>
    public MetadataSourceController(
        SeriesContext context,
        IMangaDexSearchService mangaDexSearchService,
        IWorkerQueue workerQueue)
        : this(context, mangaDexSearchService, new NullAniListSearchService(), workerQueue)
    {
    }

    /// <summary>
    /// No-op AniList service used when none is registered (test compat shim).
    /// </summary>
    private sealed class NullAniListSearchService : IAniListSearchService
    {
        public Task<List<AniListSearchResult>> SearchAsync(string title, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<AniListSearchResult>());
    }


    /// <summary>
    /// Returns the <see cref="MetadataSource"/> for a given <see cref="Schema.SeriesContext.Series"/>.
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <response code="200">MetadataSource data</response>
    /// <response code="404">Series not found</response>
    [HttpGet("{MangaId}/metadataSource")]
    [ProducesResponseType<MetadataSourceResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<MetadataSourceResult>, NotFound<string>>> GetMetadataSource(string MangaId)
    {
        if (await context.Series
                .Include(m => m.MetadataSource)
                .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));

        MetadataSource? source = manga.MetadataSource;
        if (source is null)
            return TypedResults.NotFound("MetadataSource not initialized");

        var result = new MetadataSourceResult(
            source.SourceType.ToString(),
            source.ExternalId,
            source.Status.ToString(),
            source.LastSyncedAt,
            source.MatchScore);

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Sets the <see cref="MetadataSource"/> for a <see cref="Schema.SeriesContext.Series"/>, marking it as Confirmed.
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <param name="request">Source type and external ID</param>
    /// <response code="204">Updated successfully</response>
    /// <response code="400">ExternalId is null or empty</response>
    /// <response code="404">Series not found</response>
    [HttpPut("{MangaId}/metadataSource")]
    [ProducesResponseType(Status204NoContent)]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<NoContent, BadRequest<string>, NotFound<string>, InternalServerError<string>>> SetMetadataSource(
        string MangaId, [FromBody] PatchMetadataSourceRecord request)
    {
        if (string.IsNullOrWhiteSpace(request.ExternalId))
            return TypedResults.BadRequest("ExternalId must not be null or empty.");

        if (await context.Series
                .Include(m => m.MetadataSource)
                .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));

        if (!Enum.TryParse<MetadataSourceType>(request.SourceType, ignoreCase: true, out var sourceType))
            sourceType = MetadataSourceType.MangaDex;

        if (manga.MetadataSource is null)
            manga.MetadataSource = new MetadataSource(manga.Key, sourceType, MetadataSourceStatus.Confirmed);
        else
        {
            manga.MetadataSource.SourceType = sourceType;
            manga.MetadataSource.ExternalId = request.ExternalId;
            manga.MetadataSource.Status = MetadataSourceStatus.Confirmed;
        }

        if (await context.Sync(HttpContext.RequestAborted, GetType(), nameof(SetMetadataSource)) is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Searches for candidates matching a Series's title, scored by similarity.
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <param name="q">Title to search for</param>
    /// <param name="source">Metadata source to search: "mangadex" (default) or "anilist"</param>
    /// <response code="200">Top 10 scored candidates</response>
    /// <response code="404">Series not found</response>
    [HttpGet("{MangaId}/metadataSource/candidates")]
    [ProducesResponseType<List<MetadataSourceCandidate>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<List<MetadataSourceCandidate>>, NotFound<string>>> GetMetadataSourceCandidates(
        string MangaId, [FromQuery] string q, [FromQuery] string source = "mangadex")
    {
        if (await context.Series
                .Include(m => m.Authors)
                .Include(m => m.Chapters)
                .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));

        int ourChapterCount = manga.Chapters.Count;
        string? ourAuthor = manga.Authors.FirstOrDefault()?.AuthorName;
        bool hasAuthor = !string.IsNullOrEmpty(ourAuthor);

        List<MetadataSourceCandidate> candidates;

        if (string.Equals(source, "anilist", StringComparison.OrdinalIgnoreCase))
        {
            var aniListResults = await aniListSearchService.SearchAsync(q, HttpContext.RequestAborted);
            candidates = aniListResults.Select(r =>
            {
                float score = ScoreAniListCandidate(q, r, ourChapterCount, ourAuthor, hasAuthor);
                var reasons = BuildAniListMatchReasons(q, r, ourChapterCount, ourAuthor, hasAuthor);
                string externalId = r.AniListId.ToString();
                return new MetadataSourceCandidate(externalId, r.Title, r.Author, r.ChapterCount ?? 0, score, reasons, externalId);
            })
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();
        }
        else
        {
            var searchResults = await mangaDexSearchService.SearchAsync(q, HttpContext.RequestAborted);
            candidates = searchResults.Select(r =>
            {
                float score = ScoreCandidate(q, r, ourChapterCount, ourAuthor, hasAuthor);
                var reasons = BuildMatchReasons(q, r, ourChapterCount, ourAuthor, hasAuthor);
                return new MetadataSourceCandidate(r.MangaDexId, r.Title, r.Author, r.ChapterCount, score, reasons, r.MangaDexId);
            })
            .OrderByDescending(c => c.Score)
            .Take(10)
            .ToList();
        }

        return TypedResults.Ok(candidates);
    }

    /// <summary>
    /// Queues a background worker to refresh chapter volumes for a Series from its confirmed MangaDex ExternalId.
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <response code="202">Job queued. Returns jobId.</response>
    /// <response code="400">MetadataSource is Unlinked (no ExternalId set)</response>
    /// <response code="404">Series not found</response>
    [HttpPost("{MangaId}/metadataSource/refresh")]
    [ProducesResponseType<object>(Status202Accepted, "application/json")]
    [ProducesResponseType<string>(Status400BadRequest, "text/plain")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Accepted<object>, BadRequest<string>, NotFound<string>>> RefreshMetadataSource(string MangaId)
    {
        if (await context.Series
                .Include(m => m.MetadataSource)
                .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));

        if (manga.MetadataSource is null || manga.MetadataSource.Status == MetadataSourceStatus.Unlinked
            || string.IsNullOrEmpty(manga.MetadataSource.ExternalId))
            return TypedResults.BadRequest("MetadataSource is Unlinked. Set an ExternalId first via PUT metadataSource.");

        var worker = new RefreshMetadataSourceWorker(MangaId);
        workerQueue.AddWorker(worker);

        return TypedResults.Accepted<object>((string?)null, new { jobId = worker.Key });
    }

    // --- Scoring helpers ---

    private static float ScoreCandidate(string query, MangaDexSearchResult candidate, int ourChapterCount, string? ourAuthor, bool hasAuthor)
    {
        float titleSim = JaroWinkler(Normalize(query), Normalize(candidate.Title));
        float countProx = ChapterCountProximity(ourChapterCount, candidate.ChapterCount);
        float authorMatch = hasAuthor && !string.IsNullOrEmpty(candidate.Author)
            ? (Normalize(candidate.Author!).Contains(Normalize(ourAuthor!)) || Normalize(ourAuthor!).Contains(Normalize(candidate.Author!)) ? 1f : 0f)
            : float.NaN;

        if (float.IsNaN(authorMatch))
        {
            // No author info: redistribute weight
            return titleSim * 0.65f + countProx * 0.35f;
        }

        return titleSim * 0.6f + countProx * 0.3f + authorMatch * 0.1f;
    }

    private static List<string> BuildMatchReasons(string query, MangaDexSearchResult candidate, int ourChapterCount, string? ourAuthor, bool hasAuthor)
    {
        var reasons = new List<string>();
        float titleSim = JaroWinkler(Normalize(query), Normalize(candidate.Title));
        if (titleSim > 0.9f) reasons.Add("Title is very similar");
        else if (titleSim > 0.7f) reasons.Add("Title is somewhat similar");

        float countProx = ChapterCountProximity(ourChapterCount, candidate.ChapterCount);
        if (countProx > 0.9f) reasons.Add("Chapter count matches closely");
        else if (countProx > 0.7f) reasons.Add("Chapter count is similar");

        if (hasAuthor && !string.IsNullOrEmpty(candidate.Author))
        {
            bool match = Normalize(candidate.Author!).Contains(Normalize(ourAuthor!))
                || Normalize(ourAuthor!).Contains(Normalize(candidate.Author!));
            if (match) reasons.Add("Author matches");
        }

        return reasons;
    }

    private static float ScoreAniListCandidate(string query, AniListSearchResult candidate, int ourChapterCount, string? ourAuthor, bool hasAuthor)
    {
        float titleSim = JaroWinkler(Normalize(query), Normalize(candidate.Title));
        float countProx = ChapterCountProximity(ourChapterCount, candidate.ChapterCount ?? 0);
        float authorMatch = hasAuthor && !string.IsNullOrEmpty(candidate.Author)
            ? (Normalize(candidate.Author!).Contains(Normalize(ourAuthor!)) || Normalize(ourAuthor!).Contains(Normalize(candidate.Author!)) ? 1f : 0f)
            : float.NaN;

        if (float.IsNaN(authorMatch))
            return titleSim * 0.65f + countProx * 0.35f;

        return titleSim * 0.6f + countProx * 0.3f + authorMatch * 0.1f;
    }

    private static List<string> BuildAniListMatchReasons(string query, AniListSearchResult candidate, int ourChapterCount, string? ourAuthor, bool hasAuthor)
    {
        var reasons = new List<string>();
        float titleSim = JaroWinkler(Normalize(query), Normalize(candidate.Title));
        if (titleSim > 0.9f) reasons.Add("Title is very similar");
        else if (titleSim > 0.7f) reasons.Add("Title is somewhat similar");

        float countProx = ChapterCountProximity(ourChapterCount, candidate.ChapterCount ?? 0);
        if (countProx > 0.9f) reasons.Add("Chapter count matches closely");
        else if (countProx > 0.7f) reasons.Add("Chapter count is similar");

        if (hasAuthor && !string.IsNullOrEmpty(candidate.Author))
        {
            bool match = Normalize(candidate.Author!).Contains(Normalize(ourAuthor!))
                || Normalize(ourAuthor!).Contains(Normalize(candidate.Author!));
            if (match) reasons.Add("Author matches");
        }

        return reasons;
    }

    private static float ChapterCountProximity(int ours, int theirs)
    {
        if (ours == 0 && theirs == 0) return 1f;
        int max = Math.Max(ours, theirs);
        if (max == 0) return 1f;
        return 1f - (Math.Abs(ours - theirs) / (float)max);
    }

    private static string Normalize(string s)
        => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[^\w\s]", "")
            .Trim();

    /// <summary>
    /// Jaro-Winkler similarity in [0,1].
    /// </summary>
    private static float JaroWinkler(string s1, string s2)
    {
        if (s1 == s2) return 1f;
        if (s1.Length == 0 || s2.Length == 0) return 0f;

        int matchWindow = Math.Max(s1.Length, s2.Length) / 2 - 1;
        if (matchWindow < 0) matchWindow = 0;

        bool[] s1Matched = new bool[s1.Length];
        bool[] s2Matched = new bool[s2.Length];

        int matches = 0;
        int transpositions = 0;

        for (int i = 0; i < s1.Length; i++)
        {
            int start = Math.Max(0, i - matchWindow);
            int end = Math.Min(i + matchWindow + 1, s2.Length);

            for (int j = start; j < end; j++)
            {
                if (s2Matched[j] || s1[i] != s2[j]) continue;
                s1Matched[i] = true;
                s2Matched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0) return 0f;

        int k = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            if (!s1Matched[i]) continue;
            while (!s2Matched[k]) k++;
            if (s1[i] != s2[k]) transpositions++;
            k++;
        }

        float jaro = ((float)matches / s1.Length
                      + (float)matches / s2.Length
                      + ((float)(matches - transpositions / 2) / matches)) / 3f;

        // Winkler prefix bonus
        int prefixLen = 0;
        for (int i = 0; i < Math.Min(4, Math.Min(s1.Length, s2.Length)); i++)
        {
            if (s1[i] == s2[i]) prefixLen++;
            else break;
        }

        return jaro + prefixLen * 0.1f * (1f - jaro);
    }
}
