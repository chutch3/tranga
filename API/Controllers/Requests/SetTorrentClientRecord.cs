using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace API.Controllers.Requests;

/// <summary>Torrent client (qBittorrent) Web API connection details.</summary>
public record SetTorrentClientRecord
{
    [Required]
    [Description("Base URL of the torrent client Web API, e.g. http://qbittorrent:8080")]
    public required string BaseUrl { get; init; }

    [Description("Torrent client username")]
    public string Username { get; init; } = "";

    [Description("Torrent client password")]
    public string Password { get; init; } = "";
}
