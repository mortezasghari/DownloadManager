using System.Net;
using DownloadManager.Core.Domain;
using DownloadManager.Persistence;
using DownloadManager.Tests.Fakes;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 4 / ADR-0011: per-download Authorization + Cookie are carried on the request, sent to the
/// origin, retained across a same-origin redirect, and STRIPPED on a cross-host redirect. Secrets are
/// never written to the on-disk metadata.
/// </summary>
public class AuthCookieTests
{
    private const string Token = "Bearer SUPERSECRETTOKEN";
    private const string Cookie = "session=SECRETCOOKIEVALUE";

    private static DownloadCredentials Creds() =>
        new() { AuthorizationHeaders = [Token], Cookies = [Cookie] };

    /// <summary>Records the credential headers seen on each request, keyed by target URI.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> RecordingServer(
        FakeContentServer content, Uri? redirectFrom, Uri? redirectTo,
        List<(Uri Uri, string? Auth, string? Cookie)> captured) =>
        request =>
        {
            var auth = request.Headers.NonValidated.TryGetValues("Authorization", out var a) ? a.FirstOrDefault() : null;
            var cookie = request.Headers.NonValidated.TryGetValues("Cookie", out var c) ? c.FirstOrDefault() : null;
            captured.Add((request.RequestUri!, auth, cookie));

            if (redirectFrom is not null && request.RequestUri == redirectFrom)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new ByteArrayContent([]) };
                redirect.Headers.Location = redirectTo;
                return redirect;
            }

            return content.Handle(request);
        };

    [Fact]
    public async Task Credentials_are_sent_to_the_origin()
    {
        using var harness = new EngineHarness();
        var captured = new List<(Uri Uri, string? Auth, string? Cookie)>();
        var content = new FakeContentServer { Content = EngineHarness.Pattern(50 * 1024), ETag = "\"v1\"" };

        var outcome = await harness
            .BuildEngine(RecordingServer(content, redirectFrom: null, redirectTo: null, captured))
            .RunAsync(harness.Request(credentials: Creds()), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.NotEmpty(captured);
        Assert.All(captured, r =>
        {
            Assert.Equal(Token, r.Auth);
            Assert.Equal(Cookie, r.Cookie);
        });
    }

    [Fact]
    public async Task Credentials_are_retained_on_a_same_host_redirect()
    {
        using var harness = new EngineHarness();
        var captured = new List<(Uri Uri, string? Auth, string? Cookie)>();
        var content = new FakeContentServer { Content = EngineHarness.Pattern(50 * 1024), ETag = "\"v1\"" };
        var from = new Uri("http://origin.test/file");
        var to = new Uri("http://origin.test/real"); // same scheme+host+port

        var outcome = await harness
            .BuildEngine(RecordingServer(content, from, to, captured))
            .RunAsync(harness.Request(url: from, credentials: Creds()), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        // Every request — the initial /file, the redirected /real probe, and the /real segment — is
        // same-origin, so all carry the credentials.
        Assert.All(captured, r =>
        {
            Assert.Equal(Token, r.Auth);
            Assert.Equal(Cookie, r.Cookie);
        });
        Assert.Contains(captured, r => r.Uri == to);
    }

    [Fact]
    public async Task Credentials_are_stripped_on_a_cross_host_redirect()
    {
        using var harness = new EngineHarness();
        var captured = new List<(Uri Uri, string? Auth, string? Cookie)>();
        var content = new FakeContentServer { Content = EngineHarness.Pattern(50 * 1024), ETag = "\"v1\"" };
        var from = new Uri("http://origin.test/file");
        var to = new Uri("http://elsewhere.test/real"); // different host

        var outcome = await harness
            .BuildEngine(RecordingServer(content, from, to, captured))
            .RunAsync(harness.Request(url: from, credentials: Creds()), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);

        // The credential-bearing request to the origin carried them...
        Assert.Contains(captured, r => r.Uri == from && r.Auth == Token && r.Cookie == Cookie);
        // ...but every request to the cross-host redirect target carried NO Authorization and NO Cookie.
        var crossHost = captured.Where(r => r.Uri.Host == "elsewhere.test").ToList();
        Assert.NotEmpty(crossHost);
        Assert.All(crossHost, r =>
        {
            Assert.Null(r.Auth);
            Assert.Null(r.Cookie);
        });
    }

    [Fact]
    public async Task Secrets_are_never_written_to_the_metadata_sidecar()
    {
        using var harness = new EngineHarness();
        // Truncated responses leave the download incomplete, so the .dlmeta sidecar is retained on disk.
        var content = new FakeContentServer
        {
            Content = EngineHarness.Pattern(100 * 1024),
            ETag = "\"v1\"",
            MaxBytesPerResponse = 20 * 1024,
        };

        var outcome = await harness.BuildEngine(content)
            .RunAsync(harness.Request(credentials: Creds()), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Failed, outcome.Kind);
        Assert.True(harness.SidecarsExist);

        var metaPath = PersistencePaths.MetadataPath(harness.TargetPath);
        var json = await File.ReadAllTextAsync(metaPath);
        Assert.DoesNotContain("SUPERSECRETTOKEN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETCOOKIEVALUE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_returning_401_is_needs_credentials_and_keeps_progress()
    {
        using var harness = new EngineHarness();
        var content = new FakeContentServer
        {
            Content = EngineHarness.Pattern(100 * 1024),
            ETag = "\"v1\"",
            SupportsRanges = true,
            MaxBytesPerResponse = 30 * 1024, // first run drops early, leaving resumable state
        };

        var first = await harness.BuildEngine(content).RunAsync(harness.Request(), null, CancellationToken.None);
        Assert.Equal(DownloadResultKind.Failed, first.Kind);
        Assert.True(harness.SidecarsExist);

        // The credentials have gone stale: work requests now 401 (the bytes=0-0 revalidation probe still
        // succeeds, so the engine proceeds to a work request that is rejected).
        content.MaxBytesPerResponse = null;
        content.WorkStatusOverride = HttpStatusCode.Unauthorized;

        var second = await harness.BuildEngine(content).RunAsync(harness.Request(), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Failed, second.Kind);
        Assert.True(second.NeedsCredentials);
        Assert.False(second.IsTransient);
        // Crucially, the partial download is NOT discarded.
        Assert.True(harness.SidecarsExist);
    }
}