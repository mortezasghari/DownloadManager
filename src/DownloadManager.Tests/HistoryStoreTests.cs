using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Persistence.History;
using DownloadManager.Tests.Fakes;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Phase 9 / ADR-0019: the read-only history store. Records persist across reload via the source-gen
/// context; missing/malformed files load as empty history without crashing or overwriting the user's file.
/// </summary>
public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dlm-history", Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_dir, "history.json");

    public HistoryStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private JsonHistoryStore NewStore() =>
        new(HistoryPath, new CollectingLogger<JsonHistoryStore>());

    private static HistoryRecord Record(string name, HistoryState state, long size = 1024) =>
        HistoryRecord.From(DownloadId.New(), name, size, state, $"/downloads/{name}");

    [Fact]
    public void Append_persists_and_records_round_trip_across_a_reload()
    {
        var store = NewStore();
        store.Append(Record("a.bin", HistoryState.Completed, 2048));
        store.Append(Record("b.zip", HistoryState.Failed));
        store.Append(Record("c.iso", HistoryState.Cancelled));

        Assert.True(File.Exists(HistoryPath));

        // A fresh store over the same file sees the same records, in append (chronological) order.
        var reloaded = NewStore().Load();
        Assert.Equal(3, reloaded.Count);
        Assert.Equal(["a.bin", "b.zip", "c.iso"], reloaded.Select(r => r.Name));
        Assert.Equal(2048, reloaded[0].Size);
        Assert.Equal(HistoryState.Completed, reloaded[0].State);
        Assert.Equal(HistoryState.Failed, reloaded[1].State);
        Assert.Equal(HistoryState.Cancelled, reloaded[2].State);
        Assert.Equal("/downloads/a.bin", reloaded[0].SavedPath);
    }

    [Fact]
    public void Missing_file_loads_as_empty_history_and_writes_nothing()
    {
        Assert.False(File.Exists(HistoryPath));

        var store = NewStore();

        Assert.Empty(store.Load());
        Assert.False(File.Exists(HistoryPath)); // load alone does not create the file
    }

    [Fact]
    public void Malformed_file_loads_as_empty_warns_and_is_not_overwritten()
    {
        const string broken = "{ not valid json ";
        File.WriteAllText(HistoryPath, broken);
        var logger = new CollectingLogger<JsonHistoryStore>();

        var store = new JsonHistoryStore(HistoryPath, logger);

        Assert.Empty(store.Load());
        Assert.Equal(broken, File.ReadAllText(HistoryPath)); // user's file untouched
        Assert.Contains(logger.Messages, m => m.Contains("unreadable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Load_returns_a_snapshot_that_does_not_mutate_on_a_later_append()
    {
        var store = NewStore();
        store.Append(Record("a.bin", HistoryState.Completed));

        var snapshot = store.Load();
        store.Append(Record("b.bin", HistoryState.Completed));

        Assert.Single(snapshot);            // the earlier snapshot is unaffected
        Assert.Equal(2, store.Load().Count); // a fresh read sees both
    }
}
