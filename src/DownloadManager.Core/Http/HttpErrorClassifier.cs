using System.Net;
using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Http;

/// <summary>
/// Classifies HTTP status codes into transient vs permanent failures (spec §8). Transient errors are
/// safe to retry with backoff; permanent ones are not. The retry/backoff machinery itself lands in
/// Phase 3 — this just produces correctly-classified exceptions.
/// </summary>
internal static class HttpErrorClassifier
{
    public static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500
        || status is HttpStatusCode.RequestTimeout      // 408
        || status is HttpStatusCode.TooManyRequests;    // 429

    public static DownloadException ForStatus(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var code = (int)status;

        // Auth failures are their own category: not retryable, but fresh credentials may fix them, so
        // they must not discard partial progress (ADR-0011).
        if (status is HttpStatusCode.Unauthorized       // 401
            or HttpStatusCode.Forbidden)                // 403
        {
            return new NeedsCredentialsException($"HTTP {code} {status}.");
        }

        // Clearly permanent: the resource is gone.
        if (status is HttpStatusCode.NotFound           // 404
            or HttpStatusCode.Gone)                     // 410
        {
            return new PermanentDownloadException($"HTTP {code} {status}.");
        }

        return IsTransient(status)
            ? new TransientDownloadException($"HTTP {code} {status}.") { RetryAfter = retryAfter }
            : new PermanentDownloadException($"Unexpected HTTP {code} {status}.");
    }
}