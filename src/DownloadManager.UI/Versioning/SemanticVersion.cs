namespace DownloadManager.UI.Versioning;

/// <summary>
/// A <c>major.minor.build</c> version (ADR-0025). Parses the forms the app meets — a release tag
/// (<c>v1.4.5</c>), an injected assembly InformationalVersion (which may carry a <c>+sha</c> or
/// <c>-pre</c> suffix), or a bare <c>1.4.5</c> — and compares by precedence. Pure BCL, AOT-safe.
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Build) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        // Drop any build-metadata / pre-release / whitespace suffix (e.g. "1.4.5+abc", "1.4.5-pre").
        var cut = s.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0)
        {
            s = s[..cut];
        }

        var parts = s.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return false;
        }

        var build = 0;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out build))
        {
            return false;
        }

        if (major < 0 || minor < 0 || build < 0)
        {
            return false;
        }

        version = new SemanticVersion(major, minor, build);
        return true;
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid version '{text}'.");

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Build.CompareTo(other.Build);
    }

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Build}";
}