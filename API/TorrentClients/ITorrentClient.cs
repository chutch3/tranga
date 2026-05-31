namespace API.TorrentClients;

/// <summary>
/// Owned abstraction over an external BitTorrent client (qBittorrent, Deluge, etc.). Tranga uses
/// a caller-supplied <c>tag</c> as the torrent's identifier — this sidesteps the cost of extracting
/// the BitTorrent info-hash from a magnet/.torrent on the Tranga side. Implementations tag the
/// torrent so it can be looked up again by the same tag.
/// </summary>
public interface ITorrentClient
{
    /// <summary>
    /// Adds <paramref name="downloadUrl"/> (a magnet or .torrent URL) to the client. Returns the
    /// <paramref name="tag"/> on success, null on failure.
    /// </summary>
    Task<string?> Add(string downloadUrl, string saveDir, string tag, CancellationToken ct);

    /// <summary>Returns the current status of the torrent tagged with <paramref name="tag"/>, or null if not found.</summary>
    Task<TorrentStatus?> GetStatus(string tag, CancellationToken ct);

    /// <summary>Removes the torrent tagged with <paramref name="tag"/>. No-ops if not found.</summary>
    Task Remove(string tag, bool deleteData, CancellationToken ct);
}

public abstract record TorrentStatus
{
    public sealed record Downloading(double Progress) : TorrentStatus;
    public sealed record Completed(string SavePath) : TorrentStatus;
    public sealed record Errored(string Reason) : TorrentStatus;
}
