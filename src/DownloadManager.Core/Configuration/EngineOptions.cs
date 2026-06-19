namespace DownloadManager.Core.Configuration;

/// <summary>
/// Tunables for the copy loop and durability cadence (spec §11). Defaults: 128 KB copy buffer,
/// an 8 MB fsync checkpoint interval (the dominant throughput/durability trade-off — §6b/§11),
/// and a per-attempt deadline enforced via a linked CTS rather than <c>HttpClient.Timeout</c> (§3).
/// </summary>
/// <remarks>
/// A few properties are <c>set</c> rather than <c>init</c> so the queue-settings panel (Phase 8/ADR-0018)
/// can update this shared singleton live. The engine reads them at their natural cadence —
/// <see cref="PerAttemptTimeout"/> per attempt, <see cref="SmallFileThresholdBytes"/> at download
/// start — so a change applies to the next attempt / next-started download with no engine code change.
/// Each is a single word (long / TimeSpan-over-long), written rarely from the UI thread; on the 64-bit
/// RIDs we ship, and with the memory barriers the worker hits between downloads, reads are coherent.
/// </remarks>
public sealed record EngineOptions
{
    public const string SectionName = "Engine";

    /// <summary>Network→disk copy buffer, rented from <c>ArrayPool&lt;byte&gt;</c>. 64–128 KB per §3.</summary>
    public int CopyBufferSize { get; init; } = 128 * 1024;

    /// <summary>Bytes to write between durable checkpoints (data fsync → log append → log fsync).</summary>
    public long CheckpointIntervalBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>
    /// Maximum time a single attempt may take. Enforced with a linked
    /// <see cref="CancellationTokenSource"/> deadline so a slow-but-progressing large-file stream is
    /// never aborted the way a global <c>HttpClient.Timeout</c> would (spec §3).
    /// </summary>
    public TimeSpan PerAttemptTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Upper bound on segments per download. The requested count is clamped to <c>[1, this]</c>
    /// (ADR-0007). Segments only happen when the probe confirmed real <c>206</c> range support.
    /// </summary>
    public int MaxSegmentsPerDownload { get; init; } = 16;

    /// <summary>
    /// Files smaller than this are downloaded as a single stream regardless of the requested segment
    /// count — splitting tiny files just adds round-trips and fsyncs for no throughput gain (ADR-0007).
    /// </summary>
    public long SmallFileThresholdBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>Maximum segments downloaded in parallel within one download (spec §8).</summary>
    public int MaxSegmentConcurrency { get; init; } = 8;
}