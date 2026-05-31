using Microsoft.EntityFrameworkCore;
using API.Controllers.DTOs;
using API.MangaConnectors;
using MangaConnectorImpl = API.MangaConnectors.SeriesSource;
using API.Schema.SeriesContext;
using API.Workers;
using API.Workers.MangaDownloadWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using static Microsoft.AspNetCore.Http.StatusCodes;
using Series = API.Schema.SeriesContext.Series;

// ReSharper disable InconsistentNaming

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class SearchController(
    SeriesContext context,
    IEnumerable<MangaConnectorImpl> connectors,
    IWorkerQueue workerQueue,
    Func<string, string, (Series, Schema.SeriesContext.SourceId<Series>)?>? connectorLookup = null)
    : ControllerBase
{
    private async Task<(Series, Schema.SeriesContext.SourceId<Series>)?> LookupFromConnector(string connectorName, string mangaIdOnSite)
    {
        if (connectorLookup is not null)
            return connectorLookup(connectorName, mangaIdOnSite);

        if (connectors.FirstOrDefault(c => c.Name.Equals(connectorName, StringComparison.InvariantCultureIgnoreCase)) is not { } connector)
            return null;
        return await connector.GetMangaFromId(mangaIdOnSite);
    }

    /// <summary>
    /// Initiate a search for a <see cref="Schema.SeriesContext.Series"/> on <see cref="SeriesSource"/> with searchTerm
    /// </summary>
    /// <param name="MangaConnectorName"><see cref="SeriesSource"/>.Name</param>
    /// <param name="Query">searchTerm</param>
    /// <response code="200"><see cref="MinimalSeries"/> exert of <see cref="Schema.SeriesContext.Series"/></response>
    /// <response code="404"><see cref="SeriesSource"/> with Name not found</response>
    /// <response code="412"><see cref="SeriesSource"/> with Name is disabled</response>
    [HttpGet("{MangaConnectorName}/{Query}")]
    [ProducesResponseType<List<MinimalSeries>>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType(Status406NotAcceptable)]
    public async Task<Results<Ok<List<MinimalSeries>>, NotFound<string>, StatusCodeHttpResult>> SearchManga(string MangaConnectorName, string Query)
    {
        if (connectors.FirstOrDefault(c => c.Name.Equals(MangaConnectorName, StringComparison.InvariantCultureIgnoreCase)) is not { } connector)
            return TypedResults.NotFound(nameof(MangaConnectorName));
        if (!connector.Enabled)
            return TypedResults.StatusCode(Status412PreconditionFailed);

        (Series manga, Schema.SeriesContext.SourceId<Series> id)[] mangas = await connector.SearchManga(Query);

        IEnumerable<MinimalSeries> result = mangas.Select(kv =>
        {
            Series m = kv.manga;
            Schema.SeriesContext.SourceId<Series> id = kv.id;
            IEnumerable<DTOs.SourceId<DTOs.Series>> ids =
            [
                new DTOs.SourceId<DTOs.Series>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload)
            ];
            return new MinimalSeries(
                m.Key, m.Name, m.Description, m.ReleaseStatus, ids,
                FileLibraryId: m.Library?.Key,
                Language: connector.SupportedLanguages.FirstOrDefault(),
                CoverUrl: m.CoverUrl);
        });

        return TypedResults.Ok(result.ToList());
    }

    /// <summary>
    /// Returns full <see cref="Schema.SeriesContext.Series"/> detail from a <see cref="SeriesSource"/> by its site ID, without saving to the database
    /// </summary>
    /// <param name="MangaConnectorName"><see cref="SeriesSource"/>.Name</param>
    /// <param name="ConnectorSeriesId">The manga's ID on the connector site</param>
    /// <response code="200">Full <see cref="DTOs.Series"/> detail</response>
    /// <response code="404">Series not found on connector</response>
    [HttpGet("{MangaConnectorName}/Series")]
    [ProducesResponseType<DTOs.Series>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<DTOs.Series>, NotFound<string>>> GetMangaFromConnector(string MangaConnectorName, [FromQuery] string ConnectorSeriesId)
    {
        if (await LookupFromConnector(MangaConnectorName, ConnectorSeriesId) is not ({ } manga, { } id))
            return TypedResults.NotFound(nameof(ConnectorSeriesId));
        IEnumerable<DTOs.SourceId<DTOs.Series>> ids =
        [
            new DTOs.SourceId<DTOs.Series>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload)
        ];
        IEnumerable<DTOs.Author> authors = manga.Authors.Select(a => new DTOs.Author(a.Key, a.AuthorName));
        IEnumerable<string> tags = manga.MangaTags.Select(t => t.Tag);
        IEnumerable<DTOs.Link> links = manga.Links.Select(l => new DTOs.Link(l.Key, l.LinkProvider, l.LinkUrl));
        IEnumerable<DTOs.AltTitle> altTitles = manga.AltTitles.Select(a => new DTOs.AltTitle(a.Language, a.Title));

        DTOs.Series result = new(
            manga.Key, manga.Name, manga.Description, manga.ReleaseStatus, ids,
            manga.IgnoreChaptersBefore, manga.Year, manga.OriginalLanguage,
            authors, tags, links, altTitles,
            FileLibraryId: manga.Library?.Key,
            CoverUrl: manga.CoverUrl);

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns <see cref="Schema.SeriesContext.Series"/> from the <see cref="SeriesSource"/> associated with <paramref name="url"/>
    /// </summary>
    /// <param name="url"></param>
    /// <response code="200"><see cref="MinimalSeries"/> exert of <see cref="Schema.SeriesContext.Series"/>.</response>
    /// <response code="404"><see cref="Series"/> not found</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpGet]
    [ProducesResponseType<MinimalSeries>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok<MinimalSeries>, NotFound<string>, InternalServerError<string>>> GetMangaFromUrl([FromQuery] string url)
    {
        url = url.Trim('"', '\'', ' '); // Trim extraneous values
        if (connectors.FirstOrDefault(c => c.Name.Equals("Global", StringComparison.InvariantCultureIgnoreCase)) is not { } connector)
            return TypedResults.InternalServerError("Could not find Global Connector.");

        if (await connector.GetMangaFromUrl(url) is not ({ } m, not null) manga)
            return TypedResults.NotFound("Could not retrieve Series");

        if (await context.UpsertManga(manga.Item1, manga.Item2, HttpContext.RequestAborted) is not { } added)
            return TypedResults.InternalServerError("Could not add Series to context");

        added.manga.IsTracked = true;
        await context.SaveChangesAsync(HttpContext.RequestAborted);

        // Kick off cover download worker
        workerQueue.AddWorker(new DownloadCoverFromSourceWorker(added.id, connectors));

        IEnumerable<DTOs.SourceId<DTOs.Series>> ids = added.manga.SourceIds.Select(id =>
            new DTOs.SourceId<DTOs.Series>(id.Key, id.MangaConnectorName, id.ObjId, id.IdOnConnectorSite, id.WebsiteUrl, id.UseForDownload));
        MinimalSeries result = new(
            added.manga.Key, added.manga.Name, added.manga.Description, added.manga.ReleaseStatus, ids,
            FileLibraryId: added.manga.Library?.Key,
            Language: connector.SupportedLanguages.FirstOrDefault(),
            CoverUrl: added.manga.CoverUrl);

        return TypedResults.Ok(result);
    }
}
