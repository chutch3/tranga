using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace API.Controllers.Requests;

/// <summary>
/// Request body for PUT /Series/{id}/metadataSource
/// </summary>
public record PatchMetadataSourceRecord(
    /// <summary>Source type (e.g. MangaDex, AniList, Manual)</summary>
    [Required]
    [Description("Source type identifier")]
    string SourceType,

    /// <summary>External ID on the source system (must be non-null and non-empty)</summary>
    [Required]
    [Description("External ID on the source system")]
    string ExternalId
);
