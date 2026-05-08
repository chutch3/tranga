namespace API.Controllers.DTOs;

/// <summary>Response body for PUT /api/v2/Manga/{mangaId}/libraryLayout.</summary>
public record LibraryLayoutResult(string Layout, ReorganizePreviewResult ReorganizePreview);
