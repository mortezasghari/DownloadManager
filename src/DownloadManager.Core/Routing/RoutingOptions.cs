using DownloadManager.Core.Configuration;

namespace DownloadManager.Core.Routing;

/// <summary>
/// Resolved, ready-to-use routing map (ADR-0017): every configured extension flattened to an
/// <b>absolute</b> destination folder, plus the absolute catch-all folder for unknown/extensionless
/// downloads. Built once from <see cref="RoutingSettings"/> against a base directory (the user profile),
/// so <see cref="FileRouter"/> does no path resolution per download.
/// </summary>
public sealed class RoutingOptions
{
    private readonly IReadOnlyDictionary<string, string> _folderByExtension;

    // Every distinct destination folder, canonicalized — the allowed roots an explicit path is contained to.
    private readonly string[] _destinationRoots;

    private RoutingOptions(IReadOnlyDictionary<string, string> folderByExtension, string unknownFolder)
    {
        _folderByExtension = folderByExtension;
        UnknownFolder = unknownFolder;
        _destinationRoots = folderByExtension.Values
            .Append(unknownFolder)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Absolute catch-all folder for unknown extensions / extensionless filenames.</summary>
    public string UnknownFolder { get; }

    /// <summary>The absolute destination folder for <paramref name="extension"/> (no dot), or null if unmapped.</summary>
    public string? FolderForExtension(string extension) =>
        _folderByExtension.TryGetValue(extension, out var folder) ? folder : null;

    /// <summary>
    /// Whether <paramref name="fullPath"/> (already canonicalized) is inside one of the configured
    /// destination folders — the containment check the router applies to an explicit per-download path
    /// (ADR-0020). The router only ever returns paths within its known destinations.
    /// </summary>
    public bool IsWithinDestination(string fullPath)
    {
        foreach (var root in _destinationRoots)
        {
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolve a <see cref="RoutingSettings"/> against <paramref name="baseDirectory"/> (the user profile):
    /// relative category folders are combined onto the base, rooted folders are used verbatim. Extensions
    /// are normalized to lower-case, dot-stripped; later categories win on a duplicate extension.
    /// </summary>
    public static RoutingOptions FromSettings(RoutingSettings settings, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, category) in settings.Categories)
        {
            if (category is null || string.IsNullOrWhiteSpace(category.Folder))
            {
                continue;
            }

            var folder = Resolve(baseDirectory, category.Folder);
            foreach (var extension in category.Extensions)
            {
                var normalized = Normalize(extension);
                if (normalized.Length > 0)
                {
                    map[normalized] = folder;
                }
            }
        }

        var unknown = Resolve(baseDirectory, string.IsNullOrWhiteSpace(settings.UnknownFolder) ? "Downloads" : settings.UnknownFolder);
        return new RoutingOptions(map, unknown);
    }

    private static string Resolve(string baseDirectory, string folder) =>
        Path.IsPathRooted(folder) ? folder : Path.Combine(baseDirectory, folder);

    private static string Normalize(string extension) =>
        extension.TrimStart('.').Trim().ToLowerInvariant();
}