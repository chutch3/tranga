using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace API.Schema.SeriesContext;

[PrimaryKey("Tag")]
public class SeriesTag(string tag)
{
    [StringLength(64)]
    [Required]
    public string Tag { get; init; } = tag;

    public override string ToString() => Tag;
}