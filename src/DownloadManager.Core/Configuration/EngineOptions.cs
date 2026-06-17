namespace DownloadManager.Core.Configuration;

/// <summary>
/// Tunables for the copy loop and durability cadence (spec §11). Defaults: 128 KB copy buffer,
/// an 8 MB fsync checkpoint interval (the dominant throughput/durability trade-off — §6b/§11),
/// and a per-attempt deadline enforced via a linked CTS rather than <c>HttpClient.Timeout</c> (§3).
/// </summary>
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
    public TimeSpan PerAttemptTimeout { get; init; } = TimeSpan.FromSeconds(100);
}