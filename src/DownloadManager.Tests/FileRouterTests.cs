using DownloadManager.Core.Configuration;
using DownloadManager.Core.Routing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// IDM-style extension routing tests (ADR-0017): extension → category folder resolved at start, with
/// per-platform defaults, container-vs-content placement, folder creation, collision auto-rename, and
/// explicit-path override. A temp directory stands in for the user profile so nothing touches real home.
/// </summary>
public sealed class FileRouterTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "dlm-route-tests", Guid.NewGuid().ToString("N"));

    public FileRouterTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private FileRouter DefaultRouter() =>
        new(RoutingOptions.FromSettings(RoutingSettings.CreateDefault(), _home));

    private string Folder(params string[] parts) => Path.Combine(new[] { _home }.Concat(parts).ToArray());

    [Theory]
    [InlineData("clip.mp4", "audio-or-video")]   // video
    [InlineData("song.flac", "Music")]
    [InlineData("paper.pdf", "Documents")]
    [InlineData("photo.JPG", "Pictures")]        // case-insensitive extension
    public void Terminal_content_types_route_to_their_semantic_folder(string fileName, string expectedFolderName)
    {
        var path = DefaultRouter().ResolveDestination(fileName);

        var expectedFolder = expectedFolderName == "audio-or-video"
            ? Folder(OperatingSystem.IsMacOS() ? "Movies" : "Videos")
            : Folder(expectedFolderName);
        Assert.Equal(Path.Combine(expectedFolder, fileName), path);
        Assert.True(Directory.Exists(expectedFolder)); // folder created on demand
    }

    [Fact]
    public void Default_video_folder_is_movies_on_macos_and_videos_elsewhere()
    {
        var settings = RoutingSettings.CreateDefault();
        var expected = OperatingSystem.IsMacOS() ? "Movies" : "Videos";

        Assert.Equal(expected, settings.Categories["video"].Folder);
    }

    [Fact]
    public void Archives_route_to_a_dedicated_subfolder_of_downloads_created_if_absent()
    {
        var path = DefaultRouter().ResolveDestination("bundle.zip");

        var archives = Folder("Downloads", "Archives");
        Assert.Equal(Path.Combine(archives, "bundle.zip"), path);
        Assert.StartsWith(Folder("Downloads"), archives); // a subfolder of Downloads, not a semantic folder
        Assert.True(Directory.Exists(archives));
    }

    [Fact]
    public void Executables_route_to_a_dedicated_programs_subfolder_of_downloads()
    {
        var path = DefaultRouter().ResolveDestination("setup.exe");

        var programs = Folder("Downloads", "Programs");
        Assert.Equal(Path.Combine(programs, "setup.exe"), path);
        Assert.StartsWith(Folder("Downloads"), programs);
        Assert.True(Directory.Exists(programs));
    }

    [Theory]
    [InlineData("mystery.xyz")] // unknown extension
    [InlineData("README")]      // no extension at all
    public void Unknown_and_extensionless_downloads_go_to_the_downloads_root(string fileName)
    {
        var path = DefaultRouter().ResolveDestination(fileName);

        Assert.Equal(Path.Combine(Folder("Downloads"), fileName), path);
    }

    [Fact]
    public void Explicit_per_download_path_overrides_routing()
    {
        var explicitPath = Path.Combine(_home, "somewhere", "exact.mp4");

        var path = DefaultRouter().ResolveDestination("exact.mp4", explicitPath);

        Assert.Equal(explicitPath, path);
        Assert.False(Directory.Exists(Folder(OperatingSystem.IsMacOS() ? "Movies" : "Videos")));
    }

    [Fact]
    public void Filename_collision_auto_renames_and_never_overwrites()
    {
        var router = DefaultRouter();
        var music = Folder("Music");
        Directory.CreateDirectory(music);

        // Occupy "track.mp3" then "track (1).mp3".
        File.WriteAllText(Path.Combine(music, "track.mp3"), "first");
        Assert.Equal(Path.Combine(music, "track (1).mp3"), router.ResolveDestination("track.mp3"));

        File.WriteAllText(Path.Combine(music, "track (1).mp3"), "second");
        Assert.Equal(Path.Combine(music, "track (2).mp3"), router.ResolveDestination("track.mp3"));
    }

    [Fact]
    public void Extensionless_collision_auto_renames_without_an_extension()
    {
        var router = DefaultRouter();
        var downloads = Folder("Downloads");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "LICENSE"), "x");

        Assert.Equal(Path.Combine(downloads, "LICENSE (1)"), router.ResolveDestination("LICENSE"));
    }

    [Fact]
    public void A_user_added_extension_in_config_routes_to_that_category()
    {
        var settings = RoutingSettings.CreateDefault();
        // User extends "archives" with a custom disk-image extension.
        settings.Categories["archives"].Extensions =
            [.. settings.Categories["archives"].Extensions, "iso"];
        var router = new FileRouter(RoutingOptions.FromSettings(settings, _home));

        var path = router.ResolveDestination("ubuntu.iso");

        Assert.Equal(Path.Combine(Folder("Downloads", "Archives"), "ubuntu.iso"), path);
    }
}