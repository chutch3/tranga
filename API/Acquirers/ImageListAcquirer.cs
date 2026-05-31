using System.IO.Compression;
using System.Text;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using log4net;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Binarization;

namespace API.Acquirers;

/// <summary>
/// Acquires a chapter by fetching each page image individually from the connector, optionally
/// re-encoding them (compression / black-and-white), and packaging the lot into a .cbz with a
/// ComicInfo.xml. This is the historical Tranga path — preserved verbatim from
/// DownloadChapterFromSourceWorker so the rename is a behaviour-preserving refactor.
/// </summary>
public class ImageListAcquirer(TrangaSettings settings) : IChapterAcquirer
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(ImageListAcquirer));

    public AcquisitionKind Kind => AcquisitionKind.ImageList;

    public async Task<string?> AcquireAsync(
        SourceId<Chapter> chapter,
        SeriesSource source,
        string saveArchiveFilePath,
        CancellationToken ct)
    {
        List<Stream> images = new();
        try
        {
            string[] imageUrls = await source.GetChapterImageUrls(chapter);
            foreach (string imageUrl in imageUrls)
            {
                Stream? imageStream = await source.DownloadImage(imageUrl, ct);
                if (imageStream is not null)
                    images.Add(await ProcessImage(imageStream, ct));
            }

            Log.Debug($"Images downloaded for chapter {chapter.Obj}. Packaging...");

            // ZIP-it and ship-it
            using (ZipArchive archive = ZipFile.Open(saveArchiveFilePath, ZipArchiveMode.Create))
            {
                if (Constants.CreateComicInfoXml)
                {
                    Log.Debug("Writing ComicInfo.xml");
                    Stream comicStream = archive.CreateEntry("ComicInfo.xml").Open();
                    string comicInfo = chapter.Obj.GetComicInfoXmlString();
                    await comicStream.WriteAsync(Encoding.UTF8.GetBytes(comicInfo), ct);
                    await comicStream.DisposeAsync();
                }

                for (int i = 0; i < images.Count; i++)
                {
                    Log.Debug($"Packaging images to archive {chapter.Obj} , image {i}");
                    Stream zipStream = archive.CreateEntry($"{i}.jpg").Open();
                    Stream imageStream = images[i];
                    imageStream.Position = 0;
                    await imageStream.CopyToAsync(zipStream, ct);
                    await zipStream.DisposeAsync();
                }
            }

            return saveArchiveFilePath;
        }
        catch (Exception ex)
        {
            Log.ErrorFormat("Failed to download chapter {0}: {1}", chapter.Obj, ex);
            return null;
        }
        finally
        {
            images.ForEach(i => i.Dispose());
        }
    }

    private async Task<Stream> ProcessImage(Stream imageStream, CancellationToken cancellationToken)
    {
        Log.Debug("Processing image");
        imageStream.Position = 0;
        if (!settings.BlackWhiteImages && settings.ImageCompression == 100)
        {
            Log.Debug("No processing requested for image");
            // No new stream is created; the caller still owns and disposes imageStream.
            return imageStream;
        }

        MemoryStream processedImage = new();
        try
        {
            using Image image = await Image.LoadAsync(imageStream, cancellationToken);
            Log.Debug("Image loaded");
            if (settings.BlackWhiteImages)
                image.Mutate(i => i.ApplyProcessor(new AdaptiveThresholdProcessor()));
            await image.SaveAsJpegAsync(processedImage, new JpegEncoder
            {
                Quality = settings.ImageCompression
            }, cancellationToken);
            Log.Debug("Image processed");
        }
        catch (Exception e)
        {
            Log.Error(e);
            // Processing failed: fall back to the raw source stream (caller disposes it).
            await processedImage.DisposeAsync();
            return imageStream;
        }
        // Processing succeeded: a new stream now holds the image data, so dispose the source to avoid
        // leaking the underlying network stream/handle.
        await imageStream.DisposeAsync();
        processedImage.Position = 0;
        return processedImage;
    }
}
