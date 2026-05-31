using API.Schema.ActionsContext.Actions.Generic;
using API.Schema.SeriesContext;

namespace API.Schema.ActionsContext.Actions;

public sealed class ChapterDownloadedActionRecord(Actions action, DateTime performedAt, string mangaId, string chapterId) : ActionRecord(action, performedAt), IActionWithChapterRecord, IActionWithMangaRecord
{
    public ChapterDownloadedActionRecord(Series manga, Chapter chapter) : this(Actions.ChapterDownloaded, DateTime.UtcNow, manga.Key, chapter.Key) { }
    public string ChapterId { get; init; } = chapterId;
    public string MangaId { get; init; } = mangaId;
}