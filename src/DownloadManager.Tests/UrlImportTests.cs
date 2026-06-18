using DownloadManager.Core.Import;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>Phase 4: URL list import — http/https only, dedupe, skip-with-reason, partial-failure summary.</summary>
public class UrlImportTests
{
    [Fact]
    public void Valid_list_imports_in_order_with_blank_and_comment_lines_ignored()
    {
        var result = UrlListImporter.ParseText(
            """
            # a comment line
            https://example.test/a.bin

            http://example.test/b.bin
              https://example.test/c.bin
            # trailing comment
            """);

        Assert.Equal(3, result.ImportedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(
            ["https://example.test/a.bin", "http://example.test/b.bin", "https://example.test/c.bin"],
            result.Urls.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void Non_http_schemes_are_rejected_with_a_reason()
    {
        var result = UrlListImporter.ParseText(
            """
            https://ok.test/file
            ftp://nope.test/file
            file:///etc/passwd
            """);

        Assert.Single(result.Urls);
        Assert.Equal(2, result.SkippedCount);
        Assert.Contains(result.Skipped, s => s.RawLine.StartsWith("ftp:") && s.Reason.Contains("scheme"));
        Assert.Contains(result.Skipped, s => s.RawLine.StartsWith("file:") && s.Reason.Contains("scheme"));
    }

    [Fact]
    public void Malformed_line_is_skipped_with_reason_without_aborting_the_import()
    {
        var result = UrlListImporter.ParseText(
            """
            https://good.test/1
            this is not a url
            https://good.test/2
            """);

        Assert.Equal(2, result.ImportedCount);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(2, skip.LineNumber);
        Assert.Equal("malformed URL", skip.Reason);
    }

    [Fact]
    public void Duplicates_are_deduped_and_recorded_as_skips()
    {
        var result = UrlListImporter.ParseText(
            """
            https://example.test/dup
            https://example.test/other
            https://example.test/dup
            """);

        Assert.Equal(2, result.ImportedCount);
        var dup = Assert.Single(result.Skipped);
        Assert.Equal("duplicate URL", dup.Reason);
        Assert.Equal(3, dup.LineNumber);
    }

    [Fact]
    public void Summary_counts_are_correct_for_a_mixed_partial_failure_list()
    {
        var result = UrlListImporter.ParseText(
            """
            https://example.test/a

            # comment
            ftp://example.test/b
            not-a-url
            https://example.test/a
            http://example.test/c
            """);

        // Imported: a, c. Skipped: ftp (scheme), not-a-url (malformed), duplicate a.
        Assert.Equal(2, result.ImportedCount);
        Assert.Equal(3, result.SkippedCount);
        Assert.Equal(
            ["unsupported scheme 'ftp'", "malformed URL", "duplicate URL"],
            result.Skipped.Select(s => s.Reason));
    }
}