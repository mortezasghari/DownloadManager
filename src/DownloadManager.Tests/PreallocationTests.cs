using System.Runtime.InteropServices;
using DownloadManager.Core.Configuration;
using DownloadManager.Persistence.Io;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DownloadManager.Tests;

public sealed class PreallocationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "dlm-prealloc-tests", Guid.NewGuid().ToString("N"));

    private string TargetPath => Path.Combine(_directory, "file.bin");

    public PreallocationTests() => Directory.CreateDirectory(_directory);

    private static TargetFileFactory Factory() => new(NullLogger<TargetFileFactory>.Instance);

    // Forces native Full allocation to "fail" so the fallback chain is exercised.
    private static TargetFileFactory FactoryWithFailingFullAllocation() =>
        new(NullLogger<TargetFileFactory>.Instance, (SafeFileHandle _, long _) => false);

    [Fact]
    public async Task Full_preallocation_sets_the_file_to_the_expected_size()
    {
        await using (Factory().Open(TargetPath, 100_000, PreallocationMode.Full))
        {
        }

        Assert.Equal(100_000, new FileInfo(TargetPath).Length);
    }

    [Fact]
    public async Task Full_preallocation_actually_runs_the_native_path_on_this_os()
    {
        // Proves the platform's native reservation (posix_fallocate / F_PREALLOCATE /
        // FILE_ALLOCATION_INFO) executed — not that the end state was merely reached via fallback.
        var logger = new Fakes.CollectingLogger<TargetFileFactory>();
        var factory = new TargetFileFactory(logger);

        await using (factory.Open(TargetPath, 100_000, PreallocationMode.Full))
        {
        }

        Assert.Equal(100_000, new FileInfo(TargetPath).Length);

        if (NativeFullUnavailable)
        {
            // arm64 macOS: fcntl(F_PREALLOCATE) is variadic and not callable via LibraryImport
            // (DllImport/__arglist forbidden by spec), so Full degrades to a sized file. See ADR-0006.
            Assert.Contains(logger.Messages, m => m.Contains("falling back", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Contains(logger.Messages, m => m.Contains("native full", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(logger.Messages, m => m.Contains("falling back", StringComparison.OrdinalIgnoreCase));
        }
    }

    // Native full reservation is unreachable on arm64 macOS: fcntl(F_PREALLOCATE) is variadic and the
    // arm64 calling convention for it isn't expressible via LibraryImport (DllImport/__arglist forbidden
    // by spec). This is a property of the *process* ABI, not the OS — an x86_64 process (incl. running
    // under Rosetta 2 on Apple Silicon) uses the x64 ABI where the call marshals correctly, so it must
    // gate on ProcessArchitecture, not OSArchitecture (which reports the host as Arm64 under Rosetta).
    private static bool NativeFullUnavailable =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    [Fact]
    public async Task Full_falls_back_to_sparse_when_native_allocation_is_unsupported()
    {
        // Native Full fails (simulating an unsupported FS / ENOSPC); the file must still reach size.
        await using (FactoryWithFailingFullAllocation().Open(TargetPath, 100_000, PreallocationMode.Full))
        {
        }

        Assert.Equal(100_000, new FileInfo(TargetPath).Length);
    }

    [Fact]
    public async Task Sparse_sets_the_length()
    {
        await using (Factory().Open(TargetPath, 50_000, PreallocationMode.Sparse))
        {
        }

        Assert.Equal(50_000, new FileInfo(TargetPath).Length);
    }

    [Fact]
    public async Task None_does_not_preallocate()
    {
        await using (Factory().Open(TargetPath, 50_000, PreallocationMode.None))
        {
        }

        Assert.Equal(0, new FileInfo(TargetPath).Length);
    }

    [Fact]
    public async Task Resuming_an_already_sized_file_preserves_its_contents()
    {
        var existing = EngineHarnessBytes(100_000);
        await File.WriteAllBytesAsync(TargetPath, existing, CancellationToken.None);

        await using (Factory().Open(TargetPath, 100_000, PreallocationMode.Full))
        {
        }

        Assert.Equal(existing, await File.ReadAllBytesAsync(TargetPath, CancellationToken.None));
    }

    private static byte[] EngineHarnessBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}