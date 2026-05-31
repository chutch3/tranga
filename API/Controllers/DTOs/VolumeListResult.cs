using API.Schema.SeriesContext;

namespace API.Controllers.DTOs;

/// <summary>Top-level response for GET /api/v2/Series/{mangaId}/volumes.</summary>
public record VolumeListResult(
    int FilesNeedReorganizing,
    LibraryLayout Layout,
    List<VolumeEntry> Volumes,
    List<ChapterFileEntry> Unassigned
);
