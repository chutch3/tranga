namespace API.Services;

/// <summary>
/// Generates and caches chapter preview thumbnails from CBZ archive files.
/// </summary>
public interface IChapterThumbnailService
{
    /// <summary>
    /// Generates a 200×300 JPEG thumbnail from the first image in the archive
    /// and writes it to <paramref name="destinationPath"/>.
    /// </summary>
    /// <param name="archivePath">Full path to the CBZ/ZIP archive.</param>
    /// <param name="destinationPath">Full path where the JPEG thumbnail should be written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the thumbnail was generated and written successfully;
    /// <c>false</c> if the archive is unreadable, empty, or contains no recognised image entries.
    /// Never throws — callers should treat <c>false</c> as a 404.
    /// </returns>
    Task<bool> GenerateThumbnailAsync(string archivePath, string destinationPath, CancellationToken cancellationToken = default);
}
