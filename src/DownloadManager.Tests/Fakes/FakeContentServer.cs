using System.Net;
using System.Net.Http.Headers;

namespace DownloadManager.Tests.Fakes;

/// <summary>
/// A scriptable origin server backing a <see cref="FakeHttpMessageHandler"/>. Serves a byte buffer
/// while honouring <c>Range</c> and <c>If-Range</c>, and can simulate the nasty cases from spec §12:
/// dropped connections (<see cref="MaxBytesPerResponse"/>), changed validators, servers that lie
/// about range support, and bogus <c>Content-Range</c> offsets.
/// </summary>
internal sealed class FakeContentServer
{
    public required byte[] Content { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public bool SupportsRanges { get; set; } = true;

    /// <summary>If set, responses carry at most this many body bytes (simulates an early close).</summary>
    public long? MaxBytesPerResponse { get; set; }

    /// <summary>If true, omit Content-Length so the resource looks unknown-size (spec §3).</summary>
    public bool AdvertiseLength { get; set; } = true;

    /// <summary>Offset added to the reported <c>Content-Range</c> start to forge a mismatch.</summary>
    public long ReportedFromDelta { get; set; }

    /// <summary>If set, non-probe (work) requests return this status (e.g. 503) instead of content.</summary>
    public HttpStatusCode? WorkStatusOverride { get; set; }

    /// <summary>Optional <c>Retry-After</c> delta attached to <see cref="WorkStatusOverride"/> responses.</summary>
    public TimeSpan? WorkRetryAfter { get; set; }

    private readonly Lock _gate = new();

    public List<(HttpMethod Method, long? RangeFrom, long? RangeTo, string? IfRange)> Requests { get; } = [];

    // Parallel segments (Phase 2) call this concurrently, so request capture and response building
    // are serialized: List<T> is not thread-safe and a lost Add would skew assertions.
    public HttpResponseMessage Handle(HttpRequestMessage request)
    {
        lock (_gate)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var ifRange = request.Headers.NonValidated.TryGetValues("If-Range", out var values)
                ? values.FirstOrDefault()
                : null;

            Requests.Add((request.Method, range?.From, range?.To, ifRange));

            var isProbe = range is { From: 0, To: 0 };
            if (!isProbe && WorkStatusOverride is { } status)
            {
                var failure = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
                if (WorkRetryAfter is { } retryAfter)
                {
                    failure.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
                }

                return failure;
            }

            var preconditionFailed = ifRange is not null && !IfRangeMatches(ifRange);
            var honourRange = SupportsRanges && range is not null && !preconditionFailed;

            return honourRange
                ? Partial(range!.From ?? 0, range.To ?? Content.Length - 1)
                : Ok();
        }
    }

    private HttpResponseMessage Partial(long from, long to)
    {
        var requestedLength = to - from + 1;
        var bodyLength = MaxBytesPerResponse is { } cap ? Math.Min(requestedLength, cap) : requestedLength;

        var body = new byte[bodyLength];
        Array.Copy(Content, from, body, 0, bodyLength);

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(body),
        };

        // Only forge a mismatch on real ranges; keep the bytes=0-0 probe valid (from must be <= to).
        var reportedFrom = to - from >= ReportedFromDelta ? from + ReportedFromDelta : from;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(reportedFrom, to, Content.Length);
        ApplyValidators(response);
        return response;
    }

    private HttpResponseMessage Ok()
    {
        var bodyLength = MaxBytesPerResponse is { } cap ? Math.Min(Content.Length, cap) : Content.Length;
        var body = new byte[bodyLength];
        Array.Copy(Content, 0, body, 0, bodyLength);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        ApplyValidators(response);
        if (!AdvertiseLength)
        {
            response.Content.Headers.ContentLength = null;
        }

        return response;
    }

    private void ApplyValidators(HttpResponseMessage response)
    {
        if (!string.IsNullOrEmpty(ETag))
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
        }

        if (LastModified is { } lastModified)
        {
            response.Content.Headers.LastModified = lastModified;
        }
    }

    private bool IfRangeMatches(string ifRange)
    {
        if (!string.IsNullOrEmpty(ETag) && string.Equals(ifRange, ETag, StringComparison.Ordinal))
        {
            return true;
        }

        return LastModified is { } lastModified
            && string.Equals(ifRange, lastModified.ToUniversalTime().ToString("R"), StringComparison.Ordinal);
    }
}