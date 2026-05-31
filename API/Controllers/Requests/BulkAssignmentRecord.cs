using System.ComponentModel.DataAnnotations;

namespace API.Controllers.Requests;

/// <summary>
/// Request body for POST /Series/{MangaId}/volumes/assignments
/// </summary>
public record BulkAssignmentRecord(
    /// <summary>Map of ChapterNumber (string) to VolumeNumber (int)</summary>
    [Required] Dictionary<string, int> Assignments
);
