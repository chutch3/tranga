using API.Schema.ActionsContext;
using API.Schema.SeriesContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/[controller]")]
public class MaintenanceController(SeriesContext mangaContext, ActionsContext actionContext) : ControllerBase
{
    
    /// <summary>
    /// Removes all <see cref="Series"/> not marked for Download on any <see cref="SeriesSource"/>
    /// </summary>
    /// <response code="200"></response>
    /// <response code="500">Error during Database Operation</response>
    [HttpPost("CleanupNoDownloadManga")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok, InternalServerError<string>>> CleanupNoDownloadManga()
    {
        if (await mangaContext.Series
                .Include(m => m.SourceIds)
                .Where(m => !m.SourceIds.Any(id => id.UseForDownload))
                .ToListAsync(HttpContext.RequestAborted) is not { } remove)
            return TypedResults.InternalServerError("Database error");
        
        mangaContext.RemoveRange(remove);
        
        if(await mangaContext.Sync(HttpContext.RequestAborted, GetType(), System.Reflection.MethodBase.GetCurrentMethod()?.Name) is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);
        return TypedResults.Ok();
    }
    
    
    /// <summary>
    /// Removes all <see cref="ActionRecord"/>
    /// </summary>
    /// <response code="200">Number of deleted records</response>
    [HttpPost("CleanupActions")]
    [ProducesResponseType<int>(Status200OK, "text/plain")]
    public async Task<Ok<int>> CleanupActions()
    {
        var actions = await actionContext.Actions.ToListAsync(HttpContext.RequestAborted);
        int count = actions.Count;
        actionContext.Actions.RemoveRange(actions);
        await actionContext.Sync(HttpContext.RequestAborted, GetType(), "CleanupActions");
        return TypedResults.Ok(count);
    }

    /// <summary>
    /// Queues a <see cref="CleanupOrphanedFilesWorker"/> to remove files from the library that are not tracked in the database.
    /// </summary>
    /// <param name="workerQueue"></param>
    /// <param name="dryRun">If true, only log what would be deleted without actually deleting it.</param>
    /// <response code="202">Cleanup worker queued</response>
    [HttpPost("CleanupOrphanedFiles")]
    [ProducesResponseType(Status202Accepted)]
    public Ok CleanupOrphanedFiles([FromServices] IWorkerQueue workerQueue, bool dryRun = false)
    {
        workerQueue.AddWorker(new CleanupOrphanedFilesWorker(dryRun));
        return TypedResults.Ok();
    }

    /// <summary>
    /// Queues a <see cref="ResolveMissingVolumesWorker"/> to guess or resolve volume numbers for downloaded chapters.
    /// </summary>
    /// <param name="workerQueue"></param>
    /// <param name="settings"></param>
    /// <param name="mangaDexVolumeResolver"></param>
    /// <response code="202">Resolve worker queued</response>
    [HttpPost("ResolveMissingVolumes")]
    [ProducesResponseType(Status202Accepted)]
    public Ok ResolveMissingVolumes([FromServices] IWorkerQueue workerQueue, [FromServices] TrangaSettings settings, [FromServices] IBatchWorkerFactory<string> factory)
    {
        workerQueue.AddWorker(new ResolveMissingVolumesWorker(settings, factory));
        return TypedResults.Ok();
    }

    /// <summary>
    /// Queues a <see cref="SyncChapterFileNamesWorker"/> to rename chapter files where the stored filename no longer matches the current naming scheme.
    /// </summary>
    /// <param name="workerQueue"></param>
    /// <param name="settings"></param>
    /// <response code="200">Sync worker queued</response>
    [HttpPost("SyncChapterFileNames")]
    [ProducesResponseType(Status200OK)]
    public Ok SyncChapterFileNames([FromServices] IWorkerQueue workerQueue, [FromServices] TrangaSettings settings)
    {
        workerQueue.AddWorker(new SyncChapterFileNamesWorker(settings));
        return TypedResults.Ok();
    }

    /// <summary>
    /// Clears all chapter volume numbers and queues a <see cref="ResolveMissingVolumesWorker"/> to re-resolve them from scratch.
    /// </summary>
    /// <param name="workerQueue"></param>
    /// <param name="settings"></param>
    /// <param name="mangaDexVolumeResolver"></param>
    /// <response code="200">Volumes cleared and resolve worker queued</response>
    /// <response code="500">Error during database operation</response>
    [HttpPost("ResetAndResolveVolumes")]
    [ProducesResponseType(Status200OK)]
    [ProducesResponseType<string>(Status500InternalServerError, "text/plain")]
    public async Task<Results<Ok, InternalServerError<string>>> ResetAndResolveVolumes(
        [FromServices] IWorkerQueue workerQueue,
        [FromServices] TrangaSettings settings,
        [FromServices] IBatchWorkerFactory<string> factory)
    {
        var chapters = await mangaContext.Chapters.ToListAsync(HttpContext.RequestAborted);
        foreach (var chapter in chapters)
            chapter.VolumeNumber = null;

        if (await mangaContext.Sync(HttpContext.RequestAborted, GetType(), nameof(ResetAndResolveVolumes)) is { success: false } result)
            return TypedResults.InternalServerError(result.exceptionMessage);

        workerQueue.AddWorker(new ResolveMissingVolumesWorker(settings, factory));
        return TypedResults.Ok();
    }
}