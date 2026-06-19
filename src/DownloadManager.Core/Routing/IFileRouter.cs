namespace DownloadManager.Core.Routing;

/// <summary>
/// Chooses the final on-disk destination for a download at <b>start</b> (ADR-0017), by file extension.
/// Pure and synchronous — preallocation and positioned writes need the final path before any bytes
/// arrive, so routing cannot depend on response bytes (no content sniffing). This only selects the
/// directory/path and ensures it exists; the engine's write/durability path is unchanged.
/// </summary>
public interface IFileRouter
{
    /// <summary>
    /// Resolve where <paramref name="fileName"/> should land.
    /// <list type="number">
    /// <item>A non-empty <paramref name="explicitPath"/> wins — routing never overrides an explicit destination.</item>
    /// <item>Otherwise the extension picks a category folder; unknown/extensionless → the catch-all root.</item>
    /// <item>The target folder is created if absent, and a filename collision is auto-renamed
    /// <c>name (1).ext</c>, <c>name (2).ext</c>, … (never overwritten).</item>
    /// </list>
    /// </summary>
    string ResolveDestination(string fileName, string? explicitPath = null);
}