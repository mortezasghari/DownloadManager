namespace DownloadManager.Core.Routing;

/// <summary>
/// Default <see cref="IFileRouter"/> (ADR-0017). Extension → category folder, resolved at download start;
/// creates the folder and disambiguates collisions. Uses the real filesystem directly — it only touches
/// directories/paths, never the engine's write or durability path.
/// </summary>
public sealed class FileRouter(RoutingOptions options) : IFileRouter
{
    private readonly RoutingOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public string ResolveDestination(string fileName, string? explicitPath = null)
    {
        // 1. An explicit per-download destination wins — but is still canonicalized and contained by
        // design (ADR-0020): it must resolve inside a configured destination folder, else it is rejected.
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitFull = Path.GetFullPath(explicitPath);
            if (!_options.IsWithinDestination(explicitFull))
            {
                throw new PathContainmentException(
                    $"Explicit destination '{explicitPath}' is outside every configured download folder.");
            }

            return explicitFull;
        }

        // 2. Sanitize the (untrusted) name to a bare leaf BEFORE it touches a path: no separators, no
        // "..", no drive/ADS colon, no reserved device name, no control/bidi chars (audit F1/F8).
        var safeName = SafeFileName.Sanitize(fileName);

        // 3. Extension → category folder; unknown / extensionless → the catch-all root.
        var extension = Path.GetExtension(safeName).TrimStart('.').ToLowerInvariant();
        var folder = (extension.Length > 0 ? _options.FolderForExtension(extension) : null) ?? _options.UnknownFolder;

        // 4. Ensure the folder exists (creates Downloads/Archives, Downloads/Programs, … on demand).
        Directory.CreateDirectory(folder);

        // 5. Canonicalize and VERIFY containment. The sanitizer already guarantees the leaf cannot escape;
        // this is the by-design backstop that makes the chokepoint safe regardless of caller.
        var combined = Path.GetFullPath(Path.Combine(folder, safeName));
        var folderFull = Path.GetFullPath(folder);
        var folderWithSeparator = folderFull.EndsWith(Path.DirectorySeparatorChar)
            ? folderFull
            : folderFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(folderWithSeparator, StringComparison.Ordinal))
        {
            throw new PathContainmentException(
                $"Resolved path '{combined}' escapes the target folder '{folderFull}'.");
        }

        // 6. Collision → auto-rename "name (1).ext", never overwrite.
        return Disambiguate(combined);
    }

    private static string Disambiguate(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path); // includes the dot, or "" when extensionless

        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}