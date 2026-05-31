using API.MangaConnectors;
using API.Schema.SeriesContext;
using log4net;

namespace API.Acquirers;

/// <summary>
/// Acquires a chapter by downloading a single already-packaged archive (.cbz/.cbr) from
/// <see cref="SourceId{T}.WebsiteUrl"/>. Connectors whose Kind is <see cref="AcquisitionKind.DirectArchive"/>
/// are contractually responsible for populating <c>WebsiteUrl</c> with the archive URL itself,
/// not a viewer/page URL.
/// </summary>
public class DirectArchiveAcquirer(HttpClient http) : IChapterAcquirer
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(DirectArchiveAcquirer));

    public AcquisitionKind Kind => AcquisitionKind.DirectArchive;

    public async Task<string?> AcquireAsync(
        SourceId<Chapter> chapter,
        SeriesSource source,
        string saveArchiveFilePath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chapter.WebsiteUrl))
        {
            Log.ErrorFormat("Cannot acquire chapter {0}: WebsiteUrl is empty (a DirectArchive connector must populate it with the archive URL).", chapter);
            return null;
        }

        try
        {
            using HttpResponseMessage response = await http.GetAsync(chapter.WebsiteUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.ErrorFormat("Failed to download archive {0}: HTTP {1}", chapter.WebsiteUrl, (int)response.StatusCode);
                return null;
            }

            await using FileStream fs = File.Create(saveArchiveFilePath);
            await response.Content.CopyToAsync(fs, ct);
            return saveArchiveFilePath;
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Failed to acquire archive {0}: {1}", chapter.WebsiteUrl, ex);
            // Best effort cleanup of any partial file
            try { if (File.Exists(saveArchiveFilePath)) File.Delete(saveArchiveFilePath); } catch { /* swallow */ }
            return null;
        }
    }
}
