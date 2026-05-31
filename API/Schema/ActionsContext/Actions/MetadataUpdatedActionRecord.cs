using System.ComponentModel.DataAnnotations;
using API.Schema.ActionsContext.Actions.Generic;
using API.Schema.SeriesContext;
using API.Schema.SeriesContext.MetadataFetchers;

namespace API.Schema.ActionsContext.Actions;

public sealed class MetadataUpdatedActionRecord(Actions action, DateTime performedAt, string mangaId, string metadataFetcher)
    : ActionRecord(action, performedAt), IActionWithMangaRecord
{
    public MetadataUpdatedActionRecord(Series manga, MetadataFetcher fetcher) : this(Actions.MetadataUpdated, DateTime.UtcNow, manga.Key, fetcher.Name) { }

    /// <summary>
    /// Filename on disk
    /// </summary>
    [StringLength(1024)]
    public string MetadataFetcher { get; init; } = metadataFetcher;

    public string MangaId { get; init; } = mangaId;
}