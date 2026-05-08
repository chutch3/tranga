using API.Schema.MangaContext;

namespace API.Controllers.Requests;

/// <summary>Request body for PUT /api/v2/Manga/{mangaId}/libraryLayout.</summary>
public record PutLibraryLayoutRecord(LibraryLayout Layout);
