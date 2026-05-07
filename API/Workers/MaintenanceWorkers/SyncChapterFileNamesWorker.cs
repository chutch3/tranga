using API.Schema.MangaContext;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace API.Workers.MaintenanceWorkers;

public class SyncChapterFileNamesWorker(TrangaSettings settings, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    private MangaContext _mangaContext = null!;

    public DateTime LastExecution { get; set; } = DateTime.MinValue;
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<MangaContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        var chapters = await _mangaContext.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .Where(c => c.Downloaded && c.FileName != null)
            .ToListAsync(CancellationToken);

        int updatedCount = 0;

        foreach (var chapter in chapters)
        {
            string expected = chapter.GetArchiveFileName(settings.ChapterNamingScheme);
            if (chapter.FileName == expected) continue;

            string? oldPath = chapter.FullArchiveFilePath;
            string? newPath = chapter.ParentManga.FullDirectoryPath is { } dir
                ? Path.Join(dir, expected)
                : null;

            if (oldPath != null && newPath != null && oldPath != newPath && File.Exists(oldPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                File.Move(oldPath, newPath);
            }

            chapter.FileName = expected;
            updatedCount++;
        }

        if (updatedCount > 0)
        {
            await _mangaContext.Sync(CancellationToken, GetType(), nameof(DoWorkInternal));
        }

        LastExecution = DateTime.UtcNow;
        return [];
    }
}
