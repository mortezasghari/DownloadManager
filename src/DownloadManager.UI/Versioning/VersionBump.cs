namespace DownloadManager.UI.Versioning;

/// <summary>Which component a merged PR's label bumps (ADR-0025). The default — no/unknown label — is a build bump.</summary>
public enum BumpKind
{
    Build,
    Minor,
    Major,
}

/// <summary>
/// Calculated, never-hand-typed version bump (ADR-0025). The bump intent comes from a <b>PR label</b> only
/// (never a commit-message keyword): <c>major</c> → next major, reset minor+build; <c>minor</c> → next
/// minor, reset build; no/unknown label → increment build. Pure function — CI invokes it (via the app's
/// <c>--next-version</c> mode) on the latest release tag, and the unit tests lock the rules.
/// </summary>
public static class VersionBump
{
    public static BumpKind FromLabel(string? label) => label?.Trim().ToLowerInvariant() switch
    {
        "major" => BumpKind.Major,
        "minor" => BumpKind.Minor,
        _ => BumpKind.Build, // no label (default) or any other label → build increment
    };

    public static SemanticVersion Next(SemanticVersion current, BumpKind kind) => kind switch
    {
        BumpKind.Major => new SemanticVersion(current.Major + 1, 0, 0),
        BumpKind.Minor => new SemanticVersion(current.Major, current.Minor + 1, 0),
        _ => new SemanticVersion(current.Major, current.Minor, current.Build + 1),
    };

    /// <summary>
    /// Compute the next version from the latest release tag/version (anything unparseable → <c>0.0.0</c>)
    /// and a PR label. This is the exact logic CI runs to pick the version to build and release.
    /// </summary>
    public static SemanticVersion Next(string? currentTagOrVersion, string? label)
    {
        var current = SemanticVersion.TryParse(currentTagOrVersion, out var parsed) ? parsed : new SemanticVersion(0, 0, 0);
        return Next(current, FromLabel(label));
    }
}