using System.ComponentModel.DataAnnotations;

namespace API.Controllers.DTOs;

/// <summary>
/// Result of POST /Series/{MangaId}/volumes/assignments
/// </summary>
public record BulkAssignmentResult(
    [Required] int Applied,
    [Required] List<string> NotFound
);
