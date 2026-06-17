using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Persistence.Io;

/// <summary>
/// Real, up-front block reservation for the target file (spec §5, ADR-0006). Returns <c>false</c>
/// — never throws — on any failure (unsupported filesystem, ENOSPC, …) so the caller can fall back
/// down the Full → Sparse → None chain rather than aborting the download. All native calls go
/// through <see cref="LibraryImport"/> (no <c>DllImport</c>).
/// </summary>
internal static class NativePreallocator
{
    public static bool TryAllocateFull(SafeFileHandle handle, long size)
    {
        if (size <= 0)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryWindows(handle, size);
        }

        if (OperatingSystem.IsMacOS())
        {
            return TryMacOs(handle, size);
        }

        if (OperatingSystem.IsLinux())
        {
            return TryLinux(handle, size);
        }

        return false;
    }

    private static bool TryLinux(SafeFileHandle handle, long size)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();
            // posix_fallocate returns 0 on success or an errno (e.g. EOPNOTSUPP, ENOSPC) directly.
            // It also extends the file length to `size`, so no ftruncate is needed.
            return NativeMethods.posix_fallocate(fd, 0, size) == 0;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static bool TryMacOs(SafeFileHandle handle, long size)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var fd = (int)handle.DangerousGetHandle();

            // Prefer a contiguous reservation, then fall back to any-layout; F_PREALLOCATE reserves
            // blocks but does not move EOF, so ftruncate sets the final length.
            var store = new FStore
            {
                fst_flags = NativeMethods.F_ALLOCATECONTIG,
                fst_posmode = NativeMethods.F_PEOFPOSMODE,
                fst_offset = 0,
                fst_length = size,
                fst_bytesalloc = 0,
            };

            if (NativeMethods.fcntl(fd, NativeMethods.F_PREALLOCATE, ref store) != 0)
            {
                store.fst_flags = NativeMethods.F_ALLOCATEALL;
                store.fst_bytesalloc = 0;
                if (NativeMethods.fcntl(fd, NativeMethods.F_PREALLOCATE, ref store) != 0)
                {
                    return false;
                }
            }

            return NativeMethods.ftruncate(fd, size) == 0;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static bool TryWindows(SafeFileHandle handle, long size)
    {
        var info = new FileAllocationInfo { AllocationSize = size };
        var ok = NativeMethods.SetFileInformationByHandle(
            handle, NativeMethods.FileAllocationInfoClass, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<FileAllocationInfo>());
        if (!ok)
        {
            return false;
        }

        // FILE_ALLOCATION_INFO reserves clusters but leaves EOF at 0; set the length so the file
        // reports its full size (the bytes are allocated, not sparse) by writing the final byte.
        Span<byte> oneByte = stackalloc byte[1];
        RandomAccess.Write(handle, oneByte, size - 1);
        return true;
    }
}