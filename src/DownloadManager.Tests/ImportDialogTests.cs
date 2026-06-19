using DownloadManager.Tests.Fakes;
using DownloadManager.UI.ViewModels;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 6: import-review dialog VM (headless). Raw text / clipboard → checklist via the unchanged
/// UrlListImporter; only ticked valid URLs enqueue via the normal add-path; lenient partial success.
/// </summary>
public class ImportDialogTests
{
    private static (ImportDialogViewModel Vm, List<Uri> Added) New(string? clipboard = null)
    {
        var added = new List<Uri>();
        var vm = new ImportDialogViewModel(
            new FakeClipboardTextSource(clipboard),
            urls => { added.AddRange(urls); return Task.CompletedTask; });
        return (vm, added);
    }

    [Fact]
    public async Task Two_valid_https_urls_both_enqueue()
    {
        var (vm, added) = New();
        vm.RawText = "https://example.test/a.bin\nhttps://example.test/b.bin";

        Assert.Equal(2, vm.Candidates.Count);
        Assert.All(vm.Candidates, c => Assert.True(c.IsSelected));

        await vm.AddSelectedToQueueAsync();

        Assert.Equal(
            ["https://example.test/a.bin", "https://example.test/b.bin"],
            added.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task Mixed_valid_and_junk_enqueues_valid_skips_junk_with_reason_and_dedupes()
    {
        var (vm, added) = New();
        vm.RawText =
            """
            https://example.test/a
            ftp://nope.test/x
            https://example.test/a
            not a url
            https://example.test/b
            """;

        // Only the 2 distinct valid URLs become candidates.
        Assert.Equal(2, vm.Candidates.Count);
        Assert.Contains("skipped 3", vm.Summary);
        Assert.Contains("unsupported scheme", vm.Summary);
        Assert.Contains("malformed URL", vm.Summary);
        Assert.Contains("duplicate URL", vm.Summary);

        await vm.AddSelectedToQueueAsync();
        Assert.Equal(["https://example.test/a", "https://example.test/b"], added.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task Only_ticked_items_enqueue()
    {
        var (vm, added) = New();
        vm.RawText = "https://example.test/a\nhttps://example.test/b\nhttps://example.test/c";
        Assert.Equal(3, vm.Candidates.Count);

        vm.Candidates[1].IsSelected = false; // untick the middle one

        await vm.AddSelectedToQueueAsync();
        Assert.Equal(["https://example.test/a", "https://example.test/c"], added.Select(u => u.AbsoluteUri));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t  ")]
    public async Task Empty_or_whitespace_or_nontext_clipboard_is_a_noop_with_no_urls_found(string? clipboard)
    {
        var (vm, added) = New(clipboard);

        await vm.LoadFromClipboardAsync(); // auto-paste path; non-text clipboard surfaces as null too

        Assert.Empty(vm.Candidates);
        Assert.Equal("No URLs found.", vm.Summary);

        await vm.AddSelectedToQueueAsync(); // nothing ticked → no enqueue, no throw
        Assert.Empty(added);
    }

    [Fact]
    public async Task Clipboard_auto_paste_populates_the_checklist()
    {
        var (vm, added) = New("https://example.test/clip1\n# a comment\nhttps://example.test/clip2");

        await vm.LoadFromClipboardAsync();

        Assert.Equal(2, vm.Candidates.Count);
        await vm.AddSelectedToQueueAsync();
        Assert.Equal(2, added.Count);
    }
}