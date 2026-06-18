namespace DownloadManager.Core.Domain;

/// <summary>Base type for engine errors carrying a transient/permanent classification (spec §8).</summary>
public abstract class DownloadException(string message, bool isTransient, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>True if a retry with backoff might succeed (timeout, reset, 5xx, 429); false for 404/401/etc.</summary>
    public bool IsTransient { get; } = isTransient;

    /// <summary>Server-supplied <c>Retry-After</c> hint (from 429/503), if any. The scheduler honors it.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// The resource changed underneath us mid-download (size or validator mismatch on reconnect), or a
/// resume precondition failed. Fail loudly rather than stitching mismatched bytes (spec §7).
/// </summary>
public sealed class ResourceChangedException(string message)
    : DownloadException(message, isTransient: false);

/// <summary>A transient transport/server condition; safe to retry with backoff.</summary>
public sealed class TransientDownloadException(string message, Exception? inner = null)
    : DownloadException(message, isTransient: true, inner);

/// <summary>A permanent failure (e.g. 404/410/401/403); retrying will not help.</summary>
public sealed class PermanentDownloadException(string message, Exception? inner = null)
    : DownloadException(message, isTransient: false, inner);