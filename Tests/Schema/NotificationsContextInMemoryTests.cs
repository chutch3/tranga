using API.Schema.NotificationsContext;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace API.Tests.Schema;

/// <summary>
/// Pinning test: the in-memory provider must be able to finalise the model for NotificationsContext.
/// Historically NotificationConnector's only constructor took Dictionary&lt;string,string&gt; Headers
/// which EF Core's ConstructorBindingConvention could not bind under the InMemory provider —
/// causing any test that uses NotificationsContext to throw during model finalization.
/// </summary>
public class NotificationsContextInMemoryTests
{
    [Fact]
    public async Task InMemoryNotificationsContext_CanFinaliseModel_AndAddNotificationConnector()
    {
        var options = new DbContextOptionsBuilder<NotificationsContext>()
            .UseInMemoryDatabase("notif-im-" + Guid.NewGuid().ToString("N"))
            .Options;
        await using var context = new NotificationsContext(options);

        // Force model finalisation by accessing a DbSet.
        await context.NotificationConnectors.AnyAsync();

        context.NotificationConnectors.Add(new API.Schema.NotificationsContext.NotificationConnectors.NotificationConnector(
            "test", "https://example.test", new Dictionary<string, string> { ["X"] = "Y" }, "POST", "body"));
        await context.SaveChangesAsync();

        var fetched = await context.NotificationConnectors.FirstAsync();
        Assert.Equal("test", fetched.Name);
    }
}
