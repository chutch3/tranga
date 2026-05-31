using API.Schema.ActionsContext.Actions.Generic;
using API.Schema.SeriesContext;

namespace API.Schema.ActionsContext.Actions;

public sealed class ChaptersRetrievedActionRecord(Actions action, DateTime performedAt, string mangaId)
    : ActionRecord(action, performedAt), IActionWithMangaRecord
{
    public ChaptersRetrievedActionRecord(Series manga) : this(Actions.ChaptersRetrieved, DateTime.UtcNow, manga.Key) { }

    public string MangaId { get; init; } = mangaId;
}