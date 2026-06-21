using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Lifecycle;
using DownloadManager.Core.Recovery;
using DownloadManager.Persistence.History;
using DownloadManager.Persistence.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DownloadManager.Tests;

/// <summary>
/// PHASE 1 — adversarial validation of the queue-rebuild foundation (event-log source of truth; channel +
/// history projections, ADR-0021). Goes beyond the rebuild's build gate to prove the load-bearing
/// properties hold under hostile conditions before features are stacked on them: replay determinism,
/// tombstone+re-add ordering, torn-tail/corruption recovery, and projection-vs-truth reconciliation.
/// </summary>
public sealed class QueueFoundationValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dlm-qfound", Guid.NewGuid().ToString("N"));

    private string LogPath => Path.Combine(_dir, "queue.log");

    private string HistoryPath => Path.Combine(_dir, "history.json");

    public QueueFoundationValidationTests() => Directory.CreateDirectory(_dir);

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

    private IReadOnlyList<LifecycleEvent> ReadAll()
    {
        using var log = NewLog();
        return log.ReadAll();
    }

    private void Append(params LifecycleEvent[] events)
    {
        using var log = NewLog();
        foreach (var e in events)
        {
            log.Append(e);
        }
    }

    private static LifecycleEvent Ev(string id, LifecycleEventType type, string? name = null, long size = 0) => new()
    {
        Id = id,
        Type = type,
        Url = $"https://host.example/{id}.bin",
        TargetPath = $"/downloads/{id}.bin",
        SegmentCount = 4,
        Name = name ?? $"{id}.bin",
        Size = size,
    };

    private static string NewId() => DownloadId.New().ToString();

    private static (IReadOnlyList<string> Active, IReadOnlyList<string> History) Project(IReadOnlyList<LifecycleEvent> events)
    {
        var p = QueueProjection.Reduce(events);
        return (p.Active.Select(r => r.Id.ToString()).ToArray(), p.History.Select(r => r.Id).ToArray());
    }

    // Element-wise projection equality (a tuple of arrays compares the arrays by reference, not content).
    private static void AssertSameProjection(
        (IReadOnlyList<string> Active, IReadOnlyList<string> History) expected,
        (IReadOnlyList<string> Active, IReadOnlyList<string> History) actual)
    {
        Assert.Equal(expected.Active, actual.Active);
        Assert.Equal(expected.History, actual.History);
    }

    // ===================== Property 1: replay determinism / idempotence =====================

    [Fact]
    public void Replaying_the_same_log_twice_yields_byte_identical_projections()
    {
        var a = NewId();
        var b = NewId();
        var c = NewId();
        Append(Ev(a, LifecycleEventType.Queued), Ev(b, LifecycleEventType.Queued),
            Ev(a, LifecycleEventType.Started), Ev(c, LifecycleEventType.Queued),
            Ev(b, LifecycleEventType.Completed, size: 10), Ev(c, LifecycleEventType.Failed));

        var first = Project(ReadAll());
        var second = Project(ReadAll());

        Assert.Equal(first.Active, second.Active);   // same order, same ids
        Assert.Equal(first.History, second.History);
        Assert.Equal([a], first.Active);             // a Started; b Completed, c Failed → history
        Assert.Equal([b, c], first.History);
    }

    [Fact]
    public void Reduce_is_a_pure_function_order_stable_across_independent_replays()
    {
        // Build a log with interleaved ids; first-seen order must be preserved deterministically.
        var ids = Enumerable.Range(0, 8).Select(_ => NewId()).ToArray();
        var events = new List<LifecycleEvent>();
        foreach (var id in ids)
        {
            events.Add(Ev(id, LifecycleEventType.Queued));
        }

        Append([.. events]);

        var r1 = Project(ReadAll());
        var r2 = Project(ReadAll());
        Assert.Equal(r1.Active, r2.Active);
        Assert.Equal(ids, r1.Active); // first-seen append order preserved
    }

    [Fact]
    public void A_partial_replay_then_more_appends_converges_to_the_full_replay_state()
    {
        var a = NewId();
        var b = NewId();
        Append(Ev(a, LifecycleEventType.Queued));
        var afterPartial = Project(ReadAll());            // "restart" #1
        Assert.Equal([a], afterPartial.Active);

        Append(Ev(b, LifecycleEventType.Queued), Ev(a, LifecycleEventType.Completed)); // more work, "restart" #2
        var afterFull = Project(ReadAll());

        Assert.Equal([b], afterFull.Active);   // a completed → history; b active
        Assert.Equal([a], afterFull.History);
        // Replaying the full log again is identical — convergent, not path-dependent.
        AssertSameProjection(afterFull, Project(ReadAll()));
    }

    // ===================== Property 2: tombstone + re-add ordering =====================

    [Fact]
    public void A_tombstone_stays_gone_across_many_later_appends_and_replays()
    {
        var x = NewId();
        Append(Ev(x, LifecycleEventType.Queued), Ev(x, LifecycleEventType.Completed), Ev(x, LifecycleEventType.Deleted));

        // Pile on many unrelated events afterward.
        for (var i = 0; i < 20; i++)
        {
            Append(Ev(NewId(), LifecycleEventType.Queued));
        }

        var p = Project(ReadAll());
        Assert.DoesNotContain(x, p.Active);
        Assert.DoesNotContain(x, p.History);
        Assert.Equal(20, p.Active.Count);                  // the 20 others remain
        AssertSameProjection(p, Project(ReadAll()));         // stable across replays
    }

    [Fact]
    public void A_re_add_after_a_delete_for_the_same_id_resurrects_it_the_later_event_wins()
    {
        var x = NewId();
        Append(
            Ev(x, LifecycleEventType.Queued),
            Ev(x, LifecycleEventType.Completed),
            Ev(x, LifecycleEventType.Deleted),     // tombstoned
            Ev(x, LifecycleEventType.Queued));     // re-added AFTER the tombstone (higher sequence)

        var p = Project(ReadAll());

        Assert.Equal([x], p.Active);          // resurrected — the later Queued wins
        Assert.DoesNotContain(x, p.History);
    }

    [Fact]
    public void Delete_then_re_add_then_delete_again_ends_deleted()
    {
        var x = NewId();
        Append(
            Ev(x, LifecycleEventType.Queued),
            Ev(x, LifecycleEventType.Deleted),
            Ev(x, LifecycleEventType.Queued),
            Ev(x, LifecycleEventType.Deleted)); // final word is delete

        var p = Project(ReadAll());
        Assert.Empty(p.Active);
        Assert.Empty(p.History);
    }

    // ===================== Property 3: torn-tail / corruption recovery =====================

    [Fact]
    public void An_empty_log_replays_to_nothing()
    {
        File.WriteAllBytes(LogPath, []);
        Assert.Empty(ReadAll());
    }

    [Fact]
    public void A_log_containing_only_a_torn_record_replays_to_nothing()
    {
        File.WriteAllText(LogPath, "{\"Id\":\"abc\",\"Type\":\"Qu"); // no terminating newline, never completed
        Assert.Empty(ReadAll());                                    // discarded, no crash
    }

    [Fact]
    public void A_half_written_final_event_is_discarded_prior_state_intact()
    {
        var a = NewId();
        var b = NewId();
        Append(Ev(a, LifecycleEventType.Queued), Ev(b, LifecycleEventType.Queued));
        // A crash mid-append: a partial, unterminated final record.
        File.AppendAllText(LogPath, "{\"Id\":\"" + NewId() + "\",\"Type\":\"Qu  ");

        var events = ReadAll();
        Assert.Equal([a, b], events.Select(e => e.Id));
    }

    [Fact]
    public void A_file_truncated_mid_record_recovers_to_the_last_complete_record()
    {
        var a = NewId();
        var b = NewId();
        Append(Ev(a, LifecycleEventType.Queued), Ev(b, LifecycleEventType.Queued));

        // Truncate the file partway into the (now) final record's bytes.
        var len = new FileInfo(LogPath).Length;
        using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(len - 10); // chop the tail of b's record (mid-record truncation)
        }

        var events = ReadAll();
        Assert.Equal([a], events.Select(e => e.Id)); // only the first complete record survives
    }

    [Fact]
    public void A_corrupt_complete_record_stops_replay_at_that_point_prior_intact_no_crash()
    {
        var a = NewId();
        Append(Ev(a, LifecycleEventType.Queued));
        // A complete (newline-terminated) but unparseable record after a, then a valid-looking one.
        File.AppendAllText(LogPath, "this is not json at all\n");
        File.AppendAllText(LogPath, "{\"Id\":\"zzz\",\"Type\":\"Queued\",\"Sequence\":99}\n");

        var events = ReadAll();
        Assert.Equal([a], events.Select(e => e.Id)); // stops at the corrupt record; no crash
    }

    [Fact]
    public void A_torn_tail_then_a_shorter_append_does_not_resurrect_leftover_garbage_on_restart()
    {
        // The adversarial case: a torn tail is logically discarded, a new (shorter) record is appended over
        // it, and on the NEXT restart the leftover bytes beyond the new record must not be misread.
        var a = NewId();
        Append(Ev(a, LifecycleEventType.Queued));
        File.AppendAllText(LogPath, "{\"Id\":\"" + NewId() + "\",\"Type\":\"Started\",\"Url\":\"https://x/very/long/torn/tail/that/was/never/finished");

        var b = NewId();
        Append(Ev(b, LifecycleEventType.Queued)); // overwrites from the discarded torn offset

        // Reopen again (a second restart) and replay: only a and b, never the torn garbage.
        var events = ReadAll();
        Assert.Equal([a, b], events.Select(e => e.Id));
        AssertSameProjection(Project(ReadAll()), Project(ReadAll())); // and still deterministic
    }

    // ===================== Property 4: projection-vs-truth reconciliation =====================

    [Fact]
    public void A_corrupt_history_json_is_reconciled_to_the_log_and_never_poisons_the_active_projection()
    {
        var active = NewId();
        var done = NewId();
        Append(Ev(active, LifecycleEventType.Queued), Ev(done, LifecycleEventType.Completed, name: "d.bin", size: 5));

        // history.json is corrupt garbage before recovery runs.
        File.WriteAllText(HistoryPath, "}{ not even close to json ][");

        var history = new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance); // reads corrupt → empty
        IReadOnlyList<DownloadRequest> recovered;
        using (var log = NewLog())
        {
            recovered = new QueueRecoveryService(log, history, NullLogger<QueueRecoveryService>.Instance).Recover();
        }

        // Active projection derived purely from the log — corrupt history can't touch it.
        Assert.Equal(active, Assert.Single(recovered).Id.ToString());
        // history.json reconciled to the log's terminal projection.
        Assert.Equal(done, Assert.Single(history.Load()).Id);
        Assert.Equal("d.bin", history.Load()[0].Name);
        // The corrupt file was replaced with valid JSON (a fresh store reads it cleanly).
        Assert.Equal(done, Assert.Single(new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance).Load()).Id);
    }

    [Fact]
    public void A_stale_history_json_is_rebuilt_to_match_the_log_exactly()
    {
        // Stale history claims an entry the log never had; the log has a different terminal set.
        var stale = new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance);
        stale.Append(HistoryRecord.From(DownloadId.New(), "ghost.bin", 1, HistoryState.Completed, "/d/ghost.bin"));

        var real = NewId();
        Append(Ev(real, LifecycleEventType.Failed, name: "real.bin", size: 9));

        var history = new JsonHistoryStore(HistoryPath, NullLogger<JsonHistoryStore>.Instance);
        using (var log = NewLog())
        {
            new QueueRecoveryService(log, history, NullLogger<QueueRecoveryService>.Instance).Recover();
        }

        var records = history.Load();
        Assert.Equal(real, Assert.Single(records).Id); // ghost gone; only the log's truth remains
        Assert.Equal(HistoryState.Failed, records[0].State);
    }
}