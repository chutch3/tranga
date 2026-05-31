namespace API.Notifications;

/// <summary>
/// Owned abstraction for emitting a user-visible notification. Decouples notification fan-out from
/// the workers that produce events: download workers only persist ActionRecords; the periodic
/// NotifyOnNewDownloadsWorker observes those records and calls this dispatcher.
/// </summary>
public interface INotificationDispatcher
{
    Task DispatchAsync(string title, string body, CancellationToken ct);
}
