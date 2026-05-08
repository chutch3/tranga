namespace API.Controllers.DTOs;

/// <summary>Dry-run diff for a reorganize operation.</summary>
public record ReorganizePreviewResult(
    List<FileMove> Moves,
    List<string> Creates,
    List<string> Deletes
);
