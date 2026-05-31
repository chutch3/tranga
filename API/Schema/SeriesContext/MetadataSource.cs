using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace API.Schema.SeriesContext;

[PrimaryKey("MangaId")]
public class MetadataSource
{
    [Key] [StringLength(64)] public string MangaId { get; private set; } = null!;
    [ForeignKey(nameof(MangaId))] public Series Series { get; private set; } = null!;
    public MetadataSourceType SourceType { get; internal set; }
    [StringLength(256)] public string? ExternalId { get; internal set; }
    public MetadataSourceStatus Status { get; internal set; }
    public DateTime? LastSyncedAt { get; internal set; }
    public float? MatchScore { get; internal set; }

    /// <summary>
    /// EF ONLY!!!
    /// </summary>
    internal MetadataSource(string mangaId, MetadataSourceType sourceType, MetadataSourceStatus status)
    {
        MangaId = mangaId;
        SourceType = sourceType;
        Status = status;
    }
}

public enum MetadataSourceType
{
    Connector,
    MangaDex,
    AniList,
    Manual
}

public enum MetadataSourceStatus
{
    Unlinked,
    AutoMatched,
    Ambiguous,
    Confirmed,
    NoMatch
}
