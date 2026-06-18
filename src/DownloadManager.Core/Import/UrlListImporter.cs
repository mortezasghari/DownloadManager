namespace DownloadManager.Core.Import;

/// <summary>One line that was rejected during import, with a human-readable reason (spec Phase 4).</summary>
/// <param name="LineNumber">1-based line number in the source list.</param>
/// <param name="RawLine">The original line text (trimmed of surrounding whitespace).</param>
/// <param name="Reason">Why it was skipped (malformed, unsupported scheme, duplicate).</param>
public sealed record UrlImportSkip(int LineNumber, string RawLine, string Reason);

/// <summary>
/// Result of importing a URL list: the accepted URLs (in first-seen order, deduped) plus a per-line
/// reason for every skip. A summary, never an all-or-nothing throw.
/// </summary>
public sealed record UrlImportResult(IReadOnlyList<Uri> Urls, IReadOnlyList<UrlImportSkip> Skipped)
{
    public int ImportedCount => Urls.Count;

    public int SkippedCount => Skipped.Count;
}

/// <summary>
/// Imports a list file of download URLs (spec Phase 4): one URL per line; blank lines and <c>#</c>
/// comments are ignored; only <c>http</c>/<c>https</c> are accepted; duplicates are deduped. A
/// malformed or rejected line is <b>skipped with a recorded reason</b> — one bad line never aborts the
/// whole import. Pure BCL, no dependencies.
/// </summary>
public static class UrlListImporter
{
    /// <summary>Reads and parses a list file. The file is read as UTF-8 text, split into lines.</summary>
    public static async Task<UrlImportResult> ImportFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(lines);
    }

    /// <summary>Parses raw list text (any newline convention) into an import result.</summary>
    public static UrlImportResult ParseText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(text.Split('\n'));
    }

    /// <summary>Parses already-split lines into an import result (the testable core).</summary>
    public static UrlImportResult Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var urls = new List<Uri>();
        var skipped = new List<UrlImportSkip>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            // Tolerate trailing '\r' from CRLF input and surrounding whitespace.
            var line = rawLine.Trim();

            // Blank lines and '#' comments are ignored entirely — not counted as skips.
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (!Uri.TryCreate(line, UriKind.Absolute, out var uri))
            {
                skipped.Add(new UrlImportSkip(lineNumber, line, "malformed URL"));
                continue;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                skipped.Add(new UrlImportSkip(lineNumber, line, $"unsupported scheme '{uri.Scheme}'"));
                continue;
            }

            // Dedupe on the canonical absolute form so trivially equal URLs collapse to one.
            if (!seen.Add(uri.AbsoluteUri))
            {
                skipped.Add(new UrlImportSkip(lineNumber, line, "duplicate URL"));
                continue;
            }

            urls.Add(uri);
        }

        return new UrlImportResult(urls, skipped);
    }
}