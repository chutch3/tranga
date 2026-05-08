namespace API.Services;

/// <summary>
/// Raw result from AniList search, before scoring.
/// </summary>
public class AniListSearchResult
{
    public int AniListId { get; init; }
    public string Title { get; init; } = null!;
    public string? Author { get; init; }
    public int? ChapterCount { get; init; }
    public int? VolumeCount { get; init; }
}

/// <summary>
/// Searches AniList for manga by title.
/// </summary>
public interface IAniListSearchService
{
    /// <summary>
    /// Searches AniList for manga matching the given title query.
    /// </summary>
    Task<List<AniListSearchResult>> SearchAsync(string title, CancellationToken cancellationToken = default);
}
