namespace API.Controllers.DTOs;

/// <summary>
/// A scored metadata candidate for linking to a local Series.
/// </summary>
public record MetadataSourceCandidate(
    string MangaDexId,
    string Title,
    string? Author,
    int ChapterCount,
    float Score,
    List<string> MatchReasons,
    string ExternalId
);
