using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace API.Schema.SeriesContext;

/// <summary>
/// Used only by EF design-time tools (migrations). Not used at runtime.
/// </summary>
public class SeriesContextDesignTimeFactory : IDesignTimeDbContextFactory<SeriesContext>
{
    public SeriesContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SeriesContext>();
        // Placeholder connection string — only used by EF design-time tooling
        optionsBuilder.UseNpgsql("Host=localhost;Database=tranga;Username=tranga;Password=tranga");
        return new SeriesContext(optionsBuilder.Options);
    }
}
