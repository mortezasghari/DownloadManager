using System.Net;
using System.Net.Http.Headers;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Core.Http;

/// <summary>
/// Probes a resource for genuine range support and re-validates it before resume (spec §3/§6d/§7).
/// Range support is confirmed by an actual <c>206</c> to a <c>bytes=0-0</c> request — never trusted
/// from <c>Accept-Ranges</c> alone. Redirects are followed manually (the handler has auto-redirect
/// off) and the final URL is reported so it can be persisted.
/// </summary>
public sealed partial class RangeProber(
    HttpClient httpClient,
    HttpOptions httpOptions,
    EngineOptions engineOptions,
    TimeProvider timeProvider,
    ILogger<RangeProber> logger)
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly HttpOptions _httpOptions = httpOptions;
    private readonly EngineOptions _engineOptions = engineOptions;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<RangeProber> _logger = logger;

    public async Task<ProbeResult> ProbeAsync(Uri url, RequestAuthorizer authorizer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(authorizer);

        var current = url;
        for (var redirect = 0; redirect <= _httpOptions.MaxRedirects; redirect++)
        {
            using var request = CreateProbeRequest(current, ifRange: null);
            authorizer.Authorize(request); // same-origin only: stripped automatically on a cross-host redirect
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (TryGetRedirect(response, current, out var next))
            {
                current = next;
                continue;
            }

            var validators = ReadValidators(response);

            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var total = response.Content.Headers.ContentRange?.Length ?? -1;
                var acceptsRanges = total > 0;
                LogProbed(UrlRedaction.Redact(current), response.StatusCode, total, acceptsRanges);
                return new ProbeResult(current, total, acceptsRanges, validators);
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var total = response.Content.Headers.ContentLength ?? -1;
                LogProbed(UrlRedaction.Redact(current), response.StatusCode, total, acceptsRanges: false);
                return new ProbeResult(current, total, AcceptsRanges: false, validators);
            }

            throw HttpErrorClassifier.ForStatus(response.StatusCode);
        }

        throw new PermanentDownloadException($"Exceeded {_httpOptions.MaxRedirects} redirects probing {url}.");
    }

    /// <summary>
    /// Sends a <c>bytes=0-0</c> request with <c>If-Range: validator</c>. A <c>206</c> whose
    /// <c>Content-Range</c> total still matches means the resource is unchanged and resume is safe; a
    /// <c>200</c> means the precondition failed (changed/ignored). With no usable validator, resume is
    /// unsafe and we report changed so the caller restarts (spec §7 default).
    /// </summary>
    public async Task<RevalidationResult> RevalidateAsync(
        Uri finalUrl, ResourceValidators validators, long expectedSize, RequestAuthorizer authorizer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(finalUrl);
        ArgumentNullException.ThrowIfNull(authorizer);

        var ifRange = validators.ToIfRangeHeaderValue();
        if (ifRange is null)
        {
            LogRevalidated(UrlRedaction.Redact(finalUrl), unchanged: false, "no usable validator");
            return new RevalidationResult(Unchanged: false);
        }

        using var request = CreateProbeRequest(finalUrl, ifRange);
        authorizer.Authorize(request);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        // An auth failure on resume is "needs credentials", not "resource changed" — fail loudly *before*
        // any discard so partial progress survives a stale-credential resume (ADR-0011).
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            LogRevalidated(UrlRedaction.Redact(finalUrl), unchanged: false, $"status {(int)response.StatusCode}");
            throw HttpErrorClassifier.ForStatus(response.StatusCode);
        }

        // 206 + matching total size => server honoured the precondition: unchanged.
        var unchanged =
            response.StatusCode == HttpStatusCode.PartialContent
            && (response.Content.Headers.ContentRange?.Length ?? -1) == expectedSize;

        LogRevalidated(UrlRedaction.Redact(finalUrl), unchanged, $"status {(int)response.StatusCode}");
        return new RevalidationResult(unchanged);
    }

    private HttpRequestMessage CreateProbeRequest(Uri url, string? ifRange)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Ranged requests must be on the identity encoding so Content-Length / ranges refer to the
        // raw bytes, not a gzip/br-encoded stream (spec §3).
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        request.Headers.Range = new RangeHeaderValue(0, 0);

        if (ifRange is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Range", ifRange);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_engineOptions.PerAttemptTimeout, _timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientDownloadException("Probe timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new TransientDownloadException("Probe transport error.", ex);
        }
    }

    private static bool TryGetRedirect(HttpResponseMessage response, Uri current, out Uri next)
    {
        next = current;
        var code = (int)response.StatusCode;
        if (code is < 300 or >= 400 || response.Headers.Location is null)
        {
            return false;
        }

        next = new Uri(current, response.Headers.Location);
        return true;
    }

    private static ResourceValidators ReadValidators(HttpResponseMessage response) =>
        new(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Probed {Url}: status {Status}, total {TotalSize}, acceptsRanges {AcceptsRanges}.")]
    private partial void LogProbed(string url, HttpStatusCode status, long totalSize, bool acceptsRanges);

    [LoggerMessage(Level = LogLevel.Information, Message = "Revalidated {Url}: unchanged {Unchanged} ({Detail}).")]
    private partial void LogRevalidated(string url, bool unchanged, string detail);
}