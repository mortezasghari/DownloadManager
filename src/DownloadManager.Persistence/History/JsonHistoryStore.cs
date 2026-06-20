using System.Text.Json;
using DownloadManager.Core.Abstractions;
using DownloadManager.Core.History;
using DownloadManager.Persistence.Io;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence.History;

/// <summary>
/// <see cref="IHistoryStore"/> backed by a source-generated-JSON <c>history.json</c>, written atomically
/// and durably via <see cref="AtomicFileWriter"/> (ADR-0019) — the same hygiene as the metadata sidecar.
/// The records are held in memory (loaded once); each <see cref="Append"/> mutates that list and rewrites
/// the whole file atomically. A missing or malformed file loads as empty history with a warning and is
/// <b>not</b> overwritten — a later real append is what first writes a clean file.
/// </summary>
public sealed partial class JsonHistoryStore : IHistoryStore
{
    private readonly string _path;
    private readonly ILogger<JsonHistoryStore> _logger;
    private readonly Lock _gate = new();
    private readonly List<HistoryRecord> _records;

    public JsonHistoryStore(string historyFilePath, ILogger<JsonHistoryStore> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(historyFilePath);
        _path = historyFilePath;
        _logger = logger;
        _records = ReadFromDisk();
    }

    public IReadOnlyList<HistoryRecord> Load()
    {
        lock (_gate)
        {
            // Defensive copy: callers (the view) must not see the list mutate under them on a later append.
            return _records.ToArray();
        }
    }

    public void Append(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _records.Add(record);
            Persist();
        }
        LogAppended(record.Id, record.State);
    }

    public void Rebuild(IReadOnlyList<HistoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            _records.Clear();
            _records.AddRange(records);
            Persist();
        }
        LogRebuilt(_path, records.Count);
    }

    private List<HistoryRecord> ReadFromDisk()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            var bytes = File.ReadAllBytes(_path);
            var file = JsonSerializer.Deserialize(bytes, HistoryJsonContext.Default.HistoryFile);
            return file?.Records ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Malformed/unreadable: empty history, warn, and do NOT overwrite the user's file.
            LogUnreadable(_path, ex.Message);
            return [];
        }
    }

    private void Persist()
    {
        var file = new HistoryFile { Records = _records };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, HistoryJsonContext.Default.HistoryFile);
        AtomicFileWriter.WriteAllBytes(_path, bytes);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Appended history record {Id} ({State}).")]
    private partial void LogAppended(string id, HistoryState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rebuilt history {Path} from the lifecycle log ({Count} record(s)).")]
    private partial void LogRebuilt(string path, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "history.json at {Path} is unreadable; starting with empty history (file left untouched): {Reason}")]
    private partial void LogUnreadable(string path, string reason);
}