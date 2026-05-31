using System.ComponentModel.DataAnnotations;

namespace API.Schema.ActionsContext.Actions.Generic;

public interface IActionWithMangaRecord
{
    /// <summary>
    /// <see cref="Schema.MangaContext.Series"/> for which the cover was downloaded
    /// </summary>
    [StringLength(64)]
    public string MangaId { get; init; }
}