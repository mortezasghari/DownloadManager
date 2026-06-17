using DownloadManager.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Core.Recovery;

/// <summary>
/// Startup recovery (spec §6d): scans a directory for metadata sidecars and returns the downloads
/// that can be resumed. The heavy lifting (revalidate via <c>If-Range</c>, reconcile per-segment
/// offsets, restart if changed) is the engine's job when each candidate is re-run, so this stays a
/// cheap, side-effect-free discovery step.
/// </summary>
public sealed partial class RecoveryService(IMetadataStore metadataStore, ILogger<RecoveryService> logger)
{
    private readonly IMetadataStore _metadataStore = metadataStore;
    private readonly ILogger<RecoveryService> _logger = logger;

    public async Task<IReadOnlyList<ResumeCandidate>> ScanAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        var candidates = new List<ResumeCandidate>();
        foreach (var targetPath in _metadataStore.EnumerateTargets(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = await _metadataStore.TryLoadAsync(targetPath, cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                LogSkipped(targetPath);
                continue;
            }

            candidates.Add(new ResumeCandidate(targetPath, new Uri(metadata.OriginalUrl), metadata));
        }

        LogScanned(directory, candidates.Count);
        return candidates;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovery scan of {Directory} found {Count} resumable download(s).")]
    private partial void LogScanned(string directory, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovery skipped {TargetPath}: metadata missing or unreadable.")]
    private partial void LogSkipped(string targetPath);
}