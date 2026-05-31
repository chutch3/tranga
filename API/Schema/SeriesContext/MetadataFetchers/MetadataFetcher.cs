using log4net;
using Microsoft.EntityFrameworkCore;

namespace API.Schema.SeriesContext.MetadataFetchers;

[PrimaryKey("Name")]
public abstract class MetadataFetcher
{
    // ReSharper disable once EntityFramework.ModelValidation.UnlimitedStringLength
    public string Name { get; init; }

    protected ILog Log;

    protected MetadataFetcher()
    {
        this.Name = this.GetType().Name;
        this.Log = LogManager.GetLogger(Name);
    }
    
    /// <summary>
    /// EFCORE ONLY!!!
    /// </summary>
    internal MetadataFetcher(string name)
    {
        this.Name = name;
        this.Log = LogManager.GetLogger(Name);
    }

    internal MetadataEntry CreateMetadataEntry(Series manga, string identifier) =>
        new (this, manga, identifier);
    
    public abstract Task<MetadataSearchResult[]> SearchMetadataEntry(Series manga);
    
    public abstract Task<MetadataSearchResult[]> SearchMetadataEntry(string searchTerm);

    /// <summary>
    /// Updates the Series linked in the MetadataEntry
    /// </summary>
    public abstract Task UpdateMetadata(MetadataEntry metadataEntry, SeriesContext dbContext, CancellationToken token);
}