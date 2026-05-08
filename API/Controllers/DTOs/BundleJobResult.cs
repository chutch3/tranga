namespace API.Controllers.DTOs;

/// <summary>Result returned when a bundle/unbundle job is queued.</summary>
public record BundleJobResult(string JobId);

/// <summary>Result returned when an unbundle job is queued.</summary>
public record UnbundleJobResult(string JobId, string? Warning = null);
