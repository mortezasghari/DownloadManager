namespace DownloadManager.Core.Configuration;

/// <summary>
/// How the target file is preallocated before segment writes (spec §5). <see cref="Full"/> reserves
/// real blocks up front (best for fragmentation / early ENOSPC detection); <see cref="Sparse"/> only
/// sets the length; <see cref="None"/> lets positioned writes extend the file naturally (kinder to
/// sync-folder setups). Native full allocation lands in Phase 2.
/// </summary>
public enum PreallocationMode
{
    Full,
    Sparse,
    None,
}