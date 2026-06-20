using System.Text.Json;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Lifecycle;
using DownloadManager.Persistence.Io;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Persistence.Lifecycle;

/// <summary>
/// Append-only lifecycle-event log as newline-delimited source-gen JSON (ADR-0021), following the
/// <c>.dllog</c> discipline: each <see cref="Append"/> writes one <c>&lt;json&gt;\n</c> record via a
/// positioned write then fsyncs; <see cref="ReadAll"/> replays every complete record and discards a torn
/// final line. On open, the write offset is set just past the last complete record, so a partial tail left
/// by a crash is overwritten by the next append (never an in-place mutation of existing records).
/// </summary>
public sealed partial class JsonLifecycleLog : ILifecycleLog, IDisposable
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly ILogger<JsonLifecycleLog> _logger;
    private readonly SafeFileHandle _handle;

    private long _nextWriteOffset;
    private long _sequence;
    private bool _disposed;

    public JsonLifecycleLog(string path, ILogger<JsonLifecycleLog> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _logger = logger;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _handle = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        // Establish the durable boundary: scan to the end of the last complete record, set the write
        // offset there (so a torn partial tail is overwritten), and seed the sequence from the max seen.
        var (validEnd, lastSequence, torn) = ScanValidRegion();
        _nextWriteOffset = validEnd;
        _sequence = lastSequence;
        if (torn)
        {
            LogTruncatedTail(path);
        }
    }

    public void Append(LifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var sequenced = lifecycleEvent with { Sequence = ++_sequence };
            var json = JsonSerializer.SerializeToUtf8Bytes(sequenced, LifecycleJsonContext.Default.LifecycleEvent);

            // One record = compact JSON + '\n'. STJ escapes control chars, so no literal newline can appear
            // inside a record, which makes '\n' a safe, unambiguous frame delimiter.
            var record = new byte[json.Length + 1];
            json.CopyTo(record, 0);
            record[^1] = (byte)'\n';

            RandomAccess.Write(_handle, record, _nextWriteOffset);
            _nextWriteOffset += record.Length;
            DurableIo.FlushFile(_handle); // fsync — the append is the durable truth
        }
    }

    public IReadOnlyList<LifecycleEvent> ReadAll()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_nextWriteOffset == 0)
            {
                return [];
            }

            var bytes = new byte[_nextWriteOffset];
            ReadFull(bytes, 0);

            var events = new List<LifecycleEvent>();
            var start = 0;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n')
                {
                    continue;
                }

                var line = bytes.AsSpan(start, i - start);
                start = i + 1;
                if (line.IsEmpty)
                {
                    continue;
                }

                LifecycleEvent? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize(line, LifecycleJsonContext.Default.LifecycleEvent);
                }
                catch (JsonException)
                {
                    // A corrupt complete record: stop here and treat the rest as untrusted (conservative).
                    LogCorruptRecord(_path);
                    break;
                }

                if (parsed is not null)
                {
                    events.Add(parsed);
                }
            }

            return events;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Scans existing bytes and returns the offset just past the last complete <c>…\n</c> record, the max
    /// sequence among parseable records, and whether a torn/partial tail was present.
    /// </summary>
    private (long ValidEnd, long LastSequence, bool Torn) ScanValidRegion()
    {
        var length = RandomAccess.GetLength(_handle);
        if (length == 0)
        {
            return (0, 0, false);
        }

        var bytes = new byte[length];
        ReadFull(bytes, 0);

        long validEnd = 0;
        long lastSequence = 0;
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'\n')
            {
                continue;
            }

            var line = bytes.AsSpan(start, i - start);
            var recordEnd = i + 1;
            start = recordEnd;

            if (line.IsEmpty)
            {
                validEnd = recordEnd;
                continue;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize(line, LifecycleJsonContext.Default.LifecycleEvent);
                if (parsed is not null)
                {
                    lastSequence = Math.Max(lastSequence, parsed.Sequence);
                }
            }
            catch (JsonException)
            {
                // First corrupt complete record: everything from here is untrusted.
                break;
            }

            validEnd = recordEnd;
        }

        return (validEnd, lastSequence, validEnd != length);
    }

    private void ReadFull(Span<byte> buffer, long offset)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = RandomAccess.Read(_handle, buffer[read..], offset + read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lifecycle log {Path}: discarded a torn final record on open.")]
    private partial void LogTruncatedTail(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lifecycle log {Path}: stopped replay at a corrupt record.")]
    private partial void LogCorruptRecord(string path);
}