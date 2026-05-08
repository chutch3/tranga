namespace API.Services;

/// <summary>
/// Searches MangaDex for manga by title.
/// </summary>
public interface IMangaDexSearchService
{
    /// <summary>
    /// Searches MangaDex for manga matching the given title query.
    /// </summary>
    Task<List<MangaDexSearchResult>> SearchAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches chapter-to-volume mapping for a given MangaDex manga ID.
    /// </summary>
    Task<Dictionary<string, int>> GetChapterToVolumeMapAsync(string mangaDexId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raw result from MangaDex search, before scoring.
/// </summary>
public class MangaDexSearchResult
{
    public string MangaDexId { get; init; } = null!;
    public string Title { get; init; } = null!;
    public string? Author { get; init; }
    public int ChapterCount { get; init; }
}
