using API.Controllers.DTOs;
using API.Schema.SeriesContext;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;

// ReSharper disable InconsistentNaming

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/Library")]
public class LibraryController(SeriesContext context) : ControllerBase
{
    /// <summary>
    /// Returns a dashboard of manga with unresolved chapters or missing files.
    /// Unresolved: Downloaded == true &amp;&amp; VolumeNumber == null.
    /// Missing: Downloaded == true &amp;&amp; FileName == null.
    /// Only manga with FileLibraryId != null are included.
    /// </summary>
    /// <response code="200">Dashboard result; manga list may be empty</response>
    [HttpGet("unresolved")]
    [ProducesResponseType<UnresolvedDashboardResult>(Status200OK, "application/json")]
    public async Task<Ok<UnresolvedDashboardResult>> GetUnresolved()
    {
        var entries = await context.Series
            .Where(m => m.LibraryId != null)
            .Select(m => new
            {
                m.Key,
                m.Name,
                UnresolvedChapterCount = context.Chapters.Count(c =>
                    c.ParentMangaId == m.Key && c.Downloaded && c.VolumeNumber == null),
                MissingFileCount = context.Chapters.Count(c =>
                    c.ParentMangaId == m.Key && c.Downloaded && c.FileName == null)
            })
            .Where(m => m.UnresolvedChapterCount > 0 || m.MissingFileCount > 0)
            .ToListAsync(HttpContext.RequestAborted);

        var manga = entries
            .Select(e => new UnresolvedMangaEntry(e.Key, e.Name, e.UnresolvedChapterCount, e.MissingFileCount))
            .ToList();

        return TypedResults.Ok(new UnresolvedDashboardResult(manga));
    }
}
