namespace DownloadManager.Core.Domain;

/// <summary>
/// Per-download secrets carried on the <see cref="DownloadRequest"/> (Phase 4): <c>Authorization</c>
/// header(s) and request <c>Cookie</c>s. These are <b>session memory only</b> — they are never written
/// to <c>*.dlmeta</c> or any on-disk state (ADR-0011). OS keystore integration is an explicit non-goal
/// (it would drag native interop into an AOT-absolute build), so credentials live only for the lifetime
/// of the in-memory request and must be re-supplied after a restart.
/// </summary>
public sealed record DownloadCredentials
{
    /// <summary>The empty set: no Authorization, no cookies. The default for a request.</summary>
    public static readonly DownloadCredentials None = new();

    /// <summary>Raw <c>Authorization</c> header value(s), e.g. <c>"Bearer …"</c>. Sent verbatim.</summary>
    public IReadOnlyList<string> AuthorizationHeaders { get; init; } = [];

    /// <summary>Cookie pairs (<c>name=value</c>); joined into a single <c>Cookie</c> request header.</summary>
    public IReadOnlyList<string> Cookies { get; init; } = [];

    public bool IsEmpty => AuthorizationHeaders.Count == 0 && Cookies.Count == 0;
}