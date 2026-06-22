using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DownloadManager.UI.Versioning;
using Microsoft.Extensions.Logging;

namespace DownloadManager.UI.Services;

/// <summary>
/// <see cref="IUpdateChecker"/> against the GitHub Releases API (ADR-0025). It issues a single GET for the
/// <c>releases/latest</c> <b>metadata</b> (tag + html_url), compares the tag to the running version, and
/// returns where to view a newer release — it never fetches an artifact, so there is no download/verify/
/// install path here. Any network/parse error is a silent no-op (returns null). Reflection-free JSON via
/// the source-gen context (AOT-safe).
/// </summary>
public sealed partial class GithubUpdateChecker : IUpdateChecker
{
    // The app's own repository (notify-only; reads public release metadata).
    private const string LatestReleaseUrl = "https://api.github.com/repos/mortezasghari/DownloadManager/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GithubUpdateChecker> _logger;
    private readonly Func<SemanticVersion> _currentVersion;

    public GithubUpdateChecker(
        HttpClient httpClient,
        ILogger<GithubUpdateChecker> logger,
        Func<SemanticVersion>? currentVersion = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        // Default to the running assembly's injected version; tests pin a known "current" to compare against.
        _currentVersion = currentVersion ?? (() => AppVersion.Current());
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = _currentVersion();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "DownloadManager-UpdateCheck");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null; // no releases yet, rate-limited, etc. — silently up-to-date
            }

            var release = await response.Content
                .ReadFromJsonAsync(GithubJsonContext.Default.GithubRelease, cancellationToken)
                .ConfigureAwait(false);

            if (release?.TagName is null || !SemanticVersion.TryParse(release.TagName, out var latest))
            {
                return null;
            }

            if (latest <= current)
            {
                return null; // up to date
            }

            LogUpdateAvailable(current.ToString(), latest.ToString());
            return new UpdateInfo(current, latest, release.HtmlUrl ?? string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Resilient by design: offline / API error / malformed response → no notification, no crash.
            LogCheckFailed(ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Update available: on {Current}, latest is {Latest}.")]
    private partial void LogUpdateAvailable(string current, string latest);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Update check skipped (no-op): {Reason}")]
    private partial void LogCheckFailed(string reason);
}

/// <summary>Minimal shape of the GitHub <c>releases/latest</c> response we read.</summary>
public sealed class GithubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}

/// <summary>Source-gen JSON context for the GitHub release metadata — reflection-free, AOT-safe (spec §1).</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GithubRelease))]
internal sealed partial class GithubJsonContext : System.Text.Json.Serialization.JsonSerializerContext;