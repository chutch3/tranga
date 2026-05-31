using System.ComponentModel.DataAnnotations;
using API.Schema.ActionsContext.Actions.Generic;
using API.Schema.SeriesContext;

namespace API.Schema.ActionsContext.Actions;

public sealed class CoverDownloadedActionRecord(Actions action, DateTime performedAt, string mangaId, string filename)
    : ActionRecord(action, performedAt), IActionWithMangaRecord
{
    public CoverDownloadedActionRecord(Series manga, string filename) : this(Actions.CoverDownloaded, DateTime.UtcNow, manga.Key, filename) { }

    /// <summary>
    /// Filename on disk
    /// </summary>
    [StringLength(1024)]
    public string Filename { get; init; } = filename;

    public string MangaId { get; init; } = mangaId;
}