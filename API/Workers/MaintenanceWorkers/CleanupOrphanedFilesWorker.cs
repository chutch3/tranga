using API.Schema.SeriesContext;
using Microsoft.EntityFrameworkCore;

namespace API.Workers.MaintenanceWorkers;

public class CleanupOrphanedFilesWorker(bool dryRun = false, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn), IPeriodic
{
    private SeriesContext _mangaContext = null!;
    private readonly bool _dryRun = dryRun;

    // IPeriodic implementation
    public DateTime LastExecution { get; set; } = DateTime.MinValue;
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _mangaContext = GetContext<SeriesContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        Log.Info($"Starting Orphaned Files Cleanup (DryRun: {_dryRun})...");

        // 1. Get all libraries
        List<FileLibrary> libraries = await _mangaContext.FileLibraries.ToListAsync(CancellationToken);
        
        // 2. Get all chapters that are downloaded and their full paths
        List<Chapter> chapters = await _mangaContext.Chapters
            .Include(c => c.ParentManga)
            .ThenInclude(m => m.Library)
            .Where(c => c.Downloaded && c.FileName != null)
            .ToListAsync(CancellationToken);

        HashSet<string> trackedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (Chapter chapter in chapters)
        {
            if (chapter.GetFullFilepath(null) is { } path)
            {
                trackedPaths.Add(Path.GetFullPath(path));
            }
        }

        Log.Debug($"Found {trackedPaths.Count} tracked files in database.");

        int deletedCount = 0;
        long deletedSize = 0;

        foreach (FileLibrary library in libraries)
        {
            if (!Directory.Exists(library.BasePath))
            {
                Log.Warn($"Library path does not exist: {library.BasePath}");
                continue;
            }

            Log.Debug($"Scanning library: {library.LibraryName} ({library.BasePath})");

            string[] files = Directory.GetFiles(library.BasePath, "*", SearchOption.AllDirectories);
            
            foreach (string file in files)
            {
                string fullPath = Path.GetFullPath(file);
                string extension = Path.GetExtension(fullPath).ToLowerInvariant();

                // Only consider chapter-like files
                if (extension != ".cbz" && extension != ".zip" && extension != ".rar")
                    continue;

                if (!trackedPaths.Contains(fullPath))
                {
                    FileInfo fileInfo = new(fullPath);
                    Log.Info($"{( _dryRun ? "[DRY RUN] Would delete" : "Deleting" )} orphaned file: {fullPath} ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
                    
                    if (!_dryRun)
                    {
                        try
                        {
                            File.Delete(fullPath);
                            deletedCount++;
                            deletedSize += fileInfo.Length;
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Failed to delete {fullPath}: {ex.Message}");
                        }
                    }
                    else
                    {
                        deletedCount++;
                        deletedSize += fileInfo.Length;
                    }
                }
            }
        }

        Log.Info($"Cleanup complete. {(_dryRun ? "Found" : "Deleted")} {deletedCount} files ({deletedSize / 1024.0 / 1024.0:F2} MB).");

        LastExecution = DateTime.UtcNow;
        return [];
    }

    public override string ToString() => $"{base.ToString()} DryRun={_dryRun}";
}
