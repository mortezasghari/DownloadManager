using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Domain;
using DownloadManager.Persistence.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Persistence.Progress;

/// <summary>
/// Append-only binary progress log (spec §6b). Each <see cref="Append"/> writes one fixed-size,
/// CRC-protected record via a positioned write; <see cref="FlushToDisk"/> fsyncs it. The engine
/// always calls these <b>after</b> flushing the target file's data, preserving the §6c invariant.
/// When the file grows past the compaction threshold it is rewritten from the current per-segment
/// maxima via an atomic replace.
/// </summary>
public sealed partial class BinaryProgressLog : IProgressLog
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly long _compactionThresholdBytes;
    private readonly ILogger _logger;
    private readonly Dictionary<int, long> _maxima;

    private SafeFileHandle _handle;
    private long _nextWriteOffset;
    private long _sequence;
    private bool _disposed;

    internal BinaryProgressLog(
        SafeFileHandle handle,
        string path,
        long nextWriteOffset,
        long lastSequence,
        Dictionary<int, long> maxima,
        long compactionThresholdBytes,
        ILogger logger)
    {
        _handle = handle;
        _path = path;
        _nextWriteOffset = nextWriteOffset;
        _sequence = lastSequence;
        _maxima = maxima;
        _compactionThresholdBytes = compactionThresholdBytes;
        _logger = logger;
    }

    public void Append(SegmentCheckpoint checkpoint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            Span<byte> record = stackalloc byte[ProgressLogFormat.RecordSize];
            var sequence = ++_sequence;
            ProgressLogFormat.WriteRecord(record, checkpoint.SegmentId, checkpoint.DurableOffset, sequence);

            RandomAccess.Write(_handle, record, _nextWriteOffset);
            _nextWriteOffset += ProgressLogFormat.RecordSize;

            // Durable offsets advance monotonically per segment, so the latest write is the maximum.
            _maxima[checkpoint.SegmentId] = checkpoint.DurableOffset;

            if (_nextWriteOffset >= _compactionThresholdBytes)
            {
                Compact();
            }
        }
    }

    public void FlushToDisk()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DurableIo.FlushFile(_handle);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _handle.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    // Caller holds _gate.
    private void Compact()
    {
        DurableIo.FlushFile(_handle);
        _handle.Dispose();

        var image = ProgressLogFormat.BuildCompacted(_maxima);
        AtomicFileWriter.WriteAllBytes(_path, image); // durable atomic replace

        _handle = File.OpenHandle(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _nextWriteOffset = image.Length;
        _sequence = _maxima.Count;
        LogCompacted(_path, _maxima.Count);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Compacted progress log {Path} to {SegmentCount} segment record(s).")]
    private partial void LogCompacted(string path, int segmentCount);
}