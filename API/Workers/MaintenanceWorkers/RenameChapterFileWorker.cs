using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace API.Workers.MaintenanceWorkers;

public class RenameChapterFileWorker(string chapterKey, string newFileName, TrangaSettings settings, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    private MangaContext _mangaContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<MangaContext>(serviceScope);
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
            File.Move(oldPath, newPath);
        }

        chapter.FileName = newFileName;
        await _mangaContext.Sync(CancellationToken, GetType(), nameof(DoWorkInternal));

        return [];
    }

    public override string ToString() => $"{base.ToString()} {chapterKey} -> {newFileName}";
}
