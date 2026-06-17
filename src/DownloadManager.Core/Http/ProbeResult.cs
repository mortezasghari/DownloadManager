using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Http;

/// <summary>Outcome of a range-support probe (spec §3).</summary>
/// <param name="FinalUrl">The post-redirect URL that must be persisted and used for all requests.</param>
/// <param name="TotalSize">Total resource size, or -1 if the server didn't disclose it.</param>
/// <param name="AcceptsRanges">True only if a real <c>206</c> with a sane <c>Content-Range</c> was observed.</param>
/// <param name="Validators">ETag / Last-Modified captured for use as resume preconditions.</param>
public sealed record ProbeResult(Uri FinalUrl, long TotalSize, bool AcceptsRanges, ResourceValidators Validators);

/// <summary>Outcome of re-validating a stored resource before resuming (spec §6d/§7).</summary>
/// <param name="Unchanged">True if the server proved (via <c>If-Range</c>) the resource is unchanged.</param>
public sealed record RevalidationResult(bool Unchanged);