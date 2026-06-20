namespace DownloadManager.Core.Routing;

/// <summary>
/// Thrown when a resolved destination would fall outside its intended folder (ADR-0020, audit F1). The
/// router rejects rather than writes. In practice the leaf-name sanitizer prevents this on the normal
/// path; it is the enforced guarantee for the explicit-path branch and a belt-and-suspenders backstop.
/// </summary>
public sealed class PathContainmentException(string message) : Exception(message);