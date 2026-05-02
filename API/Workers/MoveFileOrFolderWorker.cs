using API.Schema.ActionsContext;
using API.Schema.ActionsContext.Actions;

namespace API.Workers;

public class MoveFileOrFolderWorker(string toLocation, string fromLocation, IEnumerable<BaseWorker>? dependsOn = null)
    : BaseWorkerWithContexts(dependsOn)
{
    public readonly string FromLocation = fromLocation;
    public readonly string ToLocation = toLocation;

    // 1. Renamed to _actionsContext to remove the need for [SuppressMessage]
    private ActionsContext _actionsContext = null!;

    protected override void SetContexts(IServiceScope serviceScope)
    {
        _actionsContext = GetContext<ActionsContext>(serviceScope);
    }

    protected override async Task<BaseWorker[]> DoWorkInternal()
    {
        try
        {
            bool isDir = Directory.Exists(FromLocation);
            bool isFile = File.Exists(FromLocation);

            if (!isDir && !isFile)
            {
                Log.ErrorFormat("Source does not exist at {0}", FromLocation);
                return [];
            }

            if (File.Exists(ToLocation) || Directory.Exists(ToLocation))
            {
                Log.ErrorFormat("Destination already exists at {0}", ToLocation);
                return [];
            }

            // 2. Unify the directory creation logic before we attempt the move
            EnsureParentDirectoryExists(ToLocation);

            // 3. Inline the moves directly using the string paths
            if (isDir)
                Directory.Move(FromLocation, ToLocation);
            else
                File.Move(FromLocation, ToLocation);
        }
        catch (Exception e)
        {
            Log.Error(e);
            return []; // Bail out on IO failure so we don't accidentally update the DB!
        }

        _actionsContext.Actions.Add(new DataMovedActionRecord(FromLocation, ToLocation));
        if (await _actionsContext.Sync(CancellationToken, GetType(), "Library Moved") is { success: false } syncResult)
        {
            Log.ErrorFormat("Failed to save database changes: {0}", syncResult.exceptionMessage);
        }

        return [];
    }

    // A single, unified helper method for creating the target paths
    private static void EnsureParentDirectoryExists(string path)
    {
        var parentDir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }
    }

    public override string ToString() => $"{base.ToString()} {FromLocation} {ToLocation}";
}
