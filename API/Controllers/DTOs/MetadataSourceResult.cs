namespace API.Controllers.DTOs;

/// <summary>
/// DTO for a Series's MetadataSource.
/// </summary>
public record MetadataSourceResult(
    string SourceType,
    string? ExternalId,
    string Status,
    DateTime? LastSyncedAt,
    float? MatchScore
);
