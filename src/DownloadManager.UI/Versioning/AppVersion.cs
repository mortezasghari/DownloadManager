using System.Reflection;

namespace DownloadManager.UI.Versioning;

/// <summary>
/// Reads the running app's own version from the assembly (ADR-0025) — the version CI injected at build via
/// MSBuild <c>Version</c>/<c>InformationalVersion</c>. No version string is hardcoded anywhere, so the
/// displayed/compared version can never drift from the published binary. Pure BCL, AOT-safe.
/// </summary>
public static class AppVersion
{
    /// <summary>The current version, read from <paramref name="assembly"/> (default: the entry assembly).</summary>
    public static SemanticVersion Current(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        return FromAttributes(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);
    }

    /// <summary>
    /// The version-reading rule, separated from the <see cref="Assembly"/> lookup so it is directly
    /// testable: prefer the (clean) InformationalVersion CI injected; fall back to the assembly version.
    /// </summary>
    public static SemanticVersion FromAttributes(string? informationalVersion, Version? assemblyVersion)
    {
        if (SemanticVersion.TryParse(informationalVersion, out var fromInformational))
        {
            return fromInformational;
        }

        return assemblyVersion is null
            ? new SemanticVersion(0, 0, 0)
            : new SemanticVersion(assemblyVersion.Major, assemblyVersion.Minor, Math.Max(0, assemblyVersion.Build));
    }

    public static string CurrentString(Assembly? assembly = null) => Current(assembly).ToString();
}