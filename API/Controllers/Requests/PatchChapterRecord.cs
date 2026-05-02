using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace API.Controllers.Requests;

/// <summary>
/// Request body for updating mutable metadata on a Chapter
/// </summary>
public record PatchChapterRecord(
    /// <summary>Relative file path of the chapter archive</summary>
    [Required]
    [Description("Relative file path of the chapter archive")]
    string FileName,

    /// <summary>Volume number, or null if not part of a volume</summary>
    [Description("Volume number, or null if not part of a volume")]
    int? VolumeNumber
);
