using System.Text.Json;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Domain;
using DownloadManager.Persistence.Io;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence.Metadata;

/// <summary>
/// <see cref="IMetadataStore"/> backed by a source-generated-JSON <c>*.dlmeta</c> sidecar, written
/// atomically and durably via <see cref="AtomicFileWriter"/> (spec §6a). A torn or unparseable file
/// is treated as "no metadata" so recovery falls back to a clean restart rather than crashing.
/// </summary>
public sealed partial class JsonMetadataStore(ILogger<JsonMetadataStore> logger) : IMetadataStore
{
    private readonly ILogger<JsonMetadataStore> _logger = logger;

    public Task SaveAsync(string targetPath, DownloadMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        ArgumentNullException.ThrowIfNull(metadata);
        cancellationToken.ThrowIfCancellationRequested();

        var path = PersistencePaths.MetadataPath(targetPath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, MetadataJsonContext.Default.DownloadMetadata);
        AtomicFileWriter.WriteAllBytes(path, bytes);
        LogSaved(path, metadata.TotalSize);
        return Task.CompletedTask;
    }

    public async Task<DownloadMetadata?> TryLoadAsync(string targetPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        var path = PersistencePaths.MetadataPath(targetPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(bytes, MetadataJsonContext.Default.DownloadMetadata);
        }
        catch (JsonException ex)
        {
            LogUnreadable(path, ex.Message);
            return null;
        }
    }

    public void Delete(string targetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        var path = PersistencePaths.MetadataPath(targetPath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IEnumerable<string> EnumerateTargets(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        const string extension = PersistencePaths.MetadataExtension;
        foreach (var metadataFile in Directory.EnumerateFiles(directory, "*" + extension))
        {
            // Defend against the legacy Windows glob quirk where "*.dlmeta" can match longer extensions.
            if (metadataFile.EndsWith(extension, StringComparison.Ordinal))
            {
                yield return metadataFile[..^extension.Length];
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Saved metadata {Path} (total size {TotalSize} bytes).")]
    private partial void LogSaved(string path, long totalSize);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metadata {Path} is unreadable, treating as absent: {Reason}")]
    private partial void LogUnreadable(string path, string reason);
}