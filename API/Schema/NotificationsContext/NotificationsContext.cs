using API.Schema.NotificationsContext.NotificationConnectors;
using Microsoft.EntityFrameworkCore;

namespace API.Schema.NotificationsContext;

public class NotificationsContext(DbContextOptions<NotificationsContext> options) : TrangaBaseContext<NotificationsContext>(options)
{
    public DbSet<NotificationConnector> NotificationConnectors { get; set; }
    public DbSet<Notification> Notifications { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Postgres maps Dictionary<string,string> to hstore via the Npgsql provider. Non-relational
        // providers (e.g. the InMemory provider used in tests) have no such convention and would
        // fail model validation on this property. Ignore it for non-relational providers so the
        // model finalises in tests; production Postgres deployment is unaffected.
        if (!Database.IsRelational())
            modelBuilder.Entity<NotificationConnector>().Ignore(c => c.Headers);
    }
}