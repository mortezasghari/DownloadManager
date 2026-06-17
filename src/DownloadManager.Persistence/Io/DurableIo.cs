using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Persistence.Io;

/// <summary>
/// Cross-platform durability primitives built on <see cref="NativeMethods"/>. These are the only
/// things in the codebase that can make bytes durable; the durability ordering in §6c is expressed
/// purely in terms of <see cref="FlushFile"/> and <see cref="AtomicReplace"/>.
/// </summary>
public static class DurableIo
{
    /// <summary>fsync a file handle to durable storage (F_FULLFSYNC on macOS, FlushFileBuffers on Windows).</summary>
    public static void FlushFile(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (OperatingSystem.IsWindows())
        {
            if (!NativeMethods.FlushFileBuffers(handle))
            {
                throw new IOException("FlushFileBuffers failed.", Marshal.GetLastPInvokeError());
            }

            return;
        }

        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();

            if (OperatingSystem.IsMacOS())
            {
                // Prefer F_FULLFSYNC; some filesystems (e.g. SMB) don't support it, so fall back to fsync.
                if (NativeMethods.fcntl(fd, NativeMethods.F_FULLFSYNC, 0) == 0)
                {
                    return;
                }
            }

            if (NativeMethods.fsync(fd) != 0)
            {
                throw new IOException("fsync failed.", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// fsync a directory so a freshly created/renamed entry within it is durable. On Windows this is
    /// a no-op: NTFS directory metadata for a <see cref="AtomicReplace"/> is made durable by the
    /// MOVEFILE_WRITE_THROUGH flag instead.
    /// </summary>
    public static void FlushDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fd = NativeMethods.open(directoryPath, NativeMethods.O_RDONLY);
        if (fd < 0)
        {
            throw new IOException($"open('{directoryPath}') failed.", Marshal.GetLastPInvokeError());
        }

        try
        {
            if (NativeMethods.fsync(fd) != 0)
            {
                throw new IOException($"fsync of directory '{directoryPath}' failed.", Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            NativeMethods.close(fd);
        }
    }

    /// <summary>
    /// Atomically replace <paramref name="targetPath"/> with <paramref name="tempPath"/> and make the
    /// rename durable. On POSIX this is <c>rename(2)</c> (atomic within a filesystem) followed by a
    /// directory fsync; on Windows it is <c>MoveFileEx</c> with REPLACE_EXISTING | WRITE_THROUGH.
    /// </summary>
    public static void AtomicReplace(string tempPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(tempPath);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        if (OperatingSystem.IsWindows())
        {
            if (!NativeMethods.MoveFileExW(
                    tempPath, targetPath,
                    NativeMethods.MOVEFILE_REPLACE_EXISTING | NativeMethods.MOVEFILE_WRITE_THROUGH))
            {
                throw new IOException($"MoveFileEx '{tempPath}' -> '{targetPath}' failed.", Marshal.GetLastPInvokeError());
            }

            return;
        }

        File.Move(tempPath, targetPath, overwrite: true);
        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(directory))
        {
            FlushDirectory(directory);
        }
    }
}