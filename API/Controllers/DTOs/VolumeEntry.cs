namespace API.Controllers.DTOs;

/// <summary>One volume with its chapter list.</summary>
public record VolumeEntry(
    int VolumeNumber,
    string? Title,
    bool IsBundled,
    string? ArchiveFileName,
    int ChapterCount,
    List<ChapterFileEntry> Chapters
);
