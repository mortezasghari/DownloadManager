namespace DownloadManager.Core.Abstractions;

/// <summary>
/// Minimal application-information seam used by the Phase 0 AOT shell to prove
/// the UI -> Core interface boundary works under Native AOT. The download
/// engine abstractions land here in later phases.
/// </summary>
public interface IAppInfoService
{
    /// <summary>Human-readable runtime/platform description.</summary>
    string Describe();
}