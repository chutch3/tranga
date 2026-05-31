using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace API.Schema.SeriesContext;

[PrimaryKey("Key")]
public class VolumeMetadata : Identifiable
{
    [StringLength(64)] public string MangaId { get; private set; } = null!;
    public Series Series { get; private set; } = null!;
    public int VolumeNumber { get; internal set; }
    [StringLength(512)] public string? Title { get; internal set; }
    [StringLength(512)] public string? ArchiveFileName { get; internal set; }

    public VolumeMetadata(Series manga, int volumeNumber, string? title = null)
        : base(TokenGen.CreateToken(typeof(VolumeMetadata), manga.Key, volumeNumber.ToString()))
    {
        MangaId = manga.Key;
        Series = manga;
        VolumeNumber = volumeNumber;
        Title = title;
    }

    // EF ONLY
    internal VolumeMetadata(string key, string mangaId, int volumeNumber, string? title, string? archiveFileName)
        : base(key)
    {
        MangaId = mangaId;
        VolumeNumber = volumeNumber;
        Title = title;
        ArchiveFileName = archiveFileName;
    }
}
