using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Persistence.Io;

/// <summary>
/// Platform syscalls used for durability. All declared with <see cref="LibraryImportAttribute"/>
/// (source-generated marshalling, AOT-safe — spec §5 forbids <c>DllImport</c>). Each import is only
/// invoked on its own OS (guarded by <see cref="OperatingSystem"/>), so the unused libc/kernel32
/// entry points on the other platform are never resolved.
/// </summary>
internal static partial class NativeMethods
{
    // ---- POSIX (Linux, macOS) ----

    /// <summary>Flush file data + metadata to the storage device.</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int fsync(int fd);

    /// <summary>
    /// macOS only: <c>fcntl(fd, F_FULLFSYNC)</c> asks the drive to flush its own cache. Plain
    /// <c>fsync</c> on macOS does <b>not</b> do this, so F_FULLFSYNC is required for real durability.
    /// </summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int open(string pathname, int flags);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int close(int fd);

    // ---- Windows ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeFileHandle hFile);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    // ---- Constants ----

    internal const int O_RDONLY = 0;

    /// <summary>macOS <c>F_FULLFSYNC</c> command number.</summary>
    internal const int F_FULLFSYNC = 51;

    internal const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    internal const uint MOVEFILE_WRITE_THROUGH = 0x8;
}