namespace DownloadManager.Persistence;

/// <summary>
/// Derives the sidecar paths that live beside a download's target file:
/// <c>movie.mkv</c> -&gt; <c>movie.mkv.dlmeta</c> (cold metadata, §6a) and
/// <c>movie.mkv.dllog</c> (hot append-only progress log, §6b).
/// Keeping the sidecars adjacent to the target guarantees they share a
/// filesystem, which the atomic-rename and fsync-directory durability steps
/// (§6c) require.
/// </summary>
public static class PersistencePaths
{
    public const string MetadataExtension = ".dlmeta";
    public const string ProgressLogExtension = ".dllog";

    public static string MetadataPath(string targetFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetFilePath);
        return targetFilePath + MetadataExtension;
    }

    public static string ProgressLogPath(string targetFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetFilePath);
        return targetFilePath + ProgressLogExtension;
    }
}