namespace API.Acquirers;

/// <summary>
/// How a connector turns a Chapter into a .cbz on disk. Each SeriesSource declares one kind;
/// StartNewChapterDownloadsWorker uses the kind to dispatch to the matching IChapterAcquirer.
/// </summary>
public enum AcquisitionKind
{
    /// <summary>Image-by-image scrape, packaged into a .cbz client-side (manga + scrape sites).</summary>
    ImageList,

    /// <summary>Single HTTP GET of an already-packaged .cbz/.cbr (public-domain CBZ sites).</summary>
    DirectArchive,

    /// <summary>Magnet/torrent handed off to an external torrent client; result polled for completion.</summary>
    Torrent
}
