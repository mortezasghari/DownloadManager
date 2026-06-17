using System.Runtime.InteropServices;
using DownloadManager.Core.Abstractions;

namespace DownloadManager.Core;

/// <summary>
/// Default <see cref="IAppInfoService"/>. Uses only <see cref="RuntimeInformation"/>,
/// which is fully AOT-safe (no reflection over user types).
/// </summary>
public sealed class AppInfoService : IAppInfoService
{
    public string Describe() =>
        $"{RuntimeInformation.FrameworkDescription} | RID {RuntimeInformation.RuntimeIdentifier} | {RuntimeInformation.OSArchitecture}";
}