using System.Runtime.InteropServices;
using DownloadManager.Core.Configuration;
using DownloadManager.Core.Domain;
using DownloadManager.Core.History;
using DownloadManager.Core.Lifecycle;
using DownloadManager.Persistence.History;
using DownloadManager.Persistence.Io;
using DownloadManager.Persistence.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownloadManager.UI;

/// <summary>
/// Headless self-test invoked as <c>DownloadManager.UI --smoke</c>. In the actual Native-AOT-published
/// binary, without starting the GUI (so it runs on every RID's CI runner, no display needed), it proves
/// two AOT-fragile paths execute:
/// <list type="number">
/// <item><b>Config load (ADR-0016)</b>: writes a real <c>settings.json</c> and deserializes it back via
/// the source-gen context. Reflection-based binding passes a JIT run and fails AOT publish, so loading
/// config at runtime under AOT is the actual risk the matrix must validate — not just launch.</item>
/// <item><b>Native preallocation (ADR-0006)</b>: exercises the platform P/Invoke
/// (posix_fallocate / F_PREALLOCATE / FILE_ALLOCATION_INFO).</item>
/// </list>
/// Exit code 0 = both passed; non-zero = failure or silent fallback.
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var configResult = RunConfigSelfTest();
        if (configResult != 0)
        {
            return configResult;
        }

        var historyResult = RunHistorySelfTest();
        if (historyResult != 0)
        {
            return historyResult;
        }

        var lifecycleResult = RunLifecycleSelfTest();
        if (lifecycleResult != 0)
        {
            return lifecycleResult;
        }

        var rid = RuntimeInformation.RuntimeIdentifier;
        var capture = new CaptureLogger();
        var factory = new TargetFileFactory(capture);
        var path = Path.Combine(Path.GetTempPath(), $"dlm-smoke-{Guid.NewGuid():N}.bin");
        const long size = 4L * 1024 * 1024;

        try
        {
            var file = factory.Open(path, size, PreallocationMode.Full);
            long length;
            try
            {
                length = file.Length;
            }
            finally
            {
                file.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            var strategy = capture.Messages.FirstOrDefault(m => m.Contains("Preallocat", StringComparison.OrdinalIgnoreCase))
                ?? "(no preallocation logged)";
            Console.WriteLine($"SMOKE [{rid}] length={length} expected={size} :: {strategy}");

            if (length != size)
            {
                Console.Error.WriteLine($"SMOKE FAIL [{rid}]: target length {length}, expected {size}.");
                return 1;
            }

            var nativeFull = capture.Messages.Any(m => m.Contains("native full", StringComparison.OrdinalIgnoreCase));
            if (nativeFull)
            {
                Console.WriteLine($"SMOKE OK [{rid}]: native full preallocation executed.");
                return 0;
            }

            // arm64 macOS cannot reach fcntl(F_PREALLOCATE) under the LibraryImport-only constraint
            // (variadic ABI); the documented fallback sizes the file. See ADR-0006.
            if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
            {
                Console.WriteLine($"SMOKE OK [{rid}]: native full unavailable on arm64 macOS; used sized fallback (ADR-0006).");
                return 0;
            }

            Console.Error.WriteLine($"SMOKE FAIL [{rid}]: native full preallocation did not execute (fell back).");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMOKE FAIL [{rid}]: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Proves the lifecycle-event log (<c>queue.log</c>) round-trips under Native AOT via its source-gen
    /// context (ADR-0021) — the new serialization path this phase adds. Appends one event, replays it via a
    /// fresh log, and confirms the type survived. Throwaway temp dir; exit code 0 on success.
    /// </summary>
    private static int RunLifecycleSelfTest()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var dir = Path.Combine(Path.GetTempPath(), $"dlm-smoke-life-{Guid.NewGuid():N}");
        var logPath = Path.Combine(dir, "queue.log");

        try
        {
            Directory.CreateDirectory(dir);
            var id = DownloadId.New().ToString();
            using (var log = new JsonLifecycleLog(logPath, NullLogger<JsonLifecycleLog>.Instance))
            {
                log.Append(new LifecycleEvent
                {
                    Id = id, Type = LifecycleEventType.Queued,
                    Url = "https://host.example/smoke.bin", TargetPath = Path.Combine(dir, "smoke.bin"), SegmentCount = 4,
                });
            }

            using var reopened = new JsonLifecycleLog(logPath, NullLogger<JsonLifecycleLog>.Instance);
            var events = reopened.ReadAll();
            if (events.Count != 1 || events[0].Type != LifecycleEventType.Queued || events[0].Id != id)
            {
                Console.Error.WriteLine($"SMOKE FAIL [{rid}]: queue.log did not round-trip under AOT.");
                return 1;
            }

            Console.WriteLine($"LIFECYCLE OK [{rid}]: queue.log written + replayed under AOT (type={events[0].Type}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMOKE FAIL [{rid}]: lifecycle log threw: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Proves <c>history.json</c> round-trips under Native AOT via its source-gen context (ADR-0019),
    /// exercising the string-enum converter path (new in Phase 9) — the one serialization feature the
    /// config self-test does not cover. Writes one record, reloads via a fresh store, and confirms the
    /// terminal state survived. Throwaway temp dir; exit code 0 on success.
    /// </summary>
    private static int RunHistorySelfTest()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var dir = Path.Combine(Path.GetTempPath(), $"dlm-smoke-hist-{Guid.NewGuid():N}");
        var historyPath = Path.Combine(dir, "history.json");

        try
        {
            Directory.CreateDirectory(dir);
            var record = HistoryRecord.From(
                DownloadId.New(), "smoke.bin", 4096, HistoryState.Completed, Path.Combine(dir, "smoke.bin"));
            new JsonHistoryStore(historyPath, NullLogger<JsonHistoryStore>.Instance).Append(record);

            var reloaded = new JsonHistoryStore(historyPath, NullLogger<JsonHistoryStore>.Instance).Load();
            if (reloaded.Count != 1 || reloaded[0].State != HistoryState.Completed)
            {
                Console.Error.WriteLine($"SMOKE FAIL [{rid}]: history.json did not round-trip under AOT.");
                return 1;
            }

            Console.WriteLine($"HISTORY OK [{rid}]: history.json written + deserialized under AOT (state={reloaded[0].State}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMOKE FAIL [{rid}]: history load threw: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Proves <c>settings.json</c> can be written and read back under Native AOT via the source-gen
    /// context. Uses a throwaway temp config dir so the runner's real config is untouched. The second
    /// load deserializes the real on-disk file written by the first — the path that fails if any
    /// reflection-based binding slipped in. Exit code 0 on success.
    /// </summary>
    private static int RunConfigSelfTest()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var capture = new CaptureLogger();
        var dir = Path.Combine(Path.GetTempPath(), $"dlm-smoke-cfg-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(dir, "settings.json");

        try
        {
            // First load: writes a default settings.json (first-run behavior).
            var written = SettingsStore.LoadOrCreate(settingsPath, capture, userProfile: dir);
            if (!File.Exists(settingsPath))
            {
                Console.Error.WriteLine($"SMOKE FAIL [{rid}]: first-run did not write {settingsPath}.");
                return 1;
            }

            // Second load: deserialize the real on-disk file under AOT (the binding risk).
            var loaded = SettingsStore.LoadOrCreate(settingsPath, capture, userProfile: dir);
            if (loaded.Scheduler.MaxConcurrentDownloads != written.Scheduler.MaxConcurrentDownloads
                || loaded.Engine.MaxSegmentsPerDownload != written.Engine.MaxSegmentsPerDownload)
            {
                Console.Error.WriteLine($"SMOKE FAIL [{rid}]: settings did not round-trip.");
                return 1;
            }

            Console.WriteLine(
                $"CONFIG OK [{rid}]: settings.json written + deserialized under AOT "
                + $"(maxConcurrent={loaded.Scheduler.MaxConcurrentDownloads}, segments={loaded.Defaults.SegmentCount}).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMOKE FAIL [{rid}]: config load threw: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Captures formatted log messages so the chosen preallocation strategy is observable.</summary>
    private sealed class CaptureLogger : ILogger<TargetFileFactory>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}