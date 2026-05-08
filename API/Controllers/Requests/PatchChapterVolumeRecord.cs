using System.ComponentModel;

namespace API.Controllers.Requests;

/// <summary>
/// Request body for PUT /Chapter/{id}/volume
/// </summary>
public record PatchChapterVolumeRecord(
    /// <summary>Volume number, or null to clear</summary>
    [Description("Volume number, or null to clear")]
    int? VolumeNumber
);
