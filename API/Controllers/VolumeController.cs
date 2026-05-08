using API.Controllers.DTOs;
using API.Schema.MangaContext;
using API.Workers;
using API.Workers.MaintenanceWorkers;
using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Microsoft.AspNetCore.Http.StatusCodes;
using SchemaManga = API.Schema.MangaContext.Manga;

// ReSharper disable InconsistentNaming

namespace API.Controllers;

[ApiVersion(2)]
[ApiController]
[Route("v{v:apiVersion}/Manga/{MangaId}")]
public class VolumeController(MangaContext context, TrangaSettings settings, IWorkerQueue workerQueue)
    : ControllerBase
{
    /// <summary>
    /// Returns volumes and chapters for a manga, grouped by volume number.
    /// </summary>
    /// <param name="MangaId"><see cref="Manga"/>.Key</param>
    /// <response code="200">Volume listing with chapter file status</response>
    /// <response code="404">Manga not found</response>
    [HttpGet("volumes")]
    [ProducesResponseType<VolumeListResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<VolumeListResult>, NotFound<string>>> GetVolumes(string MangaId)
    {
        var manga = await context.Mangas
            .Include(m => m.Library)
            .Include(m => m.Chapters)
            .ThenInclude(c => c.MangaConnectorIds)
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
            Volumes: volumes,
            Unassigned: unassigned
        );

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// Returns a dry-run preview of all file moves needed to bring files in line with current metadata.
    /// </summary>
    /// <param name="MangaId"><see cref="Manga"/>.Key</param>
    /// <response code="200">Preview with moves, directories to create, and empty directories to delete</response>
    /// <response code="404">Manga not found</response>
    [HttpGet("reorganize/preview")]
    [ProducesResponseType<ReorganizePreviewResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Ok<ReorganizePreviewResult>, NotFound<string>>> GetReorganizePreview(string MangaId)
    {
        var manga = await context.Mangas
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
    /// <param name="MangaId"><see cref="Manga"/>.Key</param>
    /// <response code="202">Workers queued; returns first worker key as jobId</response>
    /// <response code="200">Nothing to reorganize</response>
    /// <response code="404">Manga not found</response>
    [HttpPost("reorganize")]
    [ProducesResponseType<ReorganizeJobResult>(Status202Accepted, "application/json")]
    [ProducesResponseType<ReorganizeJobResult>(Status200OK, "application/json")]
    [ProducesResponseType<string>(Status404NotFound, "text/plain")]
    public async Task<Results<Accepted<ReorganizeJobResult>, Ok<ReorganizeJobResult>, NotFound<string>>> PostReorganize(string MangaId)
    {
        var manga = await context.Mangas
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

    // ─── Private helpers ──────────────────────────────────────────────────────

    private ReorganizePreviewResult ComputeReorganizePreview(SchemaManga manga)
    {
        string namingScheme = settings.ChapterNamingScheme;
        string mangaDir = manga.FullDirectoryPath;

        var moves = new List<FileMove>();

        foreach (var chapter in manga.Chapters)
        {
            if (chapter.IsBundled)
                continue;

            string? currentPath = chapter.FullArchiveFilePath;
            if (currentPath is null)
                continue;

            string targetFileName = chapter.GetArchiveFileName(namingScheme);
            string targetPath = Path.Join(mangaDir, targetFileName);

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
