namespace DownloadManager.Tests.Fakes;

/// <summary>Async fake handler, so a responder can block (gate) a request until the test releases it.</summary>
internal sealed class AsyncFakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) => responder(request, cancellationToken);
}