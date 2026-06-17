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

    /// <summary>Linux: reserve real disk blocks for <c>[offset, offset+len)</c>. Returns 0 or an errno (does not set errno).</summary>
    [LibraryImport("libc")]
    internal static partial int posix_fallocate(int fd, long offset, long len);

    /// <summary>Set a file's length (used after macOS F_PREALLOCATE, which reserves blocks but doesn't move EOF).</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int ftruncate(int fd, long length);

    /// <summary>macOS: <c>fcntl(fd, F_PREALLOCATE, &amp;fstore)</c> overload (variadic; pointer arg).</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int fcntl(int fd, int cmd, ref FStore arg);

    // ---- Windows ----

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushFileBuffers(SafeFileHandle hFile);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(
        SafeFileHandle hFile, int fileInformationClass, ref FileAllocationInfo fileInformation, uint dwBufferSize);

    // ---- Constants ----

    internal const int O_RDONLY = 0;

    /// <summary>macOS <c>F_FULLFSYNC</c> command number.</summary>
    internal const int F_FULLFSYNC = 51;

    /// <summary>macOS <c>F_PREALLOCATE</c> command number and flags.</summary>
    internal const int F_PREALLOCATE = 42;
    internal const uint F_ALLOCATECONTIG = 0x2;
    internal const uint F_ALLOCATEALL = 0x4;
    internal const int F_PEOFPOSMODE = 3;

    internal const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    internal const uint MOVEFILE_WRITE_THROUGH = 0x8;

    /// <summary>Windows <c>FILE_INFO_BY_HANDLE_CLASS.FileAllocationInfo</c>.</summary>
    internal const int FileAllocationInfoClass = 5;
}

/// <summary>macOS <c>struct fstore</c> for <c>fcntl(F_PREALLOCATE)</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FStore
{
    public uint fst_flags;
    public int fst_posmode;
    public long fst_offset;
    public long fst_length;
    public long fst_bytesalloc;
}

/// <summary>Windows <c>FILE_ALLOCATION_INFO</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileAllocationInfo
{
    public long AllocationSize;
}