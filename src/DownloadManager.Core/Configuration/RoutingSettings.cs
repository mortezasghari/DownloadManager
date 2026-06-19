namespace DownloadManager.Core.Configuration;

/// <summary>
/// IDM-style extension routing config (ADR-0017). Each category maps a set of file extensions to a
/// destination folder; folders are relative to the user profile unless rooted. The
/// container-vs-content distinction is encoded in the <i>defaults</i>: terminal content types
/// (video/audio/documents/pictures) route to the semantic user folder, while containers
/// (archives/executables) route to neutral dedicated <c>Downloads</c> subfolders, because their
/// extension does not tell you the contained content.
/// </summary>
public sealed class RoutingSettings
{
    /// <summary>Category name → its extensions + destination folder. User-extensible.</summary>
    public Dictionary<string, RoutingCategory> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Catch-all folder for unknown / extensionless downloads (relative to user profile).</summary>
    public string UnknownFolder { get; set; } = "Downloads";

    /// <summary>Seed defaults with platform-correct folders (macOS video → Movies, not Videos).</summary>
    public static RoutingSettings CreateDefault()
    {
        var video = OperatingSystem.IsMacOS() ? "Movies" : "Videos";
        return new RoutingSettings
        {
            UnknownFolder = "Downloads",
            Categories = new Dictionary<string, RoutingCategory>(StringComparer.OrdinalIgnoreCase)
            {
                ["video"] = new()
                {
                    Folder = video,
                    Extensions = ["mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg"],
                },
                ["audio"] = new()
                {
                    Folder = "Music",
                    Extensions = ["mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus"],
                },
                ["documents"] = new()
                {
                    Folder = "Documents",
                    Extensions = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "odt", "epub", "csv"],
                },
                ["pictures"] = new()
                {
                    Folder = "Pictures",
                    Extensions = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "tiff", "heic", "svg"],
                },
                // Containers → neutral dedicated subfolders, NOT a guessed semantic folder (ADR-0017).
                ["archives"] = new()
                {
                    Folder = Path.Combine("Downloads", "Archives"),
                    Extensions = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz"],
                },
                ["executables"] = new()
                {
                    Folder = Path.Combine("Downloads", "Programs"),
                    Extensions = ["exe", "msi", "dmg", "pkg", "deb", "rpm", "appimage"],
                },
            },
        };
    }
}

/// <summary>One routing category: the extensions it claims and where matching files go.</summary>
public sealed class RoutingCategory
{
    /// <summary>Extensions (no leading dot, case-insensitive) that map to <see cref="Folder"/>.</summary>
    public string[] Extensions { get; set; } = [];

    /// <summary>Destination folder; relative paths resolve against the user profile, rooted paths are used as-is.</summary>
    public string Folder { get; set; } = "";
}