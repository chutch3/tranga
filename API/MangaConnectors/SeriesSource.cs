using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using API.Acquirers;
using API.MangaDownloadClients;
using API.Schema.SeriesContext;
using log4net;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace API.MangaConnectors;

[PrimaryKey("Name")]
[Table("MangaConnector")] // Existing DB table; a follow-up hand-crafted migration is needed to rename to "SeriesSource" (see TECHNICAL_DEBT.md).
public abstract class SeriesSource(string name, string[] supportedLanguages, string[] baseUris, string iconUrl, TrangaSettings settings)
{
    [NotMapped] internal IDownloadClient downloadClient { get; init; } = null!;
    [NotMapped] protected ILog Log { get; init; } = LogManager.GetLogger(name);
    [StringLength(32)] public string Name { get; init; } = name;
    [StringLength(8)] public string[] SupportedLanguages { get; init; } = supportedLanguages;
    [StringLength(2048)] public string IconUrl { get; init; } = iconUrl;
    [StringLength(256)] public string[] BaseUris { get; init; } = baseUris;
    public bool Enabled { get; internal set; } = true;
    protected TrangaSettings Settings => settings;

    /// <summary>How this source delivers chapters. Drives dispatch to the matching IChapterAcquirer.</summary>
    [NotMapped] public abstract AcquisitionKind Kind { get; }

    public abstract Task<(Series, SourceId<Series>)[]> SearchManga(string mangaSearchName);

    public abstract Task<(Series, SourceId<Series>)?> GetMangaFromUrl(string url);

    public abstract Task<(Series, SourceId<Series>)?> GetMangaFromId(string mangaIdOnSite);

    public abstract Task<(Chapter, SourceId<Chapter>)[]> GetChapters(SourceId<Series> mangaId,
        string? language = null);

    internal abstract Task<string[]> GetChapterImageUrls(SourceId<Chapter> chapterId);

    public bool UrlMatchesConnector(string url) => BaseUris.Any(baseUri => Regex.IsMatch(url, "https?://" + baseUri + "/.*"));

    internal async Task<string?> SaveCoverImageToCache(SourceId<Series> mangaId, int retries = 3)
    {
        if(retries < 0)
            return null;

        Regex urlRex = new (@"https?:\/\/((?:[a-zA-Z0-9-]+\.)+[a-zA-Z0-9]+)\/(?:.+\/)*(.+\.([a-zA-Z]+))");
        //https?:\/\/[a-zA-Z0-9-]+\.([a-zA-Z0-9-]+\.[a-zA-Z0-9]+)\/(?:.+\/)*(.+\.([a-zA-Z]+)) for only second level domains
        Match match = urlRex.Match(mangaId.Obj.CoverUrl);
        // Clean ONCE up front so the file written to disk and the value returned are always identical.
        string filename = $"{match.Groups[1].Value}-{mangaId.ObjId}.{mangaId.MangaConnectorName}.{match.Groups[3].Value}".CleanNameForWindows();
        string saveImagePath = Path.Join(settings.CoverImageCacheOriginal, filename);

        if (File.Exists(saveImagePath))
            return filename;

        using HttpResponseMessage coverResult = await downloadClient.MakeRequest(mangaId.Obj.CoverUrl, RequestType.MangaCover, $"https://{match.Groups[1].Value}");
        if ((int)coverResult.StatusCode < 200 || (int)coverResult.StatusCode >= 300)
            return await SaveCoverImageToCache(mangaId, retries - 1);

        try
        {
            using MemoryStream ms = new();
            await (await coverResult.Content.ReadAsStreamAsync()).CopyToAsync(ms);
            ms.Position = 0;
            Directory.CreateDirectory(settings.CoverImageCacheOriginal);
            File.WriteAllBytes(saveImagePath, ms.ToArray());

            using Image image = await Image.LoadAsync(ms); // Use stream for async load
            Directory.CreateDirectory(settings.CoverImageCacheLarge);
            using Image large = image.Clone(x => x.Resize(new ResizeOptions
                { Size = Constants.ImageLgSize, Mode = ResizeMode.Max }));
            large.SaveAsJpeg(Path.Join(settings.CoverImageCacheLarge, filename), new (){ Quality = 40 });

            Directory.CreateDirectory(settings.CoverImageCacheMedium);
            using Image medium = image.Clone(x => x.Resize(new ResizeOptions
                { Size = Constants.ImageMdSize, Mode = ResizeMode.Max }));
            medium.SaveAsJpeg(Path.Join(settings.CoverImageCacheMedium, filename), new (){ Quality = 40 });

            Directory.CreateDirectory(settings.CoverImageCacheSmall);
            using Image small = image.Clone(x => x.Resize(new ResizeOptions
                { Size = Constants.ImageSmSize, Mode = ResizeMode.Max }));
            small.SaveAsJpeg(Path.Join(settings.CoverImageCacheSmall, filename), new (){ Quality = 40 });
        }
        catch (Exception e)
        {
            Log.Error(e);
        }


        return filename;
    }

    public virtual async Task<Stream?> DownloadImage(string imageUrl, CancellationToken ct)
    {
        HttpResponseMessage requestResult = await downloadClient.MakeRequest(imageUrl, RequestType.MangaImage, cancellationToken: ct);
        return requestResult.IsSuccessStatusCode ? await requestResult.Content.ReadAsStreamAsync(ct) : null;
    }
}
