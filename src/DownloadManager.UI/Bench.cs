using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.Engine;
using DownloadManager.Core.Http;
using DownloadManager.Core.Scheduler;
using DownloadManager.Persistence.Io;
using DownloadManager.Persistence.Metadata;
using DownloadManager.Persistence.Progress;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadManager.UI;

/// <summary>
/// Headless perf harness (<c>DownloadManager.UI --bench</c>): drives the real engine/scheduler over a
/// raw-socket loopback HTTP origin (trim-safe, generates bytes on the fly so the server itself stays
/// tiny). Sweeps the three spec levers — fsync/checkpoint cadence, copy-buffer size, per-host connection
/// cap — and reports throughput plus peak memory under concurrent load. Local measurement only; not part
/// of the CI gate. See docs/perf/phase5-tuning.md.
/// </summary>
internal static class Bench
{
    public static async Task<int> RunAsync()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"dlm-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        using var server = new LoopbackOrigin();
        server.Start();
        Console.WriteLine($"Loopback origin on {server.BaseUri}");

        try
        {
            const long size = 128L * 1024 * 1024; // 128 MB per throughput download

            Console.WriteLine("\n# Checkpoint cadence (8 segments, 128 KB buffer, 128 MB)");
            foreach (var checkpoint in new[] { 1L, 8L, 64L })
            {
                await MeasureOneAsync(server, workDir, size,
                    new EngineOptions { CheckpointIntervalBytes = checkpoint * 1024 * 1024 },
                    new HttpOptions(), segments: 8, label: $"checkpoint={checkpoint} MB");
            }

            Console.WriteLine("\n# Copy-buffer size (8 segments, 8 MB checkpoint, 128 MB)");
            foreach (var bufferKb in new[] { 64, 128, 1024 })
            {
                await MeasureOneAsync(server, workDir, size,
                    new EngineOptions { CopyBufferSize = bufferKb * 1024 },
                    new HttpOptions(), segments: 8, label: $"buffer={bufferKb} KB");
            }

            Console.WriteLine("\n# Segment count / per-host connections (128 KB buffer, 8 MB checkpoint, 128 MB)");
            foreach (var segments in new[] { 1, 4, 8, 16 })
            {
                await MeasureOneAsync(server, workDir, size,
                    new EngineOptions(), new HttpOptions { MaxConnectionsPerServer = segments },
                    segments, label: $"segments={segments}");
            }

            await MeasureMemoryCeilingAsync(server, workDir);
            return 0;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task MeasureOneAsync(
        LoopbackOrigin server, string workDir, long size,
        EngineOptions engineOptions, HttpOptions httpOptions, int segments, string label)
    {
        var target = Path.Combine(workDir, $"{Guid.NewGuid():N}.bin");
        var (scheduler, http) = BuildScheduler(engineOptions, httpOptions);
        var before = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();
        try
        {
            var handle = await scheduler.EnqueueAsync(new DownloadRequest
            {
                Id = DownloadId.New(),
                Url = new Uri(server.BaseUri, $"/{size}"),
                TargetPath = target,
                SegmentCount = segments,
                Preallocation = PreallocationMode.Full,
            });
            await handle.WaitForStatusAsync(s => s is DownloadStatus.Completed or DownloadStatus.Failed);
            sw.Stop();

            var allocated = (GC.GetTotalAllocatedBytes() - before) / (1024.0 * 1024);
            var mbps = size / (1024.0 * 1024) / sw.Elapsed.TotalSeconds;
            Console.WriteLine($"  {label,-22} {mbps,7:0.0} MB/s   {sw.ElapsedMilliseconds,6} ms   alloc {allocated,6:0.0} MB");
        }
        finally
        {
            await scheduler.DisposeAsync();
            http.Dispose();
            try { File.Delete(target); } catch (IOException) { }
        }
    }

    private static async Task MeasureMemoryCeilingAsync(LoopbackOrigin server, string workDir)
    {
        const int concurrent = 8;
        const long size = 64L * 1024 * 1024; // 8 x 64 MB = 512 MB of transfer
        Console.WriteLine($"\n# Memory ceiling: {concurrent} concurrent x {size / 1024 / 1024} MB");

        var (scheduler, http) = BuildScheduler(
            new EngineOptions(),
            new HttpOptions { MaxConnectionsPerServer = 64 },
            maxConcurrent: concurrent);
        try
        {
            var handles = new List<IDownloadHandle>();
            for (var i = 0; i < concurrent; i++)
            {
                handles.Add(await scheduler.EnqueueAsync(new DownloadRequest
                {
                    Id = DownloadId.New(),
                    Url = new Uri(server.BaseUri, $"/{size}"),
                    TargetPath = Path.Combine(workDir, $"mem-{i}.bin"),
                    SegmentCount = 8,
                    Preallocation = PreallocationMode.Full,
                }));
            }

            await Task.WhenAll(handles.Select(h =>
                h.WaitForStatusAsync(s => s is DownloadStatus.Completed or DownloadStatus.Failed)));

            using var proc = Process.GetCurrentProcess();
            var peakWorkingSet = proc.PeakWorkingSet64 / (1024.0 * 1024);
            var managed = GC.GetTotalMemory(forceFullCollection: true) / (1024.0 * 1024);
            Console.WriteLine($"  peak working set {peakWorkingSet:0.0} MB   managed heap {managed:0.0} MB   (ceiling 200 MB)");
            Console.WriteLine($"  RESULT: {(peakWorkingSet < 200 ? "UNDER" : "OVER")} 200 MB ceiling");
        }
        finally
        {
            await scheduler.DisposeAsync();
            http.Dispose();
            for (var i = 0; i < concurrent; i++)
            {
                try { File.Delete(Path.Combine(workDir, $"mem-{i}.bin")); } catch (IOException) { }
            }
        }
    }

    private static (DownloadScheduler Scheduler, SharedHttpClient Http) BuildScheduler(
        EngineOptions engineOptions, HttpOptions httpOptions, int maxConcurrent = 1)
    {
        var http = new SharedHttpClient(httpOptions);
        var prober = new RangeProber(http.Client, httpOptions, engineOptions, TimeProvider.System, NullLogger<RangeProber>.Instance);
        var engine = new DownloadEngine(
            prober, http.Client,
            new TargetFileFactory(NullLogger<TargetFileFactory>.Instance),
            new BinaryProgressLogStore(new ProgressLogOptions(), NullLogger<BinaryProgressLogStore>.Instance),
            new JsonMetadataStore(NullLogger<JsonMetadataStore>.Instance),
            new ChecksumVerifier(TimeProvider.System, NullLogger<ChecksumVerifier>.Instance),
            engineOptions, TimeProvider.System, NullLogger<DownloadEngine>.Instance);
        var scheduler = new DownloadScheduler(
            engine, new RetryPolicy(new RetryOptions()),
            new SchedulerOptions { MaxConcurrentDownloads = maxConcurrent, QueueCapacity = 1024 },
            TimeProvider.System, NullLogger<DownloadScheduler>.Instance);
        return (scheduler, http);
    }

    /// <summary>
    /// Tiny raw-socket HTTP/1.1 origin. Always honours the request's <c>Range</c> with a <c>206</c>
    /// (Content-Range echoing the requested span and the total from the URL path), generating a
    /// deterministic byte pattern on the fly so the server holds no large buffer — keeping its own memory
    /// out of the downloader's ceiling measurement.
    /// </summary>
    private sealed class LoopbackOrigin : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();

        public Uri BaseUri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public void Start()
        {
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) { return; }
                _ = ServeAsync(client);
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var request = await ReadHeadersAsync(stream);
                if (request is null)
                {
                    return;
                }

                var (path, rangeFrom, rangeTo) = request.Value;
                var total = long.TryParse(path.Trim('/'), out var t) ? t : 0;
                var from = rangeFrom ?? 0;
                var to = rangeTo ?? total - 1;
                var length = to - from + 1;

                var header = new StringBuilder()
                    .Append("HTTP/1.1 206 Partial Content\r\n")
                    .Append("Accept-Ranges: bytes\r\n")
                    .Append("ETag: \"bench\"\r\n")
                    .Append($"Content-Range: bytes {from}-{to}/{total}\r\n")
                    .Append($"Content-Length: {length}\r\n")
                    .Append("Connection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()));

                var buffer = new byte[64 * 1024];
                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = (byte)(i % 251);
                }

                var remaining = length;
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(remaining, buffer.Length);
                    await stream.WriteAsync(buffer.AsMemory(0, chunk));
                    remaining -= chunk;
                }
            }
        }

        private static async Task<(string Path, long? From, long? To)?> ReadHeadersAsync(NetworkStream stream)
        {
            var raw = new List<byte>(512);
            var one = new byte[1];
            while (raw.Count < 4 || !(raw[^4] == '\r' && raw[^3] == '\n' && raw[^2] == '\r' && raw[^1] == '\n'))
            {
                var read = await stream.ReadAsync(one);
                if (read == 0)
                {
                    return null;
                }

                raw.Add(one[0]);
            }

            var text = Encoding.ASCII.GetString(raw.ToArray());
            var lines = text.Split("\r\n");
            var path = lines[0].Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";

            long? from = null;
            long? to = null;
            foreach (var line in lines)
            {
                if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    var spec = line["Range:".Length..].Trim().Replace("bytes=", string.Empty);
                    var span = spec.Split('-');
                    if (span.Length == 2)
                    {
                        from = long.TryParse(span[0], out var f) ? f : null;
                        to = long.TryParse(span[1], out var tt) ? tt : null;
                    }
                }
            }

            return (path, from, to);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}