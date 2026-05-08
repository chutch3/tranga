namespace API.Controllers.DTOs;

/// <summary>
/// A scored MangaDex candidate for linking to a local Manga.
/// </summary>
public record MetadataSourceCandidate(
    string MangaDexId,
    string Title,
    string? Author,
    int ChapterCount,
    float Score,
    List<string> MatchReasons
);
