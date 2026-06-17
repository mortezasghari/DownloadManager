namespace DownloadManager.Tests.Fakes;

/// <summary>Drives <see cref="HttpClient"/> from a synchronous responder (spec §12: inject a fake handler).</summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(responder(request));
    }
}