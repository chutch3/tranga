using API.MangaConnectors;
using API.Schema.SeriesContext;

namespace API.Acquirers;

/// <summary>
/// Produces the .cbz archive for a chapter at a specified path on disk. Implementations encapsulate
/// "how to turn a Chapter into a file" — image-by-image scrape, direct archive download, torrent
/// hand-off, etc. — behind a single seam the chapter download worker can call uniformly.
/// </summary>
public interface IChapterAcquirer
{
    /// <summary>The acquisition kind this implementation handles. Used by the dispatcher to pick the
    /// right acquirer for a given connector's declared Kind.</summary>
    AcquisitionKind Kind { get; }

    /// <summary>
    /// Acquires the chapter and writes a .cbz to <paramref name="saveArchiveFilePath"/>.
    /// Returns the path on success, or null on failure. Implementations are responsible for logging
    /// their own errors.
    /// </summary>
    /// <param name="chapter">The chapter source-id row, with Obj+ParentManga eagerly loaded.</param>
    /// <param name="source">The connector that originally produced this chapter.</param>
    /// <param name="saveArchiveFilePath">Absolute path the .cbz must be written to.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> AcquireAsync(
        SourceId<Chapter> chapter,
        SeriesSource source,
        string saveArchiveFilePath,
        CancellationToken ct);
}
