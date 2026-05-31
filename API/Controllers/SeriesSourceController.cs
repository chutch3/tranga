using API.Controllers.DTOs;
using API.MangaConnectors;
using MangaConnectorImpl = API.MangaConnectors.SeriesSource;
using API.Schema.SeriesContext;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using static Microsoft.AspNetCore.Http.StatusCodes;
// ReSharper disable InconsistentNaming

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class SeriesSourceController(SeriesContext context, IEnumerable<MangaConnectorImpl> connectors, TrangaSettings settings) : ControllerBase
{
    /// <summary>
    /// Get all <see cref="API.MangaConnectors.SeriesSource"/> (Scanlation-Sites)
    /// </summary>
    /// <response code="200">Names of <see cref="API.MangaConnectors.SeriesSource"/> (Scanlation-Sites)</response>
    [HttpGet]
    [ProducesResponseType<List<DTOs.SeriesSource>>(Status200OK, "application/json")]
    public Ok<List<DTOs.SeriesSource>> GetConnectors()
    {
        return TypedResults.Ok(connectors
            .Select(c => new DTOs.SeriesSource(c.Name, c.Enabled, c.IconUrl, c.SupportedLanguages))
            .ToList());
    }

    /// <summary>
    /// Returns the <see cref="API.MangaConnectors.SeriesSource"/> (Scanlation-Sites) with the requested Name
    /// </summary>
    /// <param name="MangaConnectorName"><see cref="API.MangaConnectors.SeriesSource"/>.Name</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="DTOs.SeriesSource"/> (Scanlation-Sites) with Name not found.</response>
    [HttpGet("{MangaConnectorName}")]
    [ProducesResponseType<DTOs.SeriesSource>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Results<Ok<DTOs.SeriesSource>, NotFound<string>> GetConnector(string MangaConnectorName)
    {
        if (connectors.FirstOrDefault(c => c.Name.Equals(MangaConnectorName, StringComparison.InvariantCultureIgnoreCase)) is not { } connector)
            return TypedResults.NotFound(nameof(MangaConnectorName));
        
        return TypedResults.Ok(new DTOs.SeriesSource(connector.Name, connector.Enabled, connector.IconUrl, connector.SupportedLanguages));
    }
    
    /// <summary>
    /// Get all <see cref="API.MangaConnectors.SeriesSource"/> (Scanlation-Sites) with <paramref name="Enabled"/>-Status
    /// </summary>
    /// <response code="200"></response>
    [HttpGet("Enabled/{Enabled}")]
    [ProducesResponseType<List<DTOs.SeriesSource>>(Status200OK, "application/json")]
    public Ok<List<DTOs.SeriesSource>> GetEnabledConnectors(bool Enabled)
    {
        return TypedResults.Ok(connectors
            .Where(c => c.Enabled == Enabled)
            .Select(c => new DTOs.SeriesSource(c.Name, c.Enabled, c.IconUrl, c.SupportedLanguages))
            .ToList());
    }

    /// <summary>
    /// Enabled or disables <see cref="API.MangaConnectors.SeriesSource"/> (Scanlation-Sites) with Name
    /// </summary>
    /// <param name="MangaConnectorName"><see cref="API.MangaConnectors.SeriesSource"/>.Name</param>
    /// <param name="Enabled">Set true to enable, false to disable</param>
    /// <response code="200"></response>
    /// <response code="404"><see cref="API.MangaConnectors.SeriesSource"/> (Scanlation-Sites) with Name not found.</response>
    /// <response code="500">Error during Database Operation</response>
    [HttpPatch("{MangaConnectorName}/SetEnabled/{Enabled}")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public Results<Ok, NotFound<string>> SetEnabled(string MangaConnectorName, bool Enabled)
    {
        if (connectors.FirstOrDefault(c => c.Name.Equals(MangaConnectorName, StringComparison.InvariantCultureIgnoreCase)) is not { } connector)
            return TypedResults.NotFound(nameof(MangaConnectorName));
        
        connector.Enabled = Enabled;
        settings.SetConnectorEnabled(connector.Name, Enabled);
        
        return TypedResults.Ok();
    }
}
