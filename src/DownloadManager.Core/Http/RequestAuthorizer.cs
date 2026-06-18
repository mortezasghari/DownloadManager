using DownloadManager.Core.Domain;

namespace DownloadManager.Core.Http;

/// <summary>
/// Applies per-download credentials (<see cref="DownloadCredentials"/>) to outgoing requests, enforcing
/// the cross-host redirect-safety default (spec Phase 4 / ADR-0011): credentials are bound to the
/// <b>origin</b> (scheme + host + port) of the URL the caller attached them to, and are attached to a
/// request only when its target is that same origin. A redirect (followed manually, since the handler
/// has auto-redirect off) to a different scheme/host/port therefore travels with <b>no</b>
/// <c>Authorization</c> or <c>Cookie</c> header — preventing credential leakage to a redirect target.
/// </summary>
public sealed class RequestAuthorizer(DownloadCredentials credentials, Uri origin)
{
    private readonly DownloadCredentials _credentials = credentials;
    private readonly Uri _origin = origin;

    /// <summary>
    /// Attaches the bound credentials to <paramref name="request"/> iff its <see cref="HttpRequestMessage.RequestUri"/>
    /// is same-origin with the credential origin. A no-op when there are no credentials or the target is
    /// cross-origin (the strip-on-cross-host rule).
    /// </summary>
    public void Authorize(HttpRequestMessage request)
    {
        if (_credentials.IsEmpty)
        {
            return;
        }

        var target = request.RequestUri;
        if (target is null || !IsSameOrigin(_origin, target))
        {
            return;
        }

        foreach (var authorization in _credentials.AuthorizationHeaders)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (_credentials.Cookies.Count > 0)
        {
            // RFC 6265: a single Cookie header carries all pairs, separated by "; ".
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _credentials.Cookies));
        }
    }

    /// <summary>Same origin = same scheme, host, and port (the credential-scope boundary).</summary>
    public static bool IsSameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}