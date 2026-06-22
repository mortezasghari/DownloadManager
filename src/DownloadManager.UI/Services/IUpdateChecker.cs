using DownloadManager.UI.Versioning;

namespace DownloadManager.UI.Services;

/// <summary>An available update: the running version, the newer published version, and its release page URL.</summary>
public readonly record struct UpdateInfo(SemanticVersion Current, SemanticVersion Latest, string ReleaseUrl);

/// <summary>
/// NOTIFY-ONLY update check (ADR-0025): compares the running version against the latest published release
/// and, if newer, returns where to view it. It <b>only fetches release metadata and compares two numbers</b>
/// — it never downloads, verifies, stages, or installs anything (that is the separate, parked, secured
/// updater phase, so this has no RCE surface). Behind a seam so it is headless-testable; failures are a
/// silent no-op (null), never a crash.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>The latest release if it is newer than the running version; otherwise null (up to date, or
    /// the check failed/was offline — both indistinguishable to the caller by design).</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default);
}