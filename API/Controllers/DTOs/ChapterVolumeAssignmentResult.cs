using System.ComponentModel.DataAnnotations;

namespace API.Controllers.DTOs;

/// <summary>
/// Result of a PUT /Chapter/{id}/volume operation.
/// </summary>
public record ChapterVolumeAssignmentResult(
    [Required] string ChapterId,
    [Required] string ChapterNumber,
    int? VolumeNumber,
    string? MetadataConfidence
);
