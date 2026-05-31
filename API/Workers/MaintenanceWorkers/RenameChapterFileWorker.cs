using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace API.Workers.MaintenanceWorkers;

public class RenameChapterFileWorker(string chapterKey, string newFileName, TrangaSettings settings, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    private SeriesContext _mangaContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<SeriesContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        var chapter = await _mangaContext.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .FirstOrDefaultAsync(c => c.Key == chapterKey, CancellationToken);

        if (chapter is null) return [];

        string? oldPath = chapter.FullArchiveFilePath;
        string? newPath = chapter.ParentManga.FullDirectoryPath is { } dir
            ? Path.Join(dir, newFileName)
            : null;

        if (oldPath != null && newPath != null && oldPath != newPath && File.Exists(oldPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            try
            {
                File.Move(oldPath, newPath);
            }
            catch (IOException ex)
            {
                Log.Warn($"Could not move '{oldPath}' to '{newPath}': {ex.Message}. Updating DB filename anyway.");
            }
        }

        chapter.FileName = newFileName;
        await _mangaContext.Sync(CancellationToken, GetType(), nameof(DoWorkInternal));

        return [];
    }

    public override string ToString() => $"{base.ToString()} {chapterKey} -> {newFileName}";
}
