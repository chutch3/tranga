using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace API.Controllers.Requests;

/// <summary>Metron (metron.cloud) account credentials for metadata lookups.</summary>
public record SetMetronRecord
{
    [Required]
    [Description("Metron account username")]
    public required string Username { get; init; }

    [Required]
    [Description("Metron account password")]
    public required string Password { get; init; }
}
