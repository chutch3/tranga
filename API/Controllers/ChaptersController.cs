using API.Controllers.DTOs;
using API.Controllers.Requests;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using API.Services;
using API.Workers.MangaDownloadWorkers;
using API.Workers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
using Chapter = API.Controllers.DTOs.Chapter;
using MangaConnectorImpl = API.MangaConnectors.SeriesSource;


// ReSharper disable InconsistentNaming

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class ChaptersController(SeriesContext context, TrangaSettings settings, IEnumerable<MangaConnectorImpl> connectors, IWorkerQueue workerQueue, IChapterThumbnailService chapterThumbnailService) : ControllerBase
{
    /// <summary>
    /// Returns all <see cref="Schema.SeriesContext.Chapter"/> of <see cref="Schema.SeriesContext.Series"/> with <paramref name="MangaId"/>
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <param name="filter"></param>
    /// <param name="page">Page to request (default 1)</param>
    /// <param name="pageSize">Size of Page (default 10)</param>
    /// <response code="200"></response>
    /// <response code="400">Page data wrong</response>
    /// <response code="500">Error during Database request</response>
    [HttpPost("Series/{MangaId}")]
    [ProducesResponseType<PagedResponse<Chapter>>(Status200OK, "application/json")]
    [ProducesResponseType(Status400BadRequest)]
    [ProducesResponseType(Status500InternalServerError)]
    public async Task<Results<Ok<PagedResponse<Chapter>>, BadRequest, InternalServerError>> GetChapters(string MangaId, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]ChapterFilterRecord? filter = null, [FromQuery]int page = 1, [FromQuery]int pageSize = 10)
    {
        if (page < 1 || pageSize < 1)
            return TypedResults.BadRequest();

        IQueryable<Schema.SeriesContext.Chapter> queryable = context.Chapters
            .Include(ch => ch.SourceIds)
            .Where(ch => ch.ParentMangaId == MangaId);

        if (filter is not null)
        {
            if(filter.Downloaded.HasValue)
                queryable = queryable.Where(ch => ch.Downloaded == filter.Downloaded.Value);
            if(filter.Name is not null && !string.IsNullOrWhiteSpace(filter.Name))
                queryable = queryable.Where(ch => ch.Title != null && ch.Title.Contains(filter.Name));
            if(filter.VolumeNumber is not null)
                queryable = queryable.Where(ch => ch.VolumeNumber == filter.VolumeNumber);
            if(filter.ChapterNumber is not null && !string.IsNullOrWhiteSpace(filter.ChapterNumber))
                queryable = queryable.Where(ch => ch.ChapterNumber == filter.ChapterNumber);
        }

        if (await queryable.ToListAsync(HttpContext.RequestAborted) is not { } dbChapters)
            return TypedResults.InternalServerError();
        PagedResponse<Chapter> pagedResponse = dbChapters.OrderDescending().CreatePagedResponse(page, pageSize)
            .ToType(c =>
            {
                IEnumerable<DTOs.SourceId<Chapter>> ids = c.SourceIds.Select(id =>
                    new DTOs.SourceId<Chapter>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
                return new Chapter(c.Key, c.ParentMangaId, c.VolumeNumber, c.ChapterNumber, c.Title, ids, c.Downloaded,
                    c.FileName);
            });

        return TypedResults.Ok(pagedResponse);
    }

    /// <summary>
    /// Returns the latest <see cref="Chapter"/> of requested <see cref="Schema.SeriesContext.Series"/>
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <response code="200"></response>
    /// <response code="204">No available chapters</response>
    /// <response code="404"><see cref="Schema.SeriesContext.Series"/> with <paramref name="MangaId"/> not found.</response>
    [HttpGet("LatestAvailable/{MangaId}")]
    [ProducesResponseType<int>(Status200OK, "application/json")]
    [ProducesResponseType(Status204NoContent)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<Chapter>, NoContent, NotFound<string>>> GetLatestChapter(string MangaId)
    {
        // 1. Explicitly check if the parent Series actually exists
        if (!await context.Series.AnyAsync(m => m.Key == MangaId, HttpContext.RequestAborted))
            return TypedResults.NotFound(nameof(MangaId));

        // 2. Fetch the chapters
        var dbChapters = await context.Chapters.Include(ch => ch.SourceIds)
            .Where(ch => ch.ParentMangaId == MangaId)
            .ToListAsync(HttpContext.RequestAborted);

        Schema.SeriesContext.Chapter? c = dbChapters.Max();

        // 3. If Series exists but has 0 chapters, return NoContent
        if (c is null)
            return TypedResults.NoContent();

        IEnumerable<DTOs.SourceId<Chapter>> ids = c.SourceIds.Select(id =>
            new DTOs.SourceId<Chapter>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));

        return TypedResults.Ok(new Chapter(c.Key, c.ParentMangaId, c.VolumeNumber, c.ChapterNumber, c.Title, ids, c.Downloaded, c.FileName));
    }
    /// <summary>
    /// Returns the latest <see cref="Chapter"/> of requested <see cref="Schema.SeriesContext.Series"/> that is downloaded
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <response code="200"></response>
    /// <response code="204">No available chapters</response>
    /// <response code="404"><see cref="Schema.SeriesContext.Series"/> with <paramref name="MangaId"/> not found.</response>
    /// <response code="412">Could not retrieve the maximum chapter-number</response>
    /// <response code="503">Retry after timeout, updating value</response>
    [HttpGet("LatestDownloaded/{MangaId}")]
    [ProducesResponseType<Chapter>(Status200OK, "application/json")]
    [ProducesResponseType(Status204NoContent)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType(Status412PreconditionFailed)]
    [ProducesResponseType(Status503ServiceUnavailable)]
    public async Task<Results<Ok<Chapter>, NoContent, NotFound<string>, StatusCodeHttpResult>>  GetLatestChapterDownloaded(string MangaId)
    {
        if(await context.Chapters.Include(ch => ch.SourceIds)
               .Where(ch => ch.ParentMangaId == MangaId && ch.Downloaded)
               .ToListAsync(HttpContext.RequestAborted)
           is not { } dbChapters)
            return TypedResults.NotFound(nameof(MangaId));

        Schema.SeriesContext.Chapter? c = dbChapters.Max();
        if (c is null)
            return TypedResults.NoContent();

        IEnumerable<DTOs.SourceId<Chapter>> ids = c.SourceIds.Select(id =>
            new DTOs.SourceId<Chapter>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
        return TypedResults.Ok(new Chapter(c.Key, c.ParentMangaId, c.VolumeNumber, c.ChapterNumber, c.Title, ids, c.Downloaded, c.FileName));
    }

    /// <summary>
    /// Configure the <see cref="Chapter"/> cut-off for <see cref="Schema.SeriesContext.Series"/>
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <param name="chapterThreshold">Threshold (<see cref="Chapter"/> ChapterNumber)</param>
    /// <response code="202"></response>
    /// <response code="404"><see cref="Schema.SeriesContext.Series"/> with <paramref name="MangaId"/> not found.</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpPatch("IgnoreBefore/{MangaId}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok, NotFound<string>, InternalServerError<string>>> IgnoreChaptersBefore(string MangaId, [FromBody]float chapterThreshold)
    {
        if (await context.Series.FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));

        manga.IgnoreChaptersBefore = chapterThreshold;
        if(await context.Sync(HttpContext.RequestAborted, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);

        return TypedResults.Ok();
    }

    /// <summary>
    /// Returns <see cref="Chapter"/> with <paramref name="ChapterId"/>
    /// </summary>
    /// <param name="ChapterId"><see cref="Chapter"/>.Key</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Chapter"/> with <paramref name="ChapterId"/> not found</response>
    [HttpGet("{ChapterId}")]
    [ProducesResponseType<Chapter>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<Chapter>, NotFound<string>>> GetChapter (string ChapterId)
    {
        if (await context.Chapters.FirstOrDefaultAsync(c => c.Key == ChapterId, HttpContext.RequestAborted) is not { } chapter)
            return TypedResults.NotFound(nameof(ChapterId));

        IEnumerable<DTOs.SourceId<Chapter>> ids = chapter.SourceIds.Select(id =>
            new DTOs.SourceId<Chapter>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
        return TypedResults.Ok(new Chapter(chapter.Key, chapter.ParentMangaId, chapter.VolumeNumber, chapter.ChapterNumber, chapter.Title,ids, chapter.Downloaded, chapter.FileName));
    }

    /// <summary>
    /// Updates mutable metadata (<see cref="Schema.SeriesContext.Chapter.FileName"/> and <see cref="Schema.SeriesContext.Chapter.VolumeNumber"/>) on a <see cref="Chapter"/>
    /// </summary>
    /// <param name="ChapterId"><see cref="Chapter"/>.Key</param>
    /// <param name="patch">Fields to update</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Chapter"/> with <paramref name="ChapterId"/> not found</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpPatch("{ChapterId}")]
    public async Task<Results<Ok, NotFound<string>, InternalServerError<string>>> UpdateChapter(string ChapterId, [FromBody] PatchChapterRecord patch)
    {
        if (await context.Chapters.FirstOrDefaultAsync(c => c.Key == ChapterId, HttpContext.RequestAborted) is not { } chapter)
            return TypedResults.NotFound(nameof(ChapterId));

        string? oldFileName = chapter.FileName;
        bool fileNameChanged = patch.FileName != oldFileName;

        // Trigger the worker we built!
        if (fileNameChanged && oldFileName is not null && patch.FileName is not null)
        {
            // Add the file move to your background queue
            var moveWorker = new MoveFileOrFolderWorker(toLocation: patch.FileName, fromLocation: oldFileName);
            workerQueue.AddWorker(moveWorker);
        }

        chapter.FileName = patch.FileName;
        chapter.VolumeNumber = patch.VolumeNumber;

        if (await context.Sync(HttpContext.RequestAborted, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);

        return TypedResults.Ok();
    }

    /// <summary>
    /// Deletes <see cref="Chapter"/> with <paramref name="ChapterId"/>
    /// </summary>
    /// <param name="ChapterId"><see cref="Chapter"/>.Key</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Chapter"/> with <paramref name="ChapterId"/> not found</response>
    [HttpDelete("{ChapterId}")]
    public async Task<Results<Ok, NotFound<string>>> DeleteChapter(string ChapterId)
    {
        var chapter = await context.Chapters.FirstOrDefaultAsync(c => c.Key == ChapterId, HttpContext.RequestAborted);
        if (chapter == null)
            return TypedResults.NotFound(nameof(ChapterId));

        context.Chapters.Remove(chapter);
        await context.SaveChangesAsync(HttpContext.RequestAborted);

        return TypedResults.Ok();
    }

    /// <summary>
    /// Returns the <see cref="DTOs.SourceId{Chapter}"/> with <see cref="DTOs.SourceId{Chapter}"/>.Key
    /// </summary>
    /// <param name="MangaConnectorIdId">Key of <see cref="DTOs.SourceId{Chapter}"/></param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="DTOs.SourceId{Chapter}"/> with <paramref name="MangaConnectorIdId"/> not found</response>
    [HttpGet("ConnectorId/{MangaConnectorIdId}")]
    [ProducesResponseType<DTOs.SourceId<Chapter>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<DTOs.SourceId<Chapter>>, NotFound<string>>> GetChapterSourceId (string MangaConnectorIdId)
    {
        if (await context.MangaConnectorToChapter.FirstOrDefaultAsync(c => c.Key == MangaConnectorIdId, HttpContext.RequestAborted) is not { } mcIdManga)
            return TypedResults.NotFound(nameof(MangaConnectorIdId));

        DTOs.SourceId<Chapter> result = new (mcIdManga.Key, mcIdManga.MangaConnectorName, mcIdManga.ObjId, mcIdManga.IdOnConnectorSite, mcIdManga.WebsiteUrl, mcIdManga.UseForDownload);

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Deletes the <see cref="DTOs.SourceId{Chapter}"/> with <see cref="DTOs.SourceId{Chapter}"/>.Key
    /// </summary>
    /// <param name="MangaConnectorIdId">Key of <see cref="DTOs.SourceId{Chapter}"/></param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="DTOs.SourceId{Chapter}"/> with <paramref name="MangaConnectorIdId"/> not found</response>
    [HttpDelete("ConnectorId/{MangaConnectorIdId}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok, NotFound<string>>> DeleteChapterSourceId (string MangaConnectorIdId)
    {
        if (await context.MangaConnectorToChapter.Where(c => c.Key == MangaConnectorIdId).ExecuteDeleteAsync(HttpContext.RequestAborted) < 1)
            return TypedResults.NotFound(nameof(MangaConnectorIdId));
        return TypedResults.Ok();
    }

    /// <summary>
    /// (Un-)Marks <see cref="Chapter"/> as requested for Download from <see cref="API.MangaConnectors.SeriesSource"/>
    /// </summary>
    /// <param name="ChapterId"><see cref="Chapter"/> with <paramref name="ChapterId"/></param>
    /// <param name="MangaConnectorName"><see cref="API.MangaConnectors.SeriesSource"/> with <paramref name="MangaConnectorName"/></param>
    /// <param name="IsRequested">true to mark as requested, false to mark as not-requested</param>
    /// <response code="200"></response>
    /// <response code="404"><paramref name="ChapterId"/> or <paramref name="MangaConnectorName"/> not found</response>
    /// <response code="428"><see cref="Chapter"/> is not linked to <see cref="API.MangaConnectors.SeriesSource"/> yet. Search for <see cref="Chapter"/> on <see cref="API.MangaConnectors.SeriesSource"/> first (to create a <see cref="DTOs.SourceId{T}"/>).</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpPatch("{ChapterId}/DownloadFrom/{MangaConnectorName}/{IsRequested}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound,  "text/plain")]
    [ProducesResponseType<string>(Status428PreconditionRequired,  "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError,  "text/plain")]
    public async Task<Results<Ok, NotFound<string>, StatusCodeHttpResult, InternalServerError<string>>> MarkAsRequested(string ChapterId, string MangaConnectorName, bool IsRequested)
    {
        if (await context.Chapters.FirstOrDefaultAsync(ch => ch.Key == ChapterId, HttpContext.RequestAborted) is not { } _)
            return TypedResults.NotFound(nameof(ChapterId));
        if(!connectors.Any(c => c.Name.Equals(MangaConnectorName, StringComparison.InvariantCultureIgnoreCase)))
            return TypedResults.NotFound(nameof(MangaConnectorName));

        if (await context.MangaConnectorToChapter
                .FirstOrDefaultAsync(id => id.MangaConnectorName == MangaConnectorName && id.ObjId == ChapterId, HttpContext.RequestAborted)
            is not { } chId)
        {
            return TypedResults.StatusCode(Status428PreconditionRequired);
        }

        chId.UseForDownload = IsRequested;
        if(await context.Sync(HttpContext.RequestAborted, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);

        if (IsRequested)
        {
            DownloadChapterFromSourceWorker worker = new(chId, connectors, settings);
            workerQueue.AddWorker(worker);
        }

        return TypedResults.Ok();
    }

    /// <summary>
    /// Manually assigns a volume number to a <see cref="Schema.SeriesContext.Chapter"/>.
    /// Passing null clears the volume assignment and confidence.
    /// </summary>
    /// <param name="ChapterId"><see cref="Schema.SeriesContext.Chapter"/>.Key</param>
    /// <param name="patch">Volume number to assign (null to clear)</param>
    /// <response code="200">Volume number updated</response>
    /// <response code="404"><see cref="Schema.SeriesContext.Chapter"/> with <paramref name="ChapterId"/> not found</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpPut("{ChapterId}/volume")]
    [ProducesResponseType<DTOs.ChapterVolumeAssignmentResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok<DTOs.ChapterVolumeAssignmentResult>, NotFound<string>, InternalServerError<string>>> AssignChapterVolume(string ChapterId, [FromBody] PatchChapterVolumeRecord patch)
    {
        if (await context.Chapters.FirstOrDefaultAsync(c => c.Key == ChapterId, HttpContext.RequestAborted) is not { } chapter)
            return TypedResults.NotFound(nameof(ChapterId));

        if (patch.VolumeNumber is not null)
        {
            chapter.VolumeNumber = patch.VolumeNumber;
            chapter.MetadataConfidence = Schema.SeriesContext.MetadataConfidence.Manual;
        }
        else
        {
            chapter.VolumeNumber = null;
            chapter.MetadataConfidence = null;
        }

        if (await context.Sync(HttpContext.RequestAborted, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);

        return TypedResults.Ok(new DTOs.ChapterVolumeAssignmentResult(
            chapter.Key,
            chapter.ChapterNumber,
            chapter.VolumeNumber,
            chapter.MetadataConfidence?.ToString()
        ));
    }

    /// <summary>
    /// Returns a 200×300 JPEG preview thumbnail of the first page of the chapter archive.
    /// The thumbnail is generated lazily and cached at <c>{AppData}/previews/{chapter.Key}.jpg</c>.
    /// </summary>
    /// <param name="ChapterId"><see cref="Schema.SeriesContext.Chapter"/>.Key</param>
    /// <response code="200">JPEG thumbnail, 200×300 pixels</response>
    /// <response code="404">Chapter not found, archive unreadable, no images in archive, or chapter is bundled</response>
    [HttpGet("{ChapterId}/preview")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<FileStreamHttpResult, NotFound<string>>> GetChapterPreview(string ChapterId)
    {
        if (await context.Chapters.FirstOrDefaultAsync(c => c.Key == ChapterId, HttpContext.RequestAborted) is not { } chapter)
            return TypedResults.NotFound("Chapter not found");

        if (chapter.IsBundled)
            return TypedResults.NotFound("bundled chapter thumbnails not yet supported");

        string cachePath = Path.Combine(settings.AppData, "previews", $"{chapter.Key}.jpg");

        if (System.IO.File.Exists(cachePath))
        {
            var cachedStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TypedResults.Stream(cachedStream, "image/jpeg");
        }

        string? archivePath = chapter.FullArchiveFilePath;
        if (string.IsNullOrEmpty(archivePath))
            return TypedResults.NotFound("Chapter archive path could not be resolved");

        bool generated = await chapterThumbnailService.GenerateThumbnailAsync(archivePath, cachePath, HttpContext.RequestAborted);
        if (!generated)
            return TypedResults.NotFound("Could not generate thumbnail: archive unreadable or contains no images");

        var thumbnailStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return TypedResults.Stream(thumbnailStream, "image/jpeg");
    }
}
