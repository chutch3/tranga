using API.Schema.SeriesContext;

namespace API.Controllers.Requests;

/// <summary>Request body for PUT /api/v2/Series/{mangaId}/libraryLayout.</summary>
public record PutLibraryLayoutRecord(LibraryLayout Layout);
