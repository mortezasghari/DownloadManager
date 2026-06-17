namespace DownloadManager.Core.Domain;

/// <summary>Stable identity for a download. Embedded in every log line for diagnosis (spec §9).</summary>
public readonly record struct DownloadId(Guid Value)
{
    public static DownloadId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}