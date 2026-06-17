using System.Text.Json.Serialization;

namespace DownloadManager.Core.Domain;

/// <summary>
/// The cold, low-frequency metadata persisted as <c>*.dlmeta</c> (spec §6a). Serialized with
/// System.Text.Json <b>source generators</b> (no reflection); written atomically. Holds everything
/// needed to revalidate and resume a download after a crash or reboot.
/// </summary>
public sealed record DownloadMetadata
{
    /// <summary>On-disk schema version, so a format change is detectable rather than silently misread.</summary>
    public int Version { get; init; } = 1;

    public required string OriginalUrl { get; init; }

    /// <summary>The post-redirect URL. Resume requests go here (spec §3: persist the final URL).</summary>
    public required string FinalUrl { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Total resource size in bytes, or -1 if unknown (non-resumable single stream).</summary>
    public required long TotalSize { get; init; }

    /// <summary>Whether range probing (§3) confirmed a real <c>206</c> with a sane <c>Content-Range</c>.</summary>
    public required bool AcceptsRanges { get; init; }

    /// <summary>The non-overlapping segment partition. Array (not interface) for AOT-friendly source-gen JSON.</summary>
    public required SegmentRange[] Segments { get; init; }

    /// <summary>Optional expected SHA-256 for post-completion verification (spec §4 / Phase 4).</summary>
    public string? ExpectedSha256 { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore]
    public ResourceValidators Validators => new(ETag, LastModified);
}