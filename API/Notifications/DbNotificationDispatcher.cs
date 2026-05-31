using API.Schema.NotificationsContext;
using log4net;
using Microsoft.Extensions.DependencyInjection;

namespace API.Notifications;

/// <summary>
/// Persists notifications to the NotificationsContext. The downstream SendNotificationsWorker is
/// what actually fans them out to the configured external notification connectors (Gotify, Ntfy, …).
/// Creates its own DI scope per dispatch so the dispatcher itself can be a singleton.
/// </summary>
public class DbNotificationDispatcher(IServiceScopeFactory scopeFactory) : INotificationDispatcher
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(DbNotificationDispatcher));

    public async Task DispatchAsync(string title, string body, CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        NotificationsContext context = scope.ServiceProvider.GetRequiredService<NotificationsContext>();
        await context.Notifications.AddAsync(new Notification(title, body), ct);
        if (await context.Sync(ct, GetType(), "DispatchNotification") is { success: false } ex)
            Log.ErrorFormat("Failed to persist notification '{0}': {1}", title, ex.exceptionMessage);
    }
}
