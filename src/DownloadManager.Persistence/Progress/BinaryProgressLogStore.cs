using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using DownloadManager.Persistence.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Persistence.Progress;

/// <summary>
/// Opens a <see cref="BinaryProgressLog"/>, performing crash recovery (spec §6b/§6d): scan records,
/// keep the highest valid (CRC-passing) durable offset per segment, and normalize away any torn or
/// corrupt tail. A linear scan over fixed-size records makes this simple and total.
/// </summary>
public sealed partial class BinaryProgressLogStore(
    ProgressLogOptions options,
    ILogger<BinaryProgressLogStore> logger) : IProgressLogStore
{
    private readonly ProgressLogOptions _options = options;
    private readonly ILogger<BinaryProgressLogStore> _logger = logger;

    public ProgressLogSession Open(string targetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        var path = PersistencePaths.ProgressLogPath(targetPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var length = RandomAccess.GetLength(handle);

        var maxima = new Dictionary<int, long>();
        long lastSequence = 0;
        var dirty = false;

        if (length < ProgressLogFormat.HeaderSize)
        {
            // Empty (fresh) or a header torn by a crash mid-initialization.
            dirty = length != 0;
            InitializeHeader(handle);
            length = ProgressLogFormat.HeaderSize;
        }
        else
        {
            Span<byte> header = stackalloc byte[ProgressLogFormat.HeaderSize];
            ReadFull(handle, header, 0);

            if (!ProgressLogFormat.IsValidHeader(header))
            {
                // Unrecognized header: cannot trust any record. Restart the log from scratch.
                LogCorruptHeader(path);
                handle.Dispose();
                handle = ResetWithHeader(path);
                length = ProgressLogFormat.HeaderSize;
            }
            else
            {
                lastSequence = ScanRecords(handle, length, maxima, out var validEnd);
                dirty = validEnd != length; // torn tail or trailing partial bytes
            }
        }

        if (dirty)
        {
            LogTruncatedTail(path);
            handle.Dispose();
            handle = RewriteCompacted(path, maxima);
            length = ProgressLogFormat.HeaderSize + ((long)maxima.Count * ProgressLogFormat.RecordSize);
            lastSequence = maxima.Count;
        }

        var log = new BinaryProgressLog(
            handle, path, length, lastSequence,
            new Dictionary<int, long>(maxima), _options.CompactionThresholdBytes, _logger);

        return new ProgressLogSession(log, new Dictionary<int, long>(maxima));
    }

    public void Delete(string targetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);
        var path = PersistencePaths.ProgressLogPath(targetPath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Scans records from the header onward, keeping per-segment maxima. Stops at the first
    /// invalid record (the start of a torn/corrupt tail). Returns the highest sequence seen.</summary>
    private static long ScanRecords(
        SafeFileHandle handle, long length, Dictionary<int, long> maxima, out long validEnd)
    {
        long lastSequence = 0;
        validEnd = ProgressLogFormat.HeaderSize;

        Span<byte> record = stackalloc byte[ProgressLogFormat.RecordSize];
        var offset = (long)ProgressLogFormat.HeaderSize;
        while (offset + ProgressLogFormat.RecordSize <= length)
        {
            if (ReadFull(handle, record, offset) != ProgressLogFormat.RecordSize)
            {
                break;
            }

            if (!ProgressLogFormat.TryReadRecord(record, out var segmentId, out var durableOffset, out var sequence))
            {
                break; // torn/corrupt tail begins here
            }

            if (!maxima.TryGetValue(segmentId, out var existing) || durableOffset > existing)
            {
                maxima[segmentId] = durableOffset;
            }

            if (sequence > lastSequence)
            {
                lastSequence = sequence;
            }

            validEnd = offset + ProgressLogFormat.RecordSize;
            offset += ProgressLogFormat.RecordSize;
        }

        return lastSequence;
    }

    private static void InitializeHeader(SafeFileHandle handle)
    {
        Span<byte> header = stackalloc byte[ProgressLogFormat.HeaderSize];
        ProgressLogFormat.WriteHeader(header);
        RandomAccess.Write(handle, header, 0);
        DurableIo.FlushFile(handle);
    }

    private static SafeFileHandle ResetWithHeader(string path)
    {
        Span<byte> header = stackalloc byte[ProgressLogFormat.HeaderSize];
        ProgressLogFormat.WriteHeader(header);
        AtomicFileWriter.WriteAllBytes(path, header);
        return File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    }

    private static SafeFileHandle RewriteCompacted(string path, Dictionary<int, long> maxima)
    {
        var image = ProgressLogFormat.BuildCompacted(maxima);
        AtomicFileWriter.WriteAllBytes(path, image);
        return File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
    }

    private static int ReadFull(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Progress log {Path} has an unrecognized header; restarting it.")]
    private partial void LogCorruptHeader(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Progress log {Path} had a torn/corrupt tail; normalized to recovered maxima.")]
    private partial void LogTruncatedTail(string path);
}