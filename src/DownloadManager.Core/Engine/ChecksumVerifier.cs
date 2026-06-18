using System.Buffers;
using System.Security.Cryptography;
using DownloadManager.Core.Domain;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Core.Engine;

/// <summary>Outcome of a SHA-256 verification pass. Hex strings are lowercase, unprefixed.</summary>
public readonly record struct ChecksumResult(bool Matched, string ExpectedHex, string ActualHex);

/// <summary>
/// Streams SHA-256 over a completed file (spec §4 / Phase 4, ADR-0012). The file is hashed through a
/// single <see cref="ArrayPool{T}"/> buffer — never loaded into memory — so it scales to 100 GB targets
/// at bounded cost. The pass is cancellable and reports a <see cref="DownloadPhase.Verifying"/> progress
/// signal (it can be a multi-minute operation on a large file). All timing reads go through the injected
/// <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class ChecksumVerifier(TimeProvider timeProvider, ILogger<ChecksumVerifier> logger)
{
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ChecksumVerifier> _logger = logger;

    public async Task<ChecksumResult> VerifyAsync(
        string filePath, string expectedSha256, int bufferSize,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentException.ThrowIfNullOrEmpty(expectedSha256);

        var started = _timeProvider.GetTimestamp();

        // Sequential, async stream over the finished file; the hash advances one buffer at a time.
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var actual = await ComputeAsync(stream, stream.Length, bufferSize, progress, cancellationToken)
            .ConfigureAwait(false);
        var expected = Normalize(expectedSha256);
        var matched = string.Equals(actual, expected, StringComparison.Ordinal);

        var elapsed = _timeProvider.GetElapsedTime(started);
        if (matched)
        {
            LogVerified(filePath, elapsed);
        }
        else
        {
            LogMismatch(filePath, expected, actual, elapsed);
        }

        return new ChecksumResult(matched, expected, actual);
    }

    /// <summary>
    /// The hashing core, factored out so it can be driven over any stream (the file path overload opens
    /// the file). Computes the lowercase hex SHA-256, hashing through one rented buffer and reporting
    /// bounded-cadence <see cref="DownloadPhase.Verifying"/> progress.
    /// </summary>
    internal static async Task<string> ComputeAsync(
        Stream stream, long totalBytes, int bufferSize,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long hashed = 0;
            long reportedAt = -1;
            // Report at most ~100 times across the file (and always the first/last), so a 100 GB hash
            // doesn't flood IProgress while a small file still emits a visible Verifying signal.
            var reportEvery = Math.Max(bufferSize, totalBytes / 100);

            progress?.Report(new DownloadProgress(0, totalBytes, DownloadPhase.Verifying));

            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                hashed += read;

                if (progress is not null && hashed - reportedAt >= reportEvery)
                {
                    progress.Report(new DownloadProgress(hashed, totalBytes, DownloadPhase.Verifying));
                    reportedAt = hashed;
                }
            }

            progress?.Report(new DownloadProgress(hashed, totalBytes, DownloadPhase.Verifying));

            Span<byte> digest = stackalloc byte[32]; // SHA-256 = 32 bytes
            hash.GetCurrentHash(digest);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Lowercase, trimmed, and tolerant of an optional <c>sha256:</c> scheme prefix.</summary>
    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["sha256:".Length..];
        }

        return trimmed.ToLowerInvariant();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Checksum verified for {Path} in {Elapsed}.")]
    private partial void LogVerified(string path, TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Checksum mismatch for {Path} after {Elapsed}: expected {Expected}, computed {Actual}. File and metadata retained.")]
    private partial void LogMismatch(string path, string expected, string actual, TimeSpan elapsed);
}