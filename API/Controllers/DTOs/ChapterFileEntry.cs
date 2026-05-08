namespace API.Controllers.DTOs;

/// <summary>Per-chapter entry in a volume listing.</summary>
public record ChapterFileEntry(
    string ChapterId,
    string ChapterNumber,
    string? FileName,
    bool FileExistsOnDisk,
    bool IsBundled,
    string? MetadataConfidence
);
