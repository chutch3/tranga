using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace API.Controllers.Requests;

/// <summary>Prowlarr instance to sync indexers from.</summary>
public record SetProwlarrRecord
{
    [Required]
    [Description("Base URL of the Prowlarr instance, e.g. http://prowlarr:9696")]
    public required string BaseUrl { get; init; }

    [Required]
    [Description("Prowlarr API key")]
    public required string ApiKey { get; init; }
}
