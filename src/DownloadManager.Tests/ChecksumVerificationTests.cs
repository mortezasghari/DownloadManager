using System.Security.Cryptography;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Engine;
using DownloadManager.Persistence;
using DownloadManager.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 4 / ADR-0012: streamed, cancellable SHA-256 verification. Match -> Completed; mismatch ->
/// Failed with the file and metadata retained. The hash streams through a bounded buffer (never the
/// whole file) and reports a Verifying progress signal.
/// </summary>
public class ChecksumVerificationTests
{
    private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public async Task Matching_checksum_completes_and_cleans_up()
    {
        using var harness = new EngineHarness();
        var content = EngineHarness.Pattern(120 * 1024);
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"" };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(expectedSha256: Sha256Hex(content)), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Completed, outcome.Kind);
        Assert.Equal(content, harness.ReadTarget());
        Assert.False(harness.SidecarsExist);
    }

    [Fact]
    public async Task Mismatched_checksum_fails_but_retains_the_file_and_metadata()
    {
        using var harness = new EngineHarness();
        var content = EngineHarness.Pattern(120 * 1024);
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"" };

        var outcome = await harness.BuildEngine(server)
            .RunAsync(harness.Request(expectedSha256: ZeroHash), null, CancellationToken.None);

        Assert.Equal(DownloadResultKind.Failed, outcome.Kind);
        Assert.False(outcome.IsTransient);

        // The (correctly downloaded) bytes are kept so the user can re-download — never silently dropped.
        Assert.True(File.Exists(harness.TargetPath));
        Assert.Equal(content, harness.ReadTarget());
        // Metadata is retained alongside the file.
        Assert.True(File.Exists(PersistencePaths.MetadataPath(harness.TargetPath)));
    }

    [Fact]
    public async Task Verification_emits_a_verifying_progress_signal()
    {
        using var harness = new EngineHarness();
        var content = EngineHarness.Pattern(120 * 1024);
        var server = new FakeContentServer { Content = content, ETag = "\"v1\"" };

        // A synchronous collector: the engine calls Report inline, so capture is deterministic (unlike
        // Progress<T>, which posts to the thread pool and would race on a plain List).
        var progress = new CollectingProgress();

        await harness.BuildEngine(server)
            .RunAsync(harness.Request(expectedSha256: Sha256Hex(content)), progress, CancellationToken.None);

        Assert.Contains(DownloadPhase.Verifying, progress.Phases);
    }

    [Fact]
    public async Task Hashing_streams_through_a_bounded_buffer_never_the_whole_file()
    {
        var data = EngineHarness.Pattern(1_000_000); // ~1 MB
        const int bufferSize = 64 * 1024;
        using var tracking = new TrackingStream(new MemoryStream(data));

        var hex = await ChecksumVerifier.ComputeAsync(tracking, data.Length, bufferSize, progress: null, CancellationToken.None);

        Assert.Equal(Sha256Hex(data), hex);
        // No single read ever asked for more than the buffer — i.e. the file was never buffered whole.
        Assert.True(tracking.MaxReadRequested <= bufferSize, $"max read {tracking.MaxReadRequested} > buffer {bufferSize}");
        Assert.True(tracking.ReadCount >= data.Length / bufferSize, "expected many bounded reads, not one big slurp");
    }

    [Fact]
    public async Task Verification_is_cancellable_mid_stream()
    {
        var data = EngineHarness.Pattern(1_000_000);
        const int bufferSize = 64 * 1024;
        using var cts = new CancellationTokenSource();
        using var stream = new TrackingStream(new MemoryStream(data)) { CancelAfterBytes = bufferSize, Trigger = cts };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ChecksumVerifier.ComputeAsync(stream, data.Length, bufferSize, progress: null, cts.Token));

        // It stopped early — it did not stream the whole file.
        Assert.True(stream.TotalRead < data.Length);
    }

    [Fact]
    public async Task File_overload_reports_match_against_a_real_file()
    {
        var data = EngineHarness.Pattern(300 * 1024);
        var path = Path.Combine(Path.GetTempPath(), $"dlm-verify-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, data);
        try
        {
            var verifier = new ChecksumVerifier(new FakeTimeProvider(), NullLogger<ChecksumVerifier>.Instance);

            var match = await verifier.VerifyAsync(path, Sha256Hex(data), 64 * 1024, null, CancellationToken.None);
            Assert.True(match.Matched);

            // Tolerates an uppercase, sha256:-prefixed expected value.
            var prefixed = await verifier.VerifyAsync(
                path, "sha256:" + Sha256Hex(data).ToUpperInvariant(), 64 * 1024, null, CancellationToken.None);
            Assert.True(prefixed.Matched);

            var mismatch = await verifier.VerifyAsync(path, ZeroHash, 64 * 1024, null, CancellationToken.None);
            Assert.False(mismatch.Matched);
            Assert.Equal(ZeroHash, mismatch.ExpectedHex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Thread-safe synchronous progress sink: the engine reports inline, so this is deterministic.</summary>
    private sealed class CollectingProgress : IProgress<DownloadProgress>
    {
        private readonly Lock _gate = new();
        private readonly List<DownloadPhase> _phases = [];

        public IReadOnlyList<DownloadPhase> Phases
        {
            get { lock (_gate) { return _phases.ToArray(); } }
        }

        public void Report(DownloadProgress value)
        {
            lock (_gate) { _phases.Add(value.Phase); }
        }
    }

    /// <summary>A pass-through stream that records read sizes and can trip a token mid-stream.</summary>
    private sealed class TrackingStream(Stream inner) : Stream
    {
        public int MaxReadRequested { get; private set; }

        public int ReadCount { get; private set; }

        public long TotalRead { get; private set; }

        public long CancelAfterBytes { get; init; } = long.MaxValue;

        public CancellationTokenSource? Trigger { get; init; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaxReadRequested = Math.Max(MaxReadRequested, buffer.Length);
            var read = await inner.ReadAsync(buffer, cancellationToken);
            ReadCount++;
            TotalRead += read;
            if (TotalRead >= CancelAfterBytes)
            {
                Trigger?.Cancel();
            }

            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}