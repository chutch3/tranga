using API.Controllers.DTOs;
using API.Controllers.Requests;
using API.Schema.SeriesContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
using SchemaManga = API.Schema.SeriesContext.Series;

// ReSharper disable InconsistentNaming

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/Series/{MangaId}")]
public class VolumeController(SeriesContext context, TrangaSettings settings, IWorkerQueue workerQueue)
    : ControllerBase
{
    /// <summary>
    /// Returns volumes and chapters for a manga, grouped by volume number.
    /// </summary>
    /// <param name="MangaId"><see cref="Series"/>.Key</param>
    /// <response code="200">Volume listing with chapter file status</response>
    /// <response code="404">Series not found</response>
    [HttpGet("volumes")]
    [ProducesResponseType<VolumeListResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<VolumeListResult>, NotFound<string>>> GetVolumes(string MangaId)
    {
        var manga = await context.Series
            .Include(m => m.Library)
            .Include(m => m.Chapters)
            .ThenInclude(c => c.SourceIds)
            .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted);

        if (manga is null)
            return TypedResults.NotFound(nameof(MangaId));

        // Load VolumeMetadata rows for this manga (keyed by VolumeNumber)
        var volumeMetaByNumber = await context.VolumeMetadata
            .Where(v => v.MangaId == MangaId)
            .ToDictionaryAsync(v => v.VolumeNumber, HttpContext.RequestAborted);

        string namingScheme = settings.ChapterNamingScheme;

        // Attach ParentManga to each chapter (needed for GetArchiveFileName, FullArchiveFilePath)
        foreach (var chapter in manga.Chapters)
        {
            chapter.ParentManga = manga;
        }

        // Count chapters whose stored FileName differs from what the naming scheme would produce.
        // This is a pure string comparison — no File.Exists calls here.
        int filesNeedReorganizing = manga.Chapters
            .Where(c => !c.IsBundled && c.FileName != null)
            .Count(c => c.FileName != c.GetArchiveFileName(namingScheme));

        // Group chapters by VolumeNumber
        var assignedChapters = manga.Chapters
            .Where(c => c.VolumeNumber.HasValue)
            .GroupBy(c => c.VolumeNumber!.Value)
            .OrderBy(g => g.Key)
            .ToList();

        var volumes = assignedChapters.Select(group =>
        {
            int volNum = group.Key;
            volumeMetaByNumber.TryGetValue(volNum, out var meta);

            bool isBundled = meta?.ArchiveFileName != null;
            string? archiveFileName = meta?.ArchiveFileName;
            string? title = meta?.Title;

            var chapters = group
                .OrderBy(c => c.ChapterNumber, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ChapterFileEntry(
                    ChapterId: c.Key,
                    ChapterNumber: c.ChapterNumber,
                    FileName: c.FileName,
                    FileExistsOnDisk: c.FullArchiveFilePath != null && System.IO.File.Exists(c.FullArchiveFilePath),
                    IsBundled: c.IsBundled,
                    MetadataConfidence: c.MetadataConfidence?.ToString()
                ))
                .ToList();

            return new VolumeEntry(
                VolumeNumber: volNum,
                Title: title,
                IsBundled: isBundled,
                ArchiveFileName: archiveFileName,
                ChapterCount: chapters.Count,
                Chapters: chapters
            );
        }).ToList();

        var unassigned = manga.Chapters
            .Where(c => !c.VolumeNumber.HasValue)
            .OrderBy(c => c.ChapterNumber, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ChapterFileEntry(
                ChapterId: c.Key,
                ChapterNumber: c.ChapterNumber,
                FileName: c.FileName,
                FileExistsOnDisk: c.FullArchiveFilePath != null && System.IO.File.Exists(c.FullArchiveFilePath),
                IsBundled: c.IsBundled,
                MetadataConfidence: null
            ))
            .ToList();

        var result = new VolumeListResult(
            FilesNeedReorganizing: filesNeedReorganizing,
            Layout: manga.LibraryLayout,
            Volumes: volumes,
            Unassigned: unassigned
        );

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns a dry-run preview of all file moves needed to bring files in line with current metadata.
    /// </summary>
    /// <param name="MangaId"><see cref="Series"/>.Key</param>
    /// <response code="200">Preview with moves, directories to create, and empty directories to delete</response>
    /// <response code="404">Series not found</response>
    [HttpGet("reorganize/preview")]
    [ProducesResponseType<ReorganizePreviewResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<ReorganizePreviewResult>, NotFound<string>>> GetReorganizePreview(string MangaId)
    {
        var manga = await context.Series
            .Include(m => m.Library)
            .Include(m => m.Chapters)
            .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted);

        if (manga is null)
            return TypedResults.NotFound(nameof(MangaId));

        foreach (var chapter in manga.Chapters)
            chapter.ParentManga = manga;

        var preview = ComputeReorganizePreview(manga);
        return TypedResults.Ok(preview);
    }

    /// <summary>
    /// Queues RenameChapterFileWorker instances for each file that needs moving.
    /// </summary>
    /// <param name="MangaId"><see cref="Series"/>.Key</param>
    /// <response code="202">Workers queued; returns first worker key as jobId</response>
    /// <response code="200">Nothing to reorganize</response>
    /// <response code="404">Series not found</response>
    [HttpPost("reorganize")]
    [ProducesResponseType<ReorganizeJobResult>(Status202Accepted, "application/json")]
    [ProducesResponseType<ReorganizeJobResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Accepted<ReorganizeJobResult>, Ok<ReorganizeJobResult>, NotFound<string>>> PostReorganize(string MangaId)
    {
        var manga = await context.Series
            .Include(m => m.Library)
            .Include(m => m.Chapters)
            .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted);

        if (manga is null)
            return TypedResults.NotFound(nameof(MangaId));

        foreach (var chapter in manga.Chapters)
            chapter.ParentManga = manga;

        var preview = ComputeReorganizePreview(manga);

        if (preview.Moves.Count == 0)
            return TypedResults.Ok(new ReorganizeJobResult(string.Empty));

        // Build workers: one RenameChapterFileWorker per move
        var chaptersByPath = manga.Chapters
            .Where(c => !c.IsBundled && c.FullArchiveFilePath != null)
            .ToDictionary(c => c.FullArchiveFilePath!, c => c);

        var workers = preview.Moves
            .Where(m => chaptersByPath.ContainsKey(m.From))
            .Select(m =>
            {
                var chapter = chaptersByPath[m.From];
                string newFileName = Path.GetRelativePath(manga.FullDirectoryPath, m.To);
                return new RenameChapterFileWorker(chapter.Key, newFileName, settings);
            })
            .Cast<BaseWorker>()
            .ToList();

        if (workers.Count == 0)
            return TypedResults.Ok(new ReorganizeJobResult(string.Empty));

        workerQueue.AddWorkers(workers);

        // Use the key of the first queued worker as the job ID
        string jobId = workers[0].Key;
        return TypedResults.Accepted<ReorganizeJobResult>((string?)null, new ReorganizeJobResult(jobId));
    }

    /// <summary>
    /// Stores the layout preference and returns a reorganize preview using the new layout.
    /// Does NOT execute any file moves.
    /// </summary>
    /// <param name="MangaId"><see cref="SchemaManga"/>.Key</param>
    /// <param name="request">New layout preference</param>
    /// <response code="200">Layout stored; response includes reorganize preview with new layout paths</response>
    /// <response code="404">Series not found</response>
    [HttpPut("libraryLayout")]
    [ProducesResponseType<LibraryLayoutResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<LibraryLayoutResult>, NotFound<string>>> PutLibraryLayout(string MangaId, [FromBody] PutLibraryLayoutRecord request)
    {
        var manga = await context.Series
            .Include(m => m.Library)
            .Include(m => m.Chapters)
            .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted);

        if (manga is null)
            return TypedResults.NotFound(nameof(MangaId));

        manga.LibraryLayout = request.Layout;
        await context.Sync(HttpContext.RequestAborted, GetType(), nameof(PutLibraryLayout));

        foreach (var chapter in manga.Chapters)
            chapter.ParentManga = manga;

        var preview = ComputeReorganizePreview(manga);
        var result = new LibraryLayoutResult(
            Layout: manga.LibraryLayout.ToString(),
            ReorganizePreview: preview
        );

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Bulk-assigns chapter numbers to volume numbers for a manga.
    /// Chapters not found are listed in the response; the request is not failed.
    /// Also marks MetadataSource as Manual/Confirmed on the manga.
    /// </summary>
    /// <param name="MangaId"><see cref="SchemaManga"/>.Key</param>
    /// <param name="request">Map of ChapterNumber to VolumeNumber</param>
    /// <response code="200">Assignment applied; returns count of applied and list of not-found chapter numbers</response>
    /// <response code="404">Series not found</response>
    [HttpPost("volumes/assignments")]
    [ProducesResponseType<BulkAssignmentResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<BulkAssignmentResult>, NotFound<string>>> PostBulkAssignment(
        string MangaId, [FromBody] BulkAssignmentRecord request)
    {
        var manga = await context.Series
            .Include(m => m.Chapters)
            .Include(m => m.MetadataSource)
            .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted);

        if (manga is null)
            return TypedResults.NotFound(nameof(MangaId));

        var notFound = new List<string>();
        int applied = 0;

        foreach (var (chapterNumber, volumeNumber) in request.Assignments)
        {
            var chapter = manga.Chapters
                .FirstOrDefault(c => string.Equals(c.ChapterNumber, chapterNumber, StringComparison.OrdinalIgnoreCase));

            if (chapter is null)
            {
                notFound.Add(chapterNumber);
                continue;
            }

            chapter.VolumeNumber = volumeNumber;
            chapter.MetadataConfidence = Schema.SeriesContext.MetadataConfidence.Manual;
            applied++;
        }

        if (manga.MetadataSource is not null)
        {
            manga.MetadataSource.SourceType = MetadataSourceType.Manual;
            manga.MetadataSource.Status = MetadataSourceStatus.Confirmed;
        }

        await context.Sync(HttpContext.RequestAborted, GetType(), nameof(PostBulkAssignment));

        return TypedResults.Ok(new BulkAssignmentResult(applied, notFound));
    }

    /// <summary>
    /// Queues a BundleVolumeWorker to merge all unbundled chapters into a single CBZ.
    /// </summary>
    /// <param name="MangaId"><see cref="SchemaManga"/>.Key</param>
    /// <param name="VolumeNumber">Volume number to bundle</param>
    /// <response code="202">Worker queued; returns job ID</response>
    /// <response code="404">Series or VolumeMetadata not found</response>
    /// <response code="409">No unbundled chapters with files to bundle</response>
    [HttpPost("volumes/{VolumeNumber}/bundle")]
    [ProducesResponseType<BundleJobResult>(Status202Accepted, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status409Conflict, "text/plain")]
    public async Task<Results<Accepted<BundleJobResult>, NotFound<string>, Conflict<string>>> PostBundle(string MangaId, int VolumeNumber)
    {
        var manga = await context.Series
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted);
        if (manga is null)
            return TypedResults.NotFound(nameof(MangaId));

        var volumeMetadata = await context.VolumeMetadata
            .FirstOrDefaultAsync(v => v.MangaId == MangaId && v.VolumeNumber == VolumeNumber, HttpContext.RequestAborted);
        if (volumeMetadata is null)
            return TypedResults.NotFound(nameof(VolumeNumber));

        bool hasUnbundledChapters = await context.Chapters
            .AnyAsync(c => c.ParentMangaId == MangaId
                           && c.VolumeNumber == VolumeNumber
                           && !c.IsBundled
                           && c.FileName != null, HttpContext.RequestAborted);
        if (!hasUnbundledChapters)
            return TypedResults.Conflict("No unbundled chapters with files exist for this volume");

        var worker = new BundleVolumeWorker(MangaId, VolumeNumber, settings);
        workerQueue.AddWorker(worker);
        return TypedResults.Accepted<BundleJobResult>((string?)null, new BundleJobResult(worker.Key));
    }

    /// <summary>
    /// Queues an UnbundleVolumeWorker to split the bundle CBZ back into individual chapter CBZs.
    /// </summary>
    /// <param name="MangaId"><see cref="SchemaManga"/>.Key</param>
    /// <param name="VolumeNumber">Volume number to unbundle</param>
    /// <response code="202">Worker queued; returns job ID (may include warning if no map exists)</response>
    /// <response code="404">Series or VolumeMetadata not found</response>
    /// <response code="409">Volume is not bundled</response>
    [HttpDelete("volumes/{VolumeNumber}/bundle")]
    [ProducesResponseType<UnbundleJobResult>(Status202Accepted, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    [ProducesResponseType<string>(Status409Conflict, "text/plain")]
    public async Task<Results<Accepted<UnbundleJobResult>, NotFound<string>, Conflict<string>>> DeleteBundle(string MangaId, int VolumeNumber)
    {
        var manga = await context.Series
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Key == MangaId, HttpContext.RequestAborted);
        if (manga is null)
            return TypedResults.NotFound(nameof(MangaId));

        var volumeMetadata = await context.VolumeMetadata
            .FirstOrDefaultAsync(v => v.MangaId == MangaId && v.VolumeNumber == VolumeNumber, HttpContext.RequestAborted);
        if (volumeMetadata is null)
            return TypedResults.NotFound(nameof(VolumeNumber));

        if (volumeMetadata.ArchiveFileName is null)
            return TypedResults.Conflict("Volume is not bundled");

        bool hasMaps = await context.BundleChapterMaps
            .AnyAsync(m => m.VolumeKey == volumeMetadata.Key, HttpContext.RequestAborted);

        string? warning = hasMaps
            ? null
            : "No chapter map found; unbundle may be incomplete";

        var worker = new UnbundleVolumeWorker(MangaId, VolumeNumber, settings);
        workerQueue.AddWorker(worker);
        return TypedResults.Accepted<UnbundleJobResult>((string?)null, new UnbundleJobResult(worker.Key, warning));
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private static string ComputeTargetPath(SchemaManga manga, Schema.SeriesContext.Chapter chapter, TrangaSettings settings)
    {
        string fileName = chapter.GetArchiveFileName(settings.ChapterNamingScheme);
        return manga.LibraryLayout switch
        {
            LibraryLayout.Flat => Path.Join(manga.FullDirectoryPath, fileName),
            LibraryLayout.VolumeFolder when chapter.VolumeNumber is not null =>
                Path.Join(manga.FullDirectoryPath, $"Vol {chapter.VolumeNumber}", fileName),
            LibraryLayout.VolumeFolder => Path.Join(manga.FullDirectoryPath, fileName), // null VolumeNumber stays flat
            LibraryLayout.VolumeCBZ when chapter.VolumeNumber is not null =>
                Path.Join(manga.FullDirectoryPath, $"Vol {chapter.VolumeNumber}", fileName), // unbundled chapters use VolumeFolder-like path
            _ => Path.Join(manga.FullDirectoryPath, fileName)
        };
    }

    private ReorganizePreviewResult ComputeReorganizePreview(SchemaManga manga)
    {
        string mangaDir = manga.FullDirectoryPath;

        var moves = new List<FileMove>();

        foreach (var chapter in manga.Chapters)
        {
            if (chapter.IsBundled)
                continue;

            string? currentPath = chapter.FullArchiveFilePath;
            if (currentPath is null)
                continue;

            string targetPath = ComputeTargetPath(manga, chapter, settings);

            if (currentPath != targetPath)
                moves.Add(new FileMove(From: currentPath, To: targetPath));
        }

        // Directories that need to be created (parent dirs of 'to' paths that don't exist)
        var creates = moves
            .Select(m => Path.GetDirectoryName(m.To) + Path.DirectorySeparatorChar)
            .Distinct()
            .Where(dir => !string.IsNullOrEmpty(dir) && !Directory.Exists(dir.TrimEnd(Path.DirectorySeparatorChar)))
            .ToList();

        // Directories that will become empty after all moves
        var movedFromDirs = moves
            .Select(m => Path.GetDirectoryName(m.From))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .ToList();

        var movedFromFiles = moves.Select(m => m.From).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var deletes = movedFromDirs
            .Where(dir =>
            {
                if (!Directory.Exists(dir))
                    return false;
                var remaining = Directory.EnumerateFiles(dir!, "*", SearchOption.AllDirectories)
                    .Where(f => !movedFromFiles.Contains(f))
                    .Any();
                return !remaining;
            })
            .Select(dir => dir + Path.DirectorySeparatorChar)
            .ToList();

        return new ReorganizePreviewResult(moves, creates, deletes);
    }
}
