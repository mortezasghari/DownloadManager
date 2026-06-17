using System.Net;
using DownloadManager.Core.Configuration;

namespace DownloadManager.Core.Http;

/// <summary>
/// Owns the single, app-wide <see cref="SocketsHttpHandler"/> and <see cref="HttpClient"/> (spec §4).
/// One handler for the whole process — never one per download — so connection pooling actually works.
/// The handler disables auto-redirect (so range/validator semantics are re-established on the final
/// URL) and auto-decompression (so byte offsets stay honest for ranged requests, §3).
/// </summary>
public sealed class SharedHttpClient : IDisposable
{
    public SharedHttpClient(HttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new SocketsHttpHandler
        {
            // The per-host cap is the real parallelism constraint (many segments hit the same host).
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
            PooledConnectionLifetime = options.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = options.PooledConnectionIdleTimeout,
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
        };

        if (!string.IsNullOrWhiteSpace(options.Proxy))
        {
            handler.Proxy = new WebProxy(options.Proxy);
            handler.UseProxy = true;
        }

        // Never use HttpClient.Timeout: it would abort long large-file streams. Per-attempt deadlines
        // are enforced with a linked CancellationTokenSource in the engine instead (spec §3).
        Client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}