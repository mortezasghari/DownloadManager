using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Http;
using DownloadManager.Core.Routing;
using DownloadManager.Persistence.Io;
using DownloadManager.Persistence.Metadata;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.Services;
using DownloadManager.UI.ViewModels;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Permanent regression tests for the security hardening (ADR-0020), each built from the audit's proven
/// exploit so the closed hole stays closed. Headless, no Avalonia render.
/// </summary>
public sealed class SecurityHardeningTests
{
    // ---------- F1/F8: leaf-name sanitization + router containment ----------

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("/etc/cron.d/x", "x")]
    [InlineData("foo/bar/../../baz", "baz")]
    [InlineData(@"..\..\Windows\System32\drivers\etc\hosts", "hosts")]
    [InlineData(@"\\server\share\evil.bin", "evil.bin")]
    [InlineData("a/b/c.txt", "c.txt")]
    public void Sanitize_reduces_any_path_to_a_bare_leaf(string hostile, string expectedLeaf)
    {
        var safe = SafeFileName.Sanitize(hostile);

        Assert.Equal(expectedLeaf, safe);
        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain("..", safe);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData(".")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData("\u202E\u202E")] // only bidi-override chars → stripped to empty → fallback
    public void Sanitize_falls_back_for_empty_dotted_or_all_stripped_names(string hostile)
    {
        Assert.Equal(SafeFileName.Fallback, SafeFileName.Sanitize(hostile));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9.txt")]
    public void Sanitize_neutralizes_windows_reserved_device_names(string reserved)
    {
        var safe = SafeFileName.Sanitize(reserved);

        // Prefixed so the device name no longer matches; still a usable in-dir leaf.
        Assert.StartsWith("_", safe);
    }

    [Fact]
    public void Sanitize_strips_ads_colon_and_windows_illegal_chars_and_trailing_dot_space()
    {
        Assert.DoesNotContain(':', SafeFileName.Sanitize("file.txt:evil"));      // NTFS ADS
        Assert.Equal("file.txtevil", SafeFileName.Sanitize("file.txt:evil"));
        Assert.Equal("name", SafeFileName.Sanitize("name. . "));                  // trailing dots/spaces
        Assert.DoesNotContain('|', SafeFileName.Sanitize("a|b?c*d"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/cron.d/x")]
    [InlineData("..")]
    [InlineData(@"..\..\win.ini")]
    [InlineData("file.txt:stream")]
    [InlineData("CON")]
    public void Router_output_always_stays_inside_a_destination_folder(string hostileName)
    {
        using var home = new TempDir();
        var router = new FileRouter(RoutingOptions.FromSettings(RoutingSettings.CreateDefault(), home.Path));

        var resolved = router.ResolveDestination(hostileName);

        var full = Path.GetFullPath(resolved);
        Assert.StartsWith(Path.GetFullPath(home.Path) + Path.DirectorySeparatorChar, full, StringComparison.Ordinal);
    }

    // ---------- F4: URL userinfo redaction ----------

    [Theory]
    [InlineData("https://alice:s3cret@host.example/file.bin", "s3cret")]
    [InlineData("https://token@host.example/a/b.bin", "token")]
    public void Redaction_removes_userinfo_but_keeps_host_and_path(string raw, string secret)
    {
        var redacted = UrlRedaction.Redact(new Uri(raw));

        Assert.DoesNotContain(secret, redacted);
        Assert.DoesNotContain("@", redacted);
        Assert.Contains("host.example", redacted);
        Assert.EndsWith(new Uri(raw).PathAndQuery, redacted); // scheme/host/path preserved, only userinfo gone
    }

    [Fact]
    public void Redaction_leaves_a_clean_url_untouched()
    {
        const string clean = "https://host.example/file.bin?x=1";
        Assert.Equal(clean, UrlRedaction.Redact(new Uri(clean)));
    }

    [Fact]
    public void Persisted_metadata_contains_no_userinfo_secret_but_request_keeps_it_for_the_wire()
    {
        var url = new Uri("https://alice:s3cret@host.example/file.bin");

        // What the engine persists is the redacted form; serialize it the same way JsonMetadataStore does.
        var metadata = new DownloadMetadata
        {
            OriginalUrl = UrlRedaction.Redact(url),
            FinalUrl = UrlRedaction.Redact(url),
            TotalSize = 1,
            AcceptsRanges = false,
            Segments = [SegmentRange.Create(0, 0)],
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(metadata, MetadataJsonContext.Default.DownloadMetadata);

        Assert.DoesNotContain("s3cret", json);
        Assert.DoesNotContain("alice", json);
        // The live request URL is untouched, so auth still travels on the wire.
        Assert.Equal("alice:s3cret", url.UserInfo);
    }

    // ---------- F6: preallocation clamp ----------

    [Fact]
    public async Task Full_preallocation_above_the_cap_falls_back_to_sparse_without_reserving()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "big.bin");
        var fullAttempted = false;
        var factory = new TargetFileFactory(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TargetFileFactory>.Instance,
            (SafeFileHandle _, long _) => { fullAttempted = true; return true; });

        const long size = 8L * 1024 * 1024 * 1024; // 8 GiB claimed
        await using (factory.Open(path, size, PreallocationMode.Full, maxFullPreallocationBytes: 64 * 1024 * 1024))
        {
        }

        Assert.False(fullAttempted);                       // Full reservation was NOT attempted
        Assert.Equal(size, new FileInfo(path).Length);     // sparse: logical length set, no real reservation
    }

    [Fact]
    public async Task Full_preallocation_within_the_cap_still_runs_the_full_path()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "small.bin");
        var fullAttempted = false;
        var factory = new TargetFileFactory(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TargetFileFactory>.Instance,
            (SafeFileHandle _, long _) => { fullAttempted = true; return true; });

        await using (factory.Open(path, 1024, PreallocationMode.Full, maxFullPreallocationBytes: 64 * 1024 * 1024))
        {
        }

        Assert.True(fullAttempted); // under the cap → Full still used
    }

    // ---------- F5: bidi-strip on displayed names ----------

    [Fact]
    public void StripBidiControls_removes_rtl_override_and_other_format_chars()
    {
        // "photo\u202Egpj.exe" renders as "photoexe.jpg" but is really …gpj.exe.
        var stripped = SafeFileName.StripBidiControls("photo\u202Egpj.exe");

        Assert.Equal("photogpj.exe", stripped);
        Assert.DoesNotContain('\u202E', stripped);
    }

    [Fact]
    public void History_row_display_name_has_bidi_controls_stripped()
    {
        var record = HistoryRecord.From(
            DownloadId.New(), "clip\u202Egpj.exe", 1, HistoryState.Completed, "/d/clip.exe");
        var item = new HistoryItemViewModel(record, new FakeFileLauncher(), _ => { });

        Assert.Equal("clipgpj.exe", item.Name);
        Assert.DoesNotContain('\u202E', item.Name);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dlm-sec", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}