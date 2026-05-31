using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using API.Schema.SeriesContext;

namespace API.Controllers.DTOs;

/// <summary>
/// Shortened Version of <see cref="Series"/>
/// </summary>
public record MinimalSeries(string Key, string Name, string Description, SeriesReleaseStatus ReleaseStatus, IEnumerable<SourceId<Series>> SourceIds, string? FileLibraryId = null, string? Language = null, string CoverUrl = "") : Identifiable(Key)
{
    /// <summary>
    /// Name of the Series
    /// </summary>
    [Required]
    [Description("Name of the Series")]
    public string Name { get; init; } = Name;
    
    /// <summary>
    /// Description of the Series
    /// </summary>
    [Required]
    [Description("Description of the Series")]
    public string Description { get; init; } = Description;
    
    /// <summary>
    /// ReleaseStatus of the Series
    /// </summary>
    [Required]
    [Description("ReleaseStatus of the Series")]
    public SeriesReleaseStatus ReleaseStatus { get; init; } = ReleaseStatus;
    
    /// <summary>
    /// Ids of the Series on MangaConnectors
    /// </summary>
    [Required]
    [Description("Ids of the Series on MangaConnectors")]
    public IEnumerable<SourceId<Series>> SourceIds { get; init; } = SourceIds;

    /// <summary>
    /// External cover image URL from the connector
    /// </summary>
    [Description("External cover image URL from the connector")]
    public string CoverUrl { get; init; } = CoverUrl;

    /// <summary>
    /// Id of the Library the Series gets downloaded to
    /// </summary>
    [Description("Id of the Library the Series gets downloaded to")]
    public string? FileLibraryId { get; init; } = FileLibraryId;

    /// <summary>
    /// Content Language (e.g. translation language)
    /// </summary>
    [Description("Content Language")]
    public string? Language { get; init; } = Language;
}