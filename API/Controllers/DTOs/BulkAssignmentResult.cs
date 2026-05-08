using System.ComponentModel.DataAnnotations;

namespace API.Controllers.DTOs;

/// <summary>
/// Result of POST /Manga/{MangaId}/volumes/assignments
/// </summary>
public record BulkAssignmentResult(
    [Required] int Applied,
    [Required] List<string> NotFound
);
