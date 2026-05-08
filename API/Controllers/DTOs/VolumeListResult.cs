namespace API.Controllers.DTOs;

/// <summary>Top-level response for GET /api/v2/Manga/{mangaId}/volumes.</summary>
public record VolumeListResult(
    int FilesNeedReorganizing,
    List<VolumeEntry> Volumes,
    List<ChapterFileEntry> Unassigned
);
