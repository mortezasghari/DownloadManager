namespace DownloadManager.Core.Http;

/// <summary>
/// Strips userinfo (<c>user:pass@</c>) from a URL for any <b>persisted or logged</b> representation
/// (ADR-0020, audit F4) — so a credential embedded in a download URL never lands in <c>.dlmeta</c> or the
/// logs in cleartext. The on-the-wire request keeps the original URL; only the recorded string is
/// redacted (and .NET does not transmit userinfo as auth anyway — credentials travel via
/// <c>DownloadCredentials</c>). Pure BCL, AOT-safe.
/// </summary>
public static class UrlRedaction
{
    /// <summary>The URL as a string with any userinfo removed; scheme/host/port/path/query preserved.</summary>
    public static string Redact(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri || string.IsNullOrEmpty(url.UserInfo))
        {
            return url.ToString();
        }

        // HttpRequestUrl = scheme + host + port + path + query, and explicitly excludes UserInfo (and the
        // fragment, which HTTP never sends anyway).
        return url.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
    }

    /// <summary>String overload: redacts when parseable as an absolute URI, else returns the input unchanged.</summary>
    public static string Redact(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? Redact(parsed) : url;
}