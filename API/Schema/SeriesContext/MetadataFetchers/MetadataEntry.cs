using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace API.Schema.SeriesContext.MetadataFetchers;

[PrimaryKey("MetadataFetcherName", "Identifier")]
public class MetadataEntry
{
    [JsonIgnore]
    public Series Series { get; init; } = null!;
    public string MangaId  { get; init; }
    public string MetadataFetcherName { get; init; }
    public string Identifier { get; init; }

    public MetadataEntry(MetadataFetcher fetcher, Series manga, string identifier)
    {
        this.Series = manga;
        this.MangaId = manga.Key;
        this.MetadataFetcherName = fetcher.Name;
        this.Identifier = identifier;
    }

    /// <summary>
    /// EFCORE only!!!!
    /// </summary>
    internal MetadataEntry(string mangaId, string identifier, string metadataFetcherName)
    {
        this.MangaId = mangaId;
        this.Identifier = identifier;
        this.MetadataFetcherName = metadataFetcherName;
    }
    
    public override string ToString() => $"{GetType().FullName} {MangaId} {MetadataFetcherName}";
}