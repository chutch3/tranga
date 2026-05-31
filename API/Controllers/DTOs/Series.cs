using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using API.Schema.SeriesContext;

namespace API.Controllers.DTOs;

/// <summary>
/// <see cref="Schema.SeriesContext.Series"/> DTO
/// </summary>
public sealed record Series(string Key, string Name, string Description, SeriesReleaseStatus ReleaseStatus, IEnumerable<SourceId<Series>> SourceIds, float IgnoreChaptersBefore, uint? Year, string? OriginalLanguage, IEnumerable<Author> Authors, IEnumerable<string> Tags, IEnumerable<Link> Links, IEnumerable<AltTitle> AltTitles, string? FileLibraryId, string CoverUrl = "")
    : MinimalSeries(Key, Name, Description, ReleaseStatus, SourceIds, FileLibraryId, OriginalLanguage, CoverUrl)
{
    /// <summary>
    /// Chapter cutoff for Downloads (Chapters before this will not be downloaded)
    /// </summary>
    [Required]
    [Description("Chapter cutoff for Downloads (Chapters before this will not be downloaded)")]
    public float IgnoreChaptersBefore { get; init; } = IgnoreChaptersBefore;
    
    /// <summary>
    /// Release Year
    /// </summary>
    [Description("Release Year")]
    public uint? Year { get; init; } = Year;
    
    /// <summary>
    /// Release Language
    /// </summary>
    [Description("Release Language")]
    public string? OriginalLanguage { get; init; } = OriginalLanguage;
    
    /// <summary>
    /// Author-names
    /// </summary>
    [Required]
    [Description("Author-names")]
    public IEnumerable<Author> Authors { get; init; } = Authors;
    
    /// <summary>
    /// Series Tags
    /// </summary>
    [Required]
    [Description("Series Tags")]
    public IEnumerable<string> Tags { get; init; } = Tags;
    
    /// <summary>
    /// Links for more Metadata
    /// </summary>
    [Required]
    [Description("Links for more Metadata")]
    public IEnumerable<Link> Links { get; init; } = Links;
    
    /// <summary>
    /// Alt Titles of Series
    /// </summary>
    [Required]
    [Description("Alt Titles of Series")]
    public IEnumerable<AltTitle> AltTitles { get; init; } = AltTitles;
    

}