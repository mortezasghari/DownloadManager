using Microsoft.Win32.SafeHandles;

namespace DownloadManager.Persistence.Io;

/// <summary>
/// Writes a file atomically and durably (spec §6a/§6c): write a temp file in the same directory,
/// fsync it, atomically rename it over the target, fsync the directory. An interrupted write leaves
/// either the old file or a stray temp file — never a torn target.
/// </summary>
public static class AtomicFileWriter
{
    public static void WriteAllBytes(string targetPath, ReadOnlySpan<byte> contents)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{targetPath}' has no directory.", nameof(targetPath));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (SafeFileHandle handle = File.OpenHandle(
                       tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                RandomAccess.Write(handle, contents, 0);
                DurableIo.FlushFile(handle);
            }

            DurableIo.AtomicReplace(tempPath, fullPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is harmless and will be ignored on recovery.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}