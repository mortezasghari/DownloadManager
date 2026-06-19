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
        // 1. An explicit per-download destination always wins — routing never overrides it.
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // 2/3. Extension → category folder; unknown / extensionless → the catch-all root.
        var extension = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var folder = (extension.Length > 0 ? _options.FolderForExtension(extension) : null) ?? _options.UnknownFolder;

        // 4. Ensure the folder exists (creates Downloads/Archives, Downloads/Programs, … on demand).
        Directory.CreateDirectory(folder);

        // 5. Collision → auto-rename "name (1).ext", never overwrite.
        return Disambiguate(Path.Combine(folder, fileName));
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