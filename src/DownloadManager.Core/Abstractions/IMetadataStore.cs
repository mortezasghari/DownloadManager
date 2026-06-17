using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Abstractions;

/// <summary>
/// Reads and writes the cold <c>*.dlmeta</c> sidecar (spec §6a). Writes are atomic and durable
/// (temp → fsync temp → atomic rename → fsync directory), so an interrupted save never leaves a
/// torn metadata file.
/// </summary>
public interface IMetadataStore
{
    Task SaveAsync(string targetPath, DownloadMetadata metadata, CancellationToken cancellationToken);

    /// <summary>Returns the persisted metadata for the target, or <c>null</c> if none exists / is unreadable.</summary>
    Task<DownloadMetadata?> TryLoadAsync(string targetPath, CancellationToken cancellationToken);

    void Delete(string targetPath);

    /// <summary>
    /// Enumerates the target file paths in <paramref name="directory"/> that have a metadata sidecar.
    /// Backs the startup recovery scan (spec §6d); the sidecar naming convention stays in the store.
    /// </summary>
    IEnumerable<string> EnumerateTargets(string directory);
}