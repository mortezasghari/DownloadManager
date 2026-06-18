using System.Runtime.InteropServices;
using DownloadManager.Core.Configuration;
using DownloadManager.Persistence.Io;
using Microsoft.Extensions.Logging;

namespace DownloadManager.UI;

/// <summary>
/// Headless self-test invoked as <c>DownloadManager.UI --smoke</c>. It exercises the native full
/// preallocation path in the actual Native-AOT-published binary, without starting the GUI — so it
/// runs on every RID's CI runner (no display needed) and proves the platform P/Invoke
/// (posix_fallocate / F_PREALLOCATE / FILE_ALLOCATION_INFO) executes, rather than inferring it.
/// Exit code 0 = native full preallocation executed; non-zero = failure or silent fallback.
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
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

            if (!capture.Messages.Any(m => m.Contains("native full", StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine($"SMOKE FAIL [{rid}]: native full preallocation did not execute (fell back).");
                return 1;
            }

            Console.WriteLine($"SMOKE OK [{rid}]: native full preallocation executed.");
            return 0;
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