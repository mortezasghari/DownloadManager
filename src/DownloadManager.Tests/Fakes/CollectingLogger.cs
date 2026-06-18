using Microsoft.Extensions.Logging;

namespace DownloadManager.Tests.Fakes;

/// <summary>Minimal <see cref="ILogger{T}"/> that records formatted messages, for asserting that a
/// specific code path (e.g. native preallocation) actually executed rather than inferring it.</summary>
internal sealed class CollectingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Messages)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}