using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace API.Schema.MangaContext;

/// <summary>
/// Used only by EF design-time tools (migrations). Not used at runtime.
/// </summary>
public class MangaContextDesignTimeFactory : IDesignTimeDbContextFactory<MangaContext>
{
    public MangaContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<MangaContext>();
        // Placeholder connection string — only used by EF design-time tooling
        optionsBuilder.UseNpgsql("Host=localhost;Database=tranga;Username=tranga;Password=tranga");
        return new MangaContext(optionsBuilder.Options);
    }
}
