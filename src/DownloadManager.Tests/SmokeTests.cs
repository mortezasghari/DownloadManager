using DownloadManager.Core;
using DownloadManager.Persistence;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 0 smoke tests: prove the test harness runs and the Core/Persistence
/// project references resolve. Real coverage (spec §12) arrives with the engine.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void AppInfo_describes_the_running_framework()
    {
        var info = new AppInfoService().Describe();
        Assert.Contains(".NET", info);
    }

    [Theory]
    [InlineData("/data/movie.mkv", "/data/movie.mkv.dlmeta", "/data/movie.mkv.dllog")]
    [InlineData("relative.bin", "relative.bin.dlmeta", "relative.bin.dllog")]
    public void Persistence_sidecar_paths_sit_beside_the_target(
        string target, string expectedMeta, string expectedLog)
    {
        Assert.Equal(expectedMeta, PersistencePaths.MetadataPath(target));
        Assert.Equal(expectedLog, PersistencePaths.ProgressLogPath(target));
    }
}
