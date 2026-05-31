using System.ComponentModel.DataAnnotations;

namespace API.Controllers.DTOs;

/// <summary>
/// Entry for a manga with unresolved chapters or missing files.
/// </summary>
public record UnresolvedMangaEntry(
    [Required] string MangaId,
    [Required] string MangaName,
    [Required] int UnresolvedChapterCount,
    [Required] int MissingFileCount
);

/// <summary>
/// Result of GET /v2/Library/unresolved
/// </summary>
public record UnresolvedDashboardResult(
    [Required] List<UnresolvedMangaEntry> Series
);
