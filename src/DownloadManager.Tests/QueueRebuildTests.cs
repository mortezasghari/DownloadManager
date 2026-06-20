using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Lifecycle;
using DownloadManager.Core.Recovery;
using DownloadManager.Persistence.History;
using DownloadManager.Persistence.Lifecycle;
using DownloadManager.Tests.Fakes;
using DownloadManager.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// Queue rebuild (ADR-0021): the append-only lifecycle-event log is the source of truth; the channel and
/// history are projections. Covers log round-trip + torn-tail, projection reduction, history rebuild,
/// re-add, append-first mutation ordering, and the crash-safety property. Headless.
/// </summary>
public sealed class QueueRebuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dlm-qrebuild", Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, "queue.log");

    private string HistoryPath => Path.Combine(_dir, "history.json");

    public QueueRebuildTests() => Directory.CreateDirectory(_dir);

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

    private JsonLifecycleLog NewLog() => new(LogPath, NullLogger<JsonLifecycleLog>.Instance);

    // The log is a single-owner resource (one singleton in the app). Tests open-read-dispose so a second
    // handle never overlaps the first (Windows FileShare.Read rejects concurrent ReadWrite handles).
    private IReadOnlyList<LifecycleEvent> ReadAllEvents()
    {
        using var log = NewLog();
        return log.ReadAll();
    }

    private void Recover(IHistoryStore history)
    {
        using var log = NewLog();
        new QueueRecoveryService(log, history, NullLogger<QueueRecoveryService>.Instance).Recover();
    }

    private static LifecycleEvent Ev(string id, LifecycleEventType type, string? url = null, string? target = null,
        string? name = null, long size = 0) => new()
    {
        Id = id,
        Type = type,
        Url = url ?? $"https://host.example/{id}.bin",
        TargetPath = target ?? $"/downloads/{id}.bin",
        SegmentCount = 4,
        Name = name ?? $"{id}.bin",
        Size = size,
    };

    private static string NewId() => DownloadId.New().ToString();

    // ---------- 1. Log round-trip + latest-per-id + tombstone + torn tail ----------

    [Fact]
    public void Replay_reconstructs_latest_state_per_id()
    {
        var a = NewId();
        var b = NewId();
        using (var log = NewLog())
        {
            log.Append(Ev(a, LifecycleEventType.Queued));
            log.Append(Ev(b, LifecycleEventType.Queued));
            log.Append(Ev(a, LifecycleEventType.Started));
            log.Append(Ev(a, LifecycleEventType.Completed, size: 100));
        }

        var events = ReadAllEvents();
        var projection = QueueProjection.Reduce(events);

        Assert.Equal(4, events.Count);
        Assert.Equal(b, Assert.Single(projection.Active).Id.ToString());          // b latest = Queued → active
        Assert.Equal(a, Assert.Single(projection.History).Id);                     // a latest = Completed → history
        Assert.True(events.Select(e => e.Sequence).SequenceEqual([1, 2, 3, 4]));   // sequences assigned in order
    }

    [Fact]
    public void A_tombstone_hides_a_deleted_id_from_every_projection()
    {
        var a = NewId();
        using (var log = NewLog())
        {
            log.Append(Ev(a, LifecycleEventType.Queued));
            log.Append(Ev(a, LifecycleEventType.Completed));
            log.Append(Ev(a, LifecycleEventType.Deleted));
        }

        var projection = QueueProjection.Reduce(ReadAllEvents());

        Assert.Empty(projection.Active);
        Assert.Empty(projection.History); // tombstone wins over the prior terminal event
    }

    [Fact]
    public void A_torn_final_record_is_discarded_on_replay_prior_state_intact()
    {
        var a = NewId();
        var b = NewId();
        using (var log = NewLog())
        {
            log.Append(Ev(a, LifecycleEventType.Queued));
            log.Append(Ev(b, LifecycleEventType.Queued));
        }

        // Simulate a crash mid-append: a partial record with no terminating newline.
        File.AppendAllText(LogPath, "{\"Id\":\"" + NewId() + "\",\"Type\":\"Que");

        var events = ReadAllEvents();

        Assert.Equal(2, events.Count); // the two complete records survive; the torn tail is dropped
        Assert.Equal([a, b], events.Select(e => e.Id));
    }

    // ---------- 2. Channel rebuilt from the log ----------

    [Fact]
    public void Recovery_returns_active_downloads_and_rebuilds_history()
    {
        var a = NewId();
        var b = NewId();
        using (var log = NewLog())
        {
            log.Append(Ev(a, LifecycleEventType.Queued));               // active
            log.Append(Ev(b, LifecycleEventType.Queued));
            log.Append(Ev(b, LifecycleEventType.Completed, size: 42));  // terminal → history
        }

        var history = new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance);
        IReadOnlyList<DownloadRequest> active;
        using (var log = NewLog())
        {
            active = new QueueRecoveryService(log, history, NullLogger<QueueRecoveryService>.Instance).Recover();
        }

        Assert.Equal(a, Assert.Single(active).Id.ToString());   // non-terminal enqueued
        Assert.Equal(b, Assert.Single(history.Load()).Id);      // terminal projected to history, not active
        Assert.Equal(42, history.Load()[0].Size);
    }

    // ---------- 3. history.json is a projection (rebuilt from the log) ----------

    [Fact]
    public void Corrupt_or_missing_history_is_rebuilt_from_the_log_identically()
    {
        var a = NewId();
        using (var log = NewLog())
        {
            log.Append(Ev(a, LifecycleEventType.Queued));
            log.Append(Ev(a, LifecycleEventType.Failed, name: "doc.pdf", size: 7));
        }

        // Build the expected history from the log.
        Recover(new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance));
        var expected = new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance).Load();

        // Corrupt history.json, then recover again — it is rebuilt from the log, identical.
        File.WriteAllText(HistoryPath, "}}garbage not json{{");
        var rebuilt = new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance);
        Recover(rebuilt);

        Assert.Equal(expected.Select(r => (r.Id, r.Name, r.Size, r.State)),
            rebuilt.Load().Select(r => (r.Id, r.Name, r.Size, r.State)));
        Assert.Equal("doc.pdf", rebuilt.Load()[0].Name);
        Assert.Equal(HistoryState.Failed, rebuilt.Load()[0].State);
    }

    // ---------- 4. Re-add-from-history appends Queued; appears active AND stays in history ----------

    [Fact]
    public async Task Re_add_from_history_appends_queued_and_appears_active_while_history_remains()
    {
        var original = NewId();
        using (var log = NewLog())
        {
            log.Append(Ev(original, LifecycleEventType.Queued));
            log.Append(Ev(original, LifecycleEventType.Completed, name: "movie.mp4", size: 9));
        }

        var scheduler = new FakeUiScheduler();
        var vmLog = NewLog();
        var vm = NewVm(scheduler, vmLog);

        await vm.ReAddFromHistoryAsync(original);
        vmLog.Dispose(); // release the file before reopening for assertions (Windows share semantics)

        var projection = QueueProjection.Reduce(ReadAllEvents());
        Assert.Single(projection.Active);                              // a new active download exists
        Assert.NotEqual(original, projection.Active[0].Id.ToString()); // …under a fresh id
        Assert.Equal(original, Assert.Single(projection.History).Id);  // the original terminal record remains
        Assert.Single(scheduler.Enqueued);                             // and it was enqueued
    }

    // ---------- 5. Mutation ordering: append precedes in-memory reflect ----------

    [Fact]
    public async Task Enqueue_appends_the_lifecycle_event_before_the_in_memory_reflect()
    {
        var scheduler = new FakeUiScheduler { FailNextEnqueue = true }; // crash during the in-memory step
        var vmLog = NewLog();
        var vm = NewVm(scheduler, vmLog);

        // The in-memory reflect throws, but the append already happened first.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            vm.NewUrl = "https://host.example/x.bin";
            await vm.AddCurrentUrlAsync();
        });
        vmLog.Dispose();

        var events = ReadAllEvents();
        Assert.Equal(LifecycleEventType.Queued, Assert.Single(events).Type); // append happened despite the crash
        Assert.Empty(scheduler.Enqueued);                                    // the in-memory reflect did not
    }

    // ---------- 6. Crash-safety regression: recover a consistent state, no byte loss ----------

    [Fact]
    public async Task Crash_between_append_and_reflect_recovers_the_download_as_active()
    {
        var vmLog = NewLog();
        var scheduler = new FakeUiScheduler { FailNextEnqueue = true };
        var vm = NewVm(scheduler, vmLog);

        // Append-first enqueue, then "crash" (the in-memory enqueue throws).
        try
        {
            vm.NewUrl = "https://host.example/big.iso";
            await vm.AddCurrentUrlAsync();
        }
        catch (InvalidOperationException)
        {
            // The simulated crash. Process "restarts" below.
        }

        vmLog.Dispose(); // the crashed process releases the log

        // Restart: a fresh recovery replays the durable log and finds the download active — nothing lost.
        var history = new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance);
        IReadOnlyList<DownloadRequest> recovered;
        using (var log = NewLog())
        {
            recovered = new QueueRecoveryService(log, history, NullLogger<QueueRecoveryService>.Instance).Recover();
        }

        var request = Assert.Single(recovered);
        Assert.Equal("https://host.example/big.iso", request.Url.ToString());
        Assert.Empty(history.Load()); // not terminal — it is active, recoverable, no corruption
    }

    private MainWindowViewModel NewVm(FakeUiScheduler scheduler, JsonLifecycleLog log) =>
        new(scheduler,
            new FakeTimeProvider(),
            new FakeFilePicker(null),
            new FakeCredentialPrompt(null),
            new FakeImportDialog(),
            NullLogger<MainWindowViewModel>.Instance,
            downloadsDirectory: _dir,
            lifecycleLog: log);
}