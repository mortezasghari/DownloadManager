namespace DownloadManager.Core.Domain;

public enum DownloadResultKind
{
    Completed,
    Failed,
    Canceled,
}

/// <summary>Terminal result of an engine run.</summary>
public sealed record DownloadOutcome(DownloadResultKind Kind, long CompletedBytes, string? Error = null)
{
    public static DownloadOutcome Completed(long bytes) => new(DownloadResultKind.Completed, bytes);

    public static DownloadOutcome Canceled(long bytes) => new(DownloadResultKind.Canceled, bytes);

    public static DownloadOutcome Failed(long bytes, string error) =>
        new(DownloadResultKind.Failed, bytes, error);
}