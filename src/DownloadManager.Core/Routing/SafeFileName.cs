using System.Globalization;
using System.Text;

namespace DownloadManager.Core.Routing;

/// <summary>
/// Reduces an untrusted server/URL-derived name to a safe filesystem <b>leaf</b> name (ADR-0020, audit
/// F1/F8). This is the by-design sanitization the router applies before a name ever becomes a path — it
/// must hold regardless of the caller, so a future source (e.g. <c>Content-Disposition</c>) cannot feed a
/// traversal straight through. Pure BCL, AOT-safe, no I/O.
/// </summary>
public static class SafeFileName
{
    /// <summary>Used whenever sanitization would otherwise yield an empty, all-dots, or unusable name.</summary>
    public const string Fallback = "download";

    // Windows reserved device names (also reserved with any extension, e.g. CON.txt). Case-insensitive.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Turn <paramref name="name"/> into a single safe filename: collapse any directory component, drop
    /// separators / drive-colon (ADS) / Windows-illegal chars / control + bidi-format chars, trim trailing
    /// dots and spaces, neutralize reserved device names, and fall back to <see cref="Fallback"/> for an
    /// empty or all-dots result. The output contains no path separator, so combining it onto a folder can
    /// never escape that folder.
    /// </summary>
    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fallback;
        }

        // Collapse any directory component, host-independently (both '/' and '\', not the host-only set
        // Path.GetFileName would use). "../../etc/passwd" and "a\..\..\b" both reduce to their last segment.
        var lastSeparator = name.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (IsStrippable(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        // Windows strips trailing dots/spaces — doing it here removes the bypass and the "..", "." cases.
        var cleaned = builder.ToString().TrimEnd(' ', '.');
        if (cleaned.Length == 0)
        {
            return Fallback;
        }

        // CON / NUL / COM1 … (with or without extension) → prefix so the device name is no longer matched.
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedDeviceNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    /// <summary>
    /// Strip Unicode bidi-control and control/format characters from a name for <b>display</b> (audit F3):
    /// e.g. a right-to-left override that renders <c>exe.jpg</c> while the bytes are <c>gpj.exe</c>. Does
    /// not otherwise alter the name.
    /// </summary>
    public static string StripBidiControls(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name ?? string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (!IsControlOrBidi(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsStrippable(char ch) =>
        IsControlOrBidi(ch)
        // Path separators, drive/ADS colon, and the Windows-illegal set.
        || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';

    private static bool IsControlOrBidi(char ch) =>
        // C0/C1 controls, plus every Unicode "Format" (Cf) char — which covers the bidi controls,
        // isolates, and marks (U+202A–202E, U+2066–2069, U+200E/200F, U+061C, …) without embedding any
        // invisible literal in this source file.
        char.IsControl(ch) || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format;
}