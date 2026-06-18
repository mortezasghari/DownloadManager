namespace DownloadManager.Core.Domain;

public enum DownloadResultKind
{
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// Terminal result of one engine run. For failures it carries the retry classification and any
/// server-supplied <c>Retry-After</c>, so the scheduler can decide whether/when to retry (spec §8)
/// without re-inspecting HTTP details.
/// </summary>
public sealed record DownloadOutcome(
    DownloadResultKind Kind,
    long CompletedBytes,
    string? Error = null,
    bool IsTransient = false,
    TimeSpan? RetryAfter = null,
    bool NeedsCredentials = false)
{
    public static DownloadOutcome Completed(long bytes) => new(DownloadResultKind.Completed, bytes);

    public static DownloadOutcome Canceled(long bytes) => new(DownloadResultKind.Canceled, bytes);

    public static DownloadOutcome Failed(
        long bytes, string error, bool isTransient, TimeSpan? retryAfter = null, bool needsCredentials = false) =>
        new(DownloadResultKind.Failed, bytes, error, isTransient, retryAfter, needsCredentials);
}