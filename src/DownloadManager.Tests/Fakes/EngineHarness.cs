using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Engine;
using DownloadManager.Core.Http;
using DownloadManager.Persistence;
using DownloadManager.Persistence.Io;
using DownloadManager.Persistence.Metadata;
using DownloadManager.Persistence.Progress;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DownloadManager.Tests.Fakes;

/// <summary>
/// Wires a real <see cref="DownloadEngine"/> over the real persistence stack (temp files) with a
/// fake HTTP origin and a <see cref="FakeTimeProvider"/>. This is the end-to-end seam used by the
/// engine/resume/durability tests.
/// </summary>
internal sealed class EngineHarness : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dlm-tests", Guid.NewGuid().ToString("N"));

    public EngineHarness()
    {
        Directory.CreateDirectory(_directory);
        EngineOptions = new EngineOptions
        {
            // Small values so tests exercise many checkpoints/buffers without large files.
            CopyBufferSize = 8 * 1024,
            CheckpointIntervalBytes = 16 * 1024,
        };
    }

    public FakeTimeProvider Time { get; } = new();

    public EngineOptions EngineOptions { get; set; }

    public string TargetPath => Path.Combine(_directory, "download.bin");

    public Uri Url { get; } = new("http://origin.test/file");

    public DownloadEngine BuildEngine(FakeContentServer server) => BuildEngine(server.Handle);

    public DownloadEngine BuildEngine(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClient = new HttpClient(new FakeHttpMessageHandler(responder));
        var prober = new RangeProber(
            httpClient, new HttpOptions(), EngineOptions, Time, NullLogger<RangeProber>.Instance);

        return new DownloadEngine(
            prober,
            httpClient,
            new TargetFileFactory(NullLogger<TargetFileFactory>.Instance),
            new BinaryProgressLogStore(new ProgressLogOptions(), NullLogger<BinaryProgressLogStore>.Instance),
            new JsonMetadataStore(NullLogger<JsonMetadataStore>.Instance),
            new ChecksumVerifier(Time, NullLogger<ChecksumVerifier>.Instance),
            EngineOptions,
            Time,
            NullLogger<DownloadEngine>.Instance);
    }

    public DownloadRequest Request(
        int segmentCount = 1,
        PreallocationMode preallocation = PreallocationMode.Full,
        string? expectedSha256 = null,
        DownloadCredentials? credentials = null,
        Uri? url = null) =>
        new()
        {
            Id = DownloadId.New(),
            Url = url ?? Url,
            TargetPath = TargetPath,
            SegmentCount = segmentCount,
            Preallocation = preallocation,
            ExpectedSha256 = expectedSha256,
            Credentials = credentials ?? DownloadCredentials.None,
        };

    public byte[] ReadTarget() => File.ReadAllBytes(TargetPath);

    public bool SidecarsExist =>
        File.Exists(PersistencePaths.MetadataPath(TargetPath))
        || File.Exists(PersistencePaths.ProgressLogPath(TargetPath));

    public static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) ^ (i >> 8));
        }

        return bytes;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}