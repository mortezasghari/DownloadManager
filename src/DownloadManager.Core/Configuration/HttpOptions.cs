namespace DownloadManager.Core.Configuration;

/// <summary>
/// Configuration for the single shared <c>SocketsHttpHandler</c> (spec §4). The per-host connection
/// cap — not the total — is what actually constrains parallelism, since many segments target the
/// same host, so it is explicit and configurable rather than left at the int.MaxValue default.
/// </summary>
public sealed record HttpOptions
{
    public const string SectionName = "Http";

    /// <summary>Per-host connection cap. 16 downloads × 8 segments mostly hit a few hosts (§4).</summary>
    public int MaxConnectionsPerServer { get; init; } = 16;

    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum redirects to follow manually during probing (spec §3).</summary>
    public int MaxRedirects { get; init; } = 10;

    public string? Proxy { get; init; }
}