using DownloadManager.Core.Abstractions;
using DownloadManager.Core.Configuration;
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

public sealed class TargetFileFactory : ITargetFileFactory
{
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

    private static void Preallocate(SafeFileHandle handle, long expectedSize, PreallocationMode mode)
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

        // Phase 1: set the logical length with a single end-byte write (sparse on NTFS/ext4/APFS).
        // True up-front block reservation (posix_fallocate / fcntl(F_PREALLOCATE) / FILE_ALLOCATION_INFO)
        // for PreallocationMode.Full lands in Phase 2 — see ADR-0004.
        Span<byte> oneByte = stackalloc byte[1];
        RandomAccess.Write(handle, oneByte, expectedSize - 1);
    }
}