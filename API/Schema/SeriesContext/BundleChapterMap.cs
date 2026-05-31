using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace API.Schema.SeriesContext;

public class BundleChapterMap
{
    [StringLength(64)] public string VolumeKey { get; set; } = null!;
    [StringLength(64)] public string ChapterKey { get; set; } = null!;
    public int StartPage { get; set; }
    public int PageCount { get; set; }

    public VolumeMetadata Volume { get; set; } = null!;
    public Chapter Chapter { get; set; } = null!;
}
