using API.Controllers.DTOs;
using API.MangaConnectors;
using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;
using API.Schema.SeriesContext;
using API.Workers;
using API.Workers.MangaDownloadWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Soenneker.Utils.String.NeedlemanWunsch;
using static Microsoft.AspNetCore.Http.StatusCodes;
using AltTitle = API.Controllers.DTOs.AltTitle;
using Author = API.Controllers.DTOs.Author;
using Chapter = API.Schema.SeriesContext.Chapter;
using Link = API.Controllers.DTOs.Link;
using Series = API.Controllers.DTOs.Series;
using MangaConnectorImpl = API.MangaConnectors.SeriesSource;

// ReSharper disable InconsistentNaming

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class SeriesController(SeriesContext context, ActionsContext actionsContext, TrangaSettings settings, IEnumerable<MangaConnectorImpl> connectors, IWorkerQueue workerQueue) : ControllerBase
{
    
    /// <summary>
    /// Returns all cached <see cref="DTOs.Series"/>
    /// </summary>
    /// <response code="200"><see cref="MinimalSeries"/> exert of <see cref="Schema.SeriesContext.Series"/>. Use <see cref="GetManga"/> for more information</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpGet]
    [ProducesResponseType<List<MinimalSeries>>(Status200OK, "application/json")]
    [ProducesResponseType(Status500InternalServerError)]
    public async Task<Results<Ok<List<MinimalSeries>>, InternalServerError>> GetAllManga ()
    {
        if (await context.GetTrackedMangas()
                .OrderBy(m => m.Name)
                .ToArrayAsync(HttpContext.RequestAborted) is not
            { } result)
            return TypedResults.InternalServerError();
        
        return TypedResults.Ok(result.Select(m =>
        {
            IEnumerable<DTOs.SourceId<Series>> ids = m.SourceIds.Select(id => new DTOs.SourceId<Series>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
            return new MinimalSeries(m.Key, m.Name, m.Description, m.ReleaseStatus, ids, m.LibraryId, m.OriginalLanguage, m.CoverUrl);
        }).ToList());
    }
    
    /// <summary>
    /// Returns all <see cref="Schema.SeriesContext.Series"/> that are being downloaded from at least one <see cref="API.MangaConnectors.SeriesSource"/>
    /// </summary>
    /// <response code="200"><see cref="MinimalSeries"/> exert of <see cref="Schema.SeriesContext.Series"/>. Use <see cref="GetManga"/> for more information</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpGet("Downloading")]
    [ProducesResponseType<MinimalSeries[]>(Status200OK, "application/json")]
    [ProducesResponseType(Status500InternalServerError)]
    public async Task<Results<Ok<List<MinimalSeries>>, InternalServerError>> GetMangaDownloading()
    {
        if (await context.Series
                .Include(m => m.SourceIds)
                .Where(m => m.SourceIds.Any(id => id.UseForDownload))
                .OrderBy(m => m.Name)
                .ToArrayAsync(HttpContext.RequestAborted) is not { } result)
            return TypedResults.InternalServerError();

        return TypedResults.Ok(result.Select(m =>
        {
            IEnumerable<DTOs.SourceId<Series>> ids = m.SourceIds.Select(id => new DTOs.SourceId<Series>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
            return new MinimalSeries(m.Key, m.Name, m.Description, m.ReleaseStatus, ids, m.LibraryId, m.OriginalLanguage, m.CoverUrl);
        }).ToList());
    }

    /// <summary>
    /// Return <see cref="Schema.SeriesContext.Series"/> with <paramref name="MangaId"/>
    /// </summary>
    /// <param name="MangaId"><see cref="Schema.SeriesContext.Series"/>.Key</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Series"/> with <paramref name="MangaId"/> not found</response>
    [HttpGet("{MangaId}")]
    [ProducesResponseType<Series>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<Series>, NotFound<string>>> GetManga (string MangaId)
    {
        if (await context.MangaWithMetadata().Include(m => m.SourceIds).FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));
        
        IEnumerable<DTOs.SourceId<Series>> ids = manga.SourceIds.Select(id => new DTOs.SourceId<Series>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
        IEnumerable<Author> authors = manga.Authors.Select(a => new Author(a.Key, a.AuthorName));
        IEnumerable<string> tags = manga.MangaTags.Select(t => t.Tag);
        IEnumerable<Link> links = manga.Links.Select(l => new Link(l.Key, l.LinkProvider, l.LinkUrl));
        IEnumerable<AltTitle> altTitles = manga.AltTitles.Select(a => new AltTitle(a.Language, a.Title));
        Series result = new (manga.Key, manga.Name, manga.Description, manga.ReleaseStatus, ids, manga.IgnoreChaptersBefore, manga.Year, manga.OriginalLanguage, authors, tags, links, altTitles, manga.LibraryId, manga.CoverUrl);
        
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Delete <see cref="Series"/> with <paramref name="MangaId"/>
    /// </summary>
    /// <param name="MangaId"><see cref="Series"/>.Key</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Series"/> with <paramref name="MangaId"/> not found</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpDelete("{MangaId}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok, NotFound<string>, InternalServerError<string>>> DeleteManga (string MangaId)
    {
        if(await context.Series.FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));
        context.Remove(manga);
        
        if(await context.Sync(HttpContext.RequestAborted, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);
        return TypedResults.Ok();
    }


    /// <summary>
    /// Merge two <see cref="Series"/> into one. THIS IS NOT REVERSIBLE!
    /// </summary>
    /// <param name="MangaIdFrom"><see cref="Series"/>.Key of <see cref="Series"/> merging data from (getting deleted)</param>
    /// <param name="MangaIdInto"><see cref="Series"/>.Key of <see cref="Series"/> merging data into</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Series"/> with <paramref name="MangaIdFrom"/> or <paramref name="MangaIdInto"/> not found</response>
    [HttpPost("{MangaIdFrom}/MergeInto/{MangaIdInto}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok, NotFound<string>>> MergeIntoManga (string MangaIdFrom, string MangaIdInto)
    {
        if (await context.MangaIncludeAll().FirstOrDefaultAsync(m => m.Key == MangaIdFrom, HttpContext.RequestAborted) is not { } from)
            return TypedResults.NotFound(nameof(MangaIdFrom));
        if (await context.MangaIncludeAll().FirstOrDefaultAsync(m => m.Key == MangaIdInto, HttpContext.RequestAborted) is not { } into)
            return TypedResults.NotFound(nameof(MangaIdInto));
        
        BaseWorker[] newJobs = into.MergeFrom(from, context);
        workerQueue.AddWorkers(newJobs);
        
        return TypedResults.Ok();
    }

    /// <summary>
    /// Returns Cover of <see cref="Series"/> with <paramref name="MangaId"/>
    /// </summary>
    /// <param name="MangaId"><see cref="Series"/>.Key</param>
    /// <param name="CoverSize">Size of the cover returned
    /// <br /> - <see cref="CoverSize.Small"/> <see cref="Constants.ImageSmSize"/>
    /// <br /> - <see cref="CoverSize.Medium"/> <see cref="Constants.ImageMdSize"/>
    /// <br /> - <see cref="CoverSize.Large"/> <see cref="Constants.ImageLgSize"/>
    /// </param>
    /// <response code="200">JPEG Image</response>
    /// <response code="204">Cover not loaded</response>
    /// <response code="404"><see cref="Series"/> with <paramref name="MangaId"/> not found</response>
    /// <response code="503">Retry later, downloading cover</response>
    [HttpGet("{MangaId}/Cover/{CoverSize?}")]
    [ProducesResponseType<FileContentResult>(Status200OK,"image/jpeg")]
    [ProducesResponseType(Status204NoContent)]
    [ProducesResponseType(Status400BadRequest)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType(Status503ServiceUnavailable)]
    public async Task<Results<FileContentHttpResult, NoContent, BadRequest, NotFound<string>, StatusCodeHttpResult>> GetCover (string MangaId, CoverSize? CoverSize = null)
    {
        if (await context.Series.FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));

        string cache = CoverSize switch
        {
            SeriesController.CoverSize.Small => settings.CoverImageCacheSmall,
            SeriesController.CoverSize.Medium => settings.CoverImageCacheMedium,
            SeriesController.CoverSize.Large => settings.CoverImageCacheLarge,
            _ => settings.CoverImageCacheOriginal
        };

        if (await manga.GetCoverImage(cache, HttpContext.RequestAborted) is not { } data)
        {
            return TypedResults.NotFound("Image not in cache");
        }
        
        DateTime lastModified = data.fileInfo.LastWriteTime;
        EntityTagHeaderValue entityTagHeaderValue = EntityTagHeaderValue.Parse($"\"{lastModified.Ticks}\"");
        if(HttpContext.Request.Headers.ETag.Equals(entityTagHeaderValue.Tag.Value))
            return TypedResults.StatusCode(Status304NotModified);
        HttpContext.Response.Headers.CacheControl = "public";
        return TypedResults.Bytes(data.stream.ToArray(), "image/jpeg", lastModified: new DateTimeOffset(lastModified), entityTag: entityTagHeaderValue);
    }
    public enum CoverSize { Original, Large, Medium, Small }

    /// <summary>
    /// Move <see cref="Series"/> to different <see cref="DTOs.FileLibrary"/>
    /// </summary>
    /// <param name="MangaId"><see cref="Series"/>.Key</param>
    /// <param name="LibraryId"><see cref="DTOs.FileLibrary"/>.Key</param>
    /// <param name="connectorName">(Optional) Name of the connector to fetch manga from if not in DB</param>
    /// <param name="connectorSeriesId">(Optional) ID of the manga on the connector site</param>
    /// <response code="202">Folder is going to be moved</response>
    /// <response code="404"><paramref name="MangaId"/> or <paramref name="LibraryId"/> not found</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpPost("{MangaId}/ChangeLibrary/{LibraryId}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError,  "text/plain")]
    public async Task<Results<Ok, NotFound<string>, InternalServerError<string>>> ChangeLibrary(string MangaId, string LibraryId, [FromQuery] string? connectorName = null, [FromQuery] string? connectorSeriesId = null)
    {
        if (await context.FileLibraries.FirstOrDefaultAsync(l => l.Key == LibraryId, HttpContext.RequestAborted) is not { } library)
            return TypedResults.NotFound(nameof(LibraryId));

        var manga = await context.Series
            .Include(m => m.Library)
            .Include(m => m.Chapters)
            .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted);

        if (manga is null)
        {
            if (string.IsNullOrWhiteSpace(connectorName) || string.IsNullOrWhiteSpace(connectorSeriesId))
                return TypedResults.NotFound(nameof(MangaId));

            if (connectors.FirstOrDefault(c => c.Name.Equals(connectorName, StringComparison.InvariantCultureIgnoreCase)) is not { } connector)
                return TypedResults.NotFound(nameof(connectorName));

            if (await connector.GetMangaFromId(connectorSeriesId) is not ({ } m, { } id))
                return TypedResults.NotFound(nameof(connectorSeriesId));

            if (await context.UpsertManga(m, id, HttpContext.RequestAborted) is not { } added)
                return TypedResults.InternalServerError("Could not add Series to context");
            
            manga = added.manga;
        }

        manga.IsTracked = true;
        
        if(manga.LibraryId == library.Key)
        {
             await context.Sync(HttpContext.RequestAborted, GetType(), "Track Series");
             return TypedResults.Ok();
        }

        Dictionary<Chapter, string?> oldPaths = manga.Chapters.Where(ch => ch.Downloaded).ToDictionary(ch => ch, ch => ch.FullArchiveFilePath);
        manga.Library = library;
        Dictionary<Chapter, string?> newPaths = oldPaths.ToDictionary(kv => kv.Key, kv => kv.Key.FullArchiveFilePath);
        IEnumerable<MoveFileOrFolderWorker> workers = oldPaths.Select(kv => new MoveFileOrFolderWorker(newPaths[kv.Key]!, kv.Value!));
        workerQueue.AddWorkers(workers);
        
        if(await context.Sync(HttpContext.RequestAborted, GetType(), "Move Series") is { success: false } mangaContextResult)
            return TypedResults.InternalServerError(mangaContextResult.exceptionMessage);
        
        actionsContext.Actions.Add(new LibraryMovedActionRecord(manga, library));
        if(await actionsContext.Sync(HttpContext.RequestAborted, GetType(), "Move Series") is { success: false } actionsContextResult)
            return TypedResults.InternalServerError(actionsContextResult.exceptionMessage);
        
        return TypedResults.Ok();
    }

    /// <summary>
    /// (Un-)Marks <see cref="Series"/> as requested for Download from <see cref="API.MangaConnectors.SeriesSource"/>
    /// </summary>
    /// <param name="MangaId"><see cref="Series"/> with <paramref name="MangaId"/></param>
    /// <param name="MangaConnectorName"><see cref="API.MangaConnectors.SeriesSource"/> with <paramref name="MangaConnectorName"/></param>
    /// <param name="IsRequested">true to mark as requested, false to mark as not-requested</param>
    /// <response code="200"></response>
    /// <response code="404"><paramref name="MangaId"/> or <paramref name="MangaConnectorName"/> not found</response>
    /// <response code="412"><see cref="Series"/> was not linked to <see cref="API.MangaConnectors.SeriesSource"/>, so nothing changed</response>
    /// <response code="428"><see cref="Series"/> is not linked to <see cref="API.MangaConnectors.SeriesSource"/> yet. Search for <see cref="Series"/> on <see cref="API.MangaConnectors.SeriesSource"/> first (to create a <see cref="DTOs.SourceId{T}"/>).</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpPatch("{MangaId}/DownloadFrom/{MangaConnectorName}/{IsRequested}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound,  "text/plain")]
    [ProducesResponseType<string>(Status412PreconditionFailed,  "text/plain")]
    [ProducesResponseType<string>(Status428PreconditionRequired,  "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError,  "text/plain")]
    public async Task<Results<Ok, NotFound<string>, StatusCodeHttpResult, InternalServerError<string>>> MarkAsRequested(string MangaId, string MangaConnectorName, bool IsRequested)
    {
        if (await context.Series
                .Include(m => m.Chapters)
                .ThenInclude(c => c.SourceIds.Where(chID => chID.MangaConnectorName == MangaConnectorName))
                .Include(m => m.SourceIds.Where(mId => mId.MangaConnectorName == MangaConnectorName))
                .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));
        if(!connectors.Any(c => c.Name.Equals(MangaConnectorName, StringComparison.InvariantCultureIgnoreCase)))
            return TypedResults.NotFound(nameof(MangaConnectorName));

        if (manga.SourceIds.FirstOrDefault(mId => mId.MangaConnectorName == MangaConnectorName) is not { } mcId)
        {
            if(IsRequested)
                return TypedResults.StatusCode(Status428PreconditionRequired);
            else
                return TypedResults.StatusCode(Status412PreconditionFailed);
        }
        else
        {
            mcId.UseForDownload = IsRequested;
        }

        if (manga.Chapters.SelectMany(ch =>
                ch.SourceIds.Where(chID => chID.MangaConnectorName == MangaConnectorName)) is { } chIds)
        {
            foreach (Schema.SeriesContext.SourceId<Chapter> chId in chIds)
            {
                chId.UseForDownload = IsRequested;
            }
        }

        if(await context.Sync(HttpContext.RequestAborted, GetType(), "Update download from SeriesSource.") is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);

        DownloadCoverFromSourceWorker downloadCover = new(mcId, connectors);
        RetrieveChaptersFromSourceWorker retrieveChapters = new(mcId, settings.DownloadLanguage, connectors);
        workerQueue.AddWorkers([downloadCover, retrieveChapters]);
        
        return TypedResults.Ok();
    }
    
    /// <summary>
    /// Initiate a search for <see cref="API.Schema.SeriesContext.Series"/> on a different <see cref="API.MangaConnectors.SeriesSource"/>
    /// </summary>
    /// <param name="MangaId"><see cref="API.Schema.SeriesContext.Series"/> with <paramref name="MangaId"/></param>
    /// <param name="MangaConnectorName"><see cref="API.MangaConnectors.SeriesSource"/>.Name</param>
    /// <response code="200"><see cref="MinimalSeries"/> exert of <see cref="Schema.SeriesContext.Series"/></response>
    /// <response code="404"><see cref="API.MangaConnectors.SeriesSource"/> with Name not found</response>
    /// <response code="412"><see cref="API.MangaConnectors.SeriesSource"/> with Name is disabled</response>
    [HttpGet("{MangaId}/OnMangaConnector/{MangaConnectorName}")]
    [ProducesResponseType<List<MinimalSeries>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType(Status406NotAcceptable)]
    public async Task<Results<Ok<List<MinimalSeries>>, NotFound<string>, StatusCodeHttpResult>> SearchOnDifferentConnector (string MangaId, string MangaConnectorName)
    {
        if (await context.Series.FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));

        return await new SearchController(context, connectors, workerQueue).SearchManga(MangaConnectorName, manga.Name);
    }
    
    /// <summary>
    /// Returns all <see cref="Series"/> which where Authored by <see cref="Author"/> with <paramref name="AuthorId"/>
    /// </summary>
    /// <param name="AuthorId"><see cref="Author"/>.Key</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Author"/> with <paramref name="AuthorId"/></response>
    /// /// <response code="500">Error during Database Operation</response>
    [HttpGet("WithAuthorId/{AuthorId}")]
    [ProducesResponseType<List<Series>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<List<Series>>, NotFound<string>, InternalServerError>> GetMangaWithAuthorIds (string AuthorId)
    {
        if (await context.Authors.FirstOrDefaultAsync(a => a.Key == AuthorId, HttpContext.RequestAborted) is not { } _)
            return TypedResults.NotFound(nameof(AuthorId));

        if (await context.MangaWithMetadata().Include(m => m.SourceIds)
                .Where(m => m.Authors.Any(a => a.Key == AuthorId))
                .OrderBy(m => m.Name)
                .ToListAsync(HttpContext.RequestAborted) is not { } result)
            return TypedResults.InternalServerError();

        return TypedResults.Ok(result.Select(m =>
        {
            IEnumerable<DTOs.SourceId<Series>> ids = m.SourceIds.Select(id => new DTOs.SourceId<Series>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
            IEnumerable<Author> authors = m.Authors.Select(a => new Author(a.Key, a.AuthorName));
            IEnumerable<string> tags = m.MangaTags.Select(t => t.Tag);
            IEnumerable<Link> links = m.Links.Select(l => new Link(l.Key, l.LinkProvider, l.LinkUrl));
            IEnumerable<AltTitle> altTitles = m.AltTitles.Select(a => new AltTitle(a.Language, a.Title));
            return new Series(m.Key, m.Name, m.Description, m.ReleaseStatus, ids, m.IgnoreChaptersBefore, m.Year, m.OriginalLanguage, authors, tags, links, altTitles, m.LibraryId);
        }).ToList());
    }
    
    /// <summary>
    /// Returns all <see cref="Series"/> with <see cref="Tag"/>
    /// </summary>
    /// <param name="Tag"><see cref="Tag"/>.Tag</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Tag"/> not found</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpGet("WithTag/{Tag}")]
    [ProducesResponseType<Series[]>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType(Status500InternalServerError)]
    public async Task<Results<Ok<List<MinimalSeries>>, NotFound<string>, InternalServerError>> GetMangasWithTag (string Tag)
    {
        if (await context.Series
                .Include(m => m.SourceIds)
                .Include(m => m.MangaTags)
                .Where(m => m.MangaTags.Any(t => t.Tag == Tag))
                .OrderBy(m => m.Name)
                .ToListAsync(HttpContext.RequestAborted) is not { } result)
            return TypedResults.InternalServerError();
        
        return TypedResults.Ok(result.Select(m =>
        {
            IEnumerable<DTOs.SourceId<Series>> ids = m.SourceIds.Select(id => new DTOs.SourceId<Series>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
            return new MinimalSeries(m.Key, m.Name, m.Description, m.ReleaseStatus, ids, m.LibraryId, m.OriginalLanguage, m.CoverUrl);
        }).ToList());
    }

    /// <summary>
    /// Returns <see cref="Schema.SeriesContext.Series"/> with names similar to <see cref="Schema.SeriesContext.Series"/> (identified by <paramref name="MangaId"/>)
    /// </summary>
    /// <param name="MangaId">Key of <see cref="Schema.SeriesContext.Series"/></param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="Schema.SeriesContext.Series"/> with <paramref name="MangaId"/> not found</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpGet("WithSimilarName/{MangaId}")]
    [ProducesResponseType<List<string>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType(Status500InternalServerError)]
    public async Task<Results<Ok<List<string>>, NotFound<string>, InternalServerError>> GetSimilarManga (string MangaId)
    {
        if (await context.Series.FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted) is not { } manga)
            return TypedResults.NotFound(nameof(MangaId));
        
        string name = manga.Name;

        if (await context.Series.Where(m => m.Key != MangaId)
                .ToDictionaryAsync(m => m.Key, m => m.Name, HttpContext.RequestAborted) is not { } mangaNames)
            return TypedResults.InternalServerError();

        List<string> similarIds = mangaNames
            .Where(kv => NeedlemanWunschStringUtil.CalculateSimilarityPercentage(name, kv.Value) > 0.8)
            .Select(kv => kv.Key)
            .ToList();
        
        return TypedResults.Ok(similarIds);
    }

    /// <summary>
    /// Returns the <see cref="DTOs.SourceId{T}"/> with <see cref="DTOs.SourceId{T}"/>.Key
    /// </summary>
    /// <param name="MangaConnectorIdId">Key of <see cref="DTOs.SourceId{T}"/></param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="DTOs.SourceId{T}"/> with <paramref name="MangaConnectorIdId"/> not found</response>
    [HttpGet("ConnectorId/{MangaConnectorIdId}")]
    [ProducesResponseType<DTOs.SourceId<Series>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<DTOs.SourceId<Series>>, NotFound<string>>> GetMangaMangaConnectorId (string MangaConnectorIdId)
    {
        if (await context.MangaConnectorToManga.FirstOrDefaultAsync(c => c.Key == MangaConnectorIdId, HttpContext.RequestAborted) is not { } mcIdManga)
            return TypedResults.NotFound(nameof(MangaConnectorIdId));

        DTOs.SourceId<Series> result = new (mcIdManga.Key, mcIdManga.MangaConnectorName, mcIdManga.ObjId, mcIdManga.IdOnConnectorSite, mcIdManga.WebsiteUrl, mcIdManga.UseForDownload);
        
        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Force re-check failed/undownloaded <see cref="Chapter"/> for <see cref="Series"/>
    /// </summary>
    /// <param name="mangaId">(optional)<see cref="Series"/>.Key</param>
    /// <response code="200">Affected Records</response>
    [HttpPost("ForceRecheck")]
    [HttpPost("ForceRecheck/{mangaId?}")]
    [ProducesResponseType<int>(Status200OK, "text/plain")]
    public async Task<Ok<int>> ForceRecheckMangaChapters(string? mangaId = null)
    {
        IQueryable<Schema.SeriesContext.SourceId<Chapter>> queryable = context.MangaConnectorToChapter.Where(chId  => chId.Obj!.Downloaded);
        if(mangaId is not null)
            queryable = queryable.Where(chId => chId.Obj!.ParentMangaId == mangaId);
        
        int rowsAffected = await queryable.ExecuteDeleteAsync(HttpContext.RequestAborted);

        return TypedResults.Ok(rowsAffected);
    }

    /// <summary>
    /// Force re-check a specific <see cref="Chapter"/> by deleting its record.
    /// </summary>
    /// <param name="chapterId"><see cref="Chapter"/>.Key</param>
    /// <response code="200">Affected records</response>
    [HttpPost("ForceRecheck/Chapter/{chapterId}")]
    [ProducesResponseType<int>(Status200OK, "text/plain")]
    public async Task<Ok<int>> ForceRecheckChapter(string chapterId)
    {
        IQueryable<Schema.SeriesContext.SourceId<Chapter>> queryable = context.MangaConnectorToChapter.Where(chId  => chId.ObjId == chapterId);
        
        int rowsAffected = await queryable.ExecuteDeleteAsync(HttpContext.RequestAborted);

        return TypedResults.Ok(rowsAffected);
    }
}