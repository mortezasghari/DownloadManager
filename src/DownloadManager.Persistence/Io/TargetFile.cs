using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Persistence.Io;

/// <summary>
/// <see cref="ITargetFile"/> over a single shared <see cref="SafeFileHandle"/> using positioned
/// <see cref="RandomAccess"/> writes (spec §5). Concurrent writers to distinct, non-overlapping
/// offset ranges are safe because positioned writes never touch a shared cursor.
/// </summary>
public sealed class TargetFile(SafeFileHandle handle) : ITargetFile
{
    private readonly SafeFileHandle _handle = handle;

    public long Length => RandomAccess.GetLength(_handle);

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        RandomAccess.WriteAsync(_handle, buffer, offset, cancellationToken);

    public void FlushToDisk() => DurableIo.FlushFile(_handle);

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed partial class TargetFileFactory : ITargetFileFactory
{
    private readonly ILogger<TargetFileFactory> _logger;
    private readonly Func<SafeFileHandle, long, bool> _tryAllocateFull;

    public TargetFileFactory(ILogger<TargetFileFactory> logger)
        : this(logger, NativePreallocator.TryAllocateFull)
    {
    }

    // Test seam: lets a unit test force the native Full allocation to "fail" and exercise the fallback.
    internal TargetFileFactory(ILogger<TargetFileFactory> logger, Func<SafeFileHandle, long, bool> tryAllocateFull)
    {
        _logger = logger;
        _tryAllocateFull = tryAllocateFull;
    }

    public ITargetFile Open(string path, long expectedSize, PreallocationMode mode)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // FileShare.Read so a post-completion checksum pass can read while we hold the handle.
        var handle = File.OpenHandle(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);

        try
        {
            Preallocate(handle, expectedSize, mode);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new TargetFile(handle);
    }

    /// <summary>
    /// Reserves space for the target. The layout + size are already persisted to <c>.dlmeta</c> before
    /// this runs, so recovery knows the intended size regardless of how allocation goes. On failure the
    /// mode degrades Full → Sparse → None with a logged warning; preallocation never aborts the
    /// download (spec §5, ADR-0006).
    /// </summary>
    private void Preallocate(SafeFileHandle handle, long expectedSize, PreallocationMode mode)
    {
        if (mode == PreallocationMode.None || expectedSize <= 0)
        {
            return;
        }

        // Resuming a file that's already at least the expected size: nothing to do.
        if (RandomAccess.GetLength(handle) >= expectedSize)
        {
            return;
        }

        if (mode == PreallocationMode.Full)
        {
            if (_tryAllocateFull(handle, expectedSize))
            {
                LogNativeFull(expectedSize);
                return;
            }

            LogFullFailed(expectedSize);
            mode = PreallocationMode.Sparse;
        }

        if (mode == PreallocationMode.Sparse)
        {
            if (TrySetLength(handle, expectedSize))
            {
                return;
            }

            LogSparseFailed(expectedSize);
            // Falls through to None: positioned writes will extend the file as data arrives.
        }
    }

    /// <summary>Sets the logical length via a single end-byte write (sparse on NTFS/ext4/APFS).</summary>
    private static bool TrySetLength(SafeFileHandle handle, long size)
    {
        try
        {
            Span<byte> oneByte = stackalloc byte[1];
            RandomAccess.Write(handle, oneByte, size - 1);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Preallocated {Size} bytes via native full reservation.")]
    private partial void LogNativeFull(long size);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Full preallocation of {Size} bytes failed (unsupported filesystem or no space); falling back to sparse.")]
    private partial void LogFullFailed(long size);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Sparse preallocation of {Size} bytes failed; continuing with no preallocation.")]
    private partial void LogSparseFailed(long size);
}