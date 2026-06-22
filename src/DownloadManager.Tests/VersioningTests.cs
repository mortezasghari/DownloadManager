using System.Net;
using DownloadManager.UI.Services;
using DownloadManager.UI.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Calculated versioning (ADR-0025): the PR-label bump rules, reading the injected assembly version, and
/// the notify-only update check (mocked GitHub API — no real network, never downloads an artifact).
/// </summary>
public sealed class VersioningTests
{
    // ---------------- Bump logic (the exact rules CI runs via --next-version) ----------------

    [Theory]
    [InlineData("1.4.5", "major", "2.0.0")]   // major bumps, minor+build reset
    [InlineData("1.3.7", "minor", "1.4.0")]   // minor bumps, build resets
    [InlineData("1.3.0", null, "1.3.1")]      // no label → build increments
    [InlineData("1.3.0", "", "1.3.1")]        // empty label → build increments
    [InlineData("1.3.0", "chore", "1.3.1")]   // unknown label → build increments (default)
    [InlineData("v2.9.1", "minor", "2.10.0")] // tolerates a leading 'v'; no decimal carry
    [InlineData("2.9.9", "major", "3.0.0")]
    [InlineData(null, null, "0.0.1")]         // no prior tag → base 0.0.0, build bump
    [InlineData("not-a-version", "minor", "0.1.0")] // unparseable → base 0.0.0
    public void Bump_applies_label_rules(string? current, string? label, string expected)
    {
        Assert.Equal(expected, VersionBump.Next(current, label).ToString());
    }

    [Theory]
    [InlineData("major", BumpKind.Major)]
    [InlineData("MINOR", BumpKind.Minor)]
    [InlineData(" minor ", BumpKind.Minor)]
    [InlineData(null, BumpKind.Build)]
    [InlineData("bug", BumpKind.Build)]
    public void Label_maps_to_bump_kind(string? label, BumpKind expected)
    {
        Assert.Equal(expected, VersionBump.FromLabel(label));
    }

    // ---------------- Reading the (injected) assembly version ----------------

    [Fact]
    public void App_version_reads_the_injected_informational_version_not_a_hardcoded_string()
    {
        // The InformationalVersion CI injects (-p:Version=4.2.9) is read verbatim — not 1.0.0.
        var version = AppVersion.FromAttributes("4.2.9", assemblyVersion: new Version(1, 0, 0, 0));
        Assert.Equal("4.2.9", version.ToString());
        Assert.NotEqual(new SemanticVersion(1, 0, 0), version);
    }

    [Fact]
    public void App_version_parses_an_informational_version_with_a_metadata_suffix()
    {
        Assert.Equal(new SemanticVersion(4, 2, 9), AppVersion.FromAttributes("4.2.9+abc1234", null));
    }

    [Fact]
    public void App_version_falls_back_to_the_assembly_version_when_informational_is_absent()
    {
        Assert.Equal(new SemanticVersion(2, 3, 4), AppVersion.FromAttributes(null, new Version(2, 3, 4)));
    }

    // ---------------- Notify-only update check (mocked GitHub API) ----------------

    private static readonly Func<SemanticVersion> OnVersion150 = () => new SemanticVersion(1, 5, 0);

    [Fact]
    public async Task Update_check_notifies_when_latest_release_is_newer()
    {
        var checker = Checker("""{"tag_name":"v1.6.0","html_url":"https://example/r"}""");
        var info = await checker.CheckAsync();

        Assert.NotNull(info);
        Assert.Equal(new SemanticVersion(1, 5, 0), info!.Value.Current);
        Assert.Equal(new SemanticVersion(1, 6, 0), info.Value.Latest);
        Assert.Equal("https://example/r", info.Value.ReleaseUrl);
    }

    [Theory]
    [InlineData("v1.5.0")] // equal to self → no notification
    [InlineData("v1.4.9")] // older than self → no notification
    public async Task Update_check_is_silent_when_not_newer(string tag)
    {
        var handler = new StubHandler(HttpStatusCode.OK, $$"""{"tag_name":"{{tag}}","html_url":"https://x"}""");
        var checker = new GithubUpdateChecker(new HttpClient(handler), NullLogger<GithubUpdateChecker>.Instance, OnVersion150);

        Assert.Null(await checker.CheckAsync());
        Assert.Equal(1, handler.Calls); // it fetched metadata exactly once, then compared
    }

    [Fact]
    public async Task Update_check_is_a_silent_noop_on_api_error()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "nope");
        var checker = new GithubUpdateChecker(new HttpClient(handler), NullLogger<GithubUpdateChecker>.Instance, OnVersion150);
        Assert.Null(await checker.CheckAsync()); // no throw, no notification
    }

    [Fact]
    public async Task Update_check_is_a_silent_noop_on_network_failure()
    {
        var checker = new GithubUpdateChecker(new HttpClient(new ThrowingHandler()), NullLogger<GithubUpdateChecker>.Instance, OnVersion150);
        Assert.Null(await checker.CheckAsync()); // offline → null, never crashes
    }

    [Fact]
    public async Task Update_check_only_fetches_metadata_and_never_an_artifact()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tag_name":"v9.9.9","html_url":"https://example/r"}""");
        var checker = new GithubUpdateChecker(new HttpClient(handler), NullLogger<GithubUpdateChecker>.Instance, OnVersion150);

        await checker.CheckAsync();

        // Exactly one request — to the releases METADATA endpoint — and nothing resembling an artifact download.
        Assert.Equal(1, handler.Calls);
        Assert.All(handler.RequestedUris, uri =>
        {
            Assert.Contains("releases/latest", uri.AbsoluteUri);
            Assert.DoesNotContain("releases/download", uri.AbsoluteUri);
            Assert.DoesNotContain(".zip", uri.AbsoluteUri);
        });
    }

    private static GithubUpdateChecker Checker(string body) =>
        new(new HttpClient(new StubHandler(HttpStatusCode.OK, body)), NullLogger<GithubUpdateChecker>.Instance, OnVersion150);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri is { } uri)
            {
                RequestedUris.Add(uri);
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}