namespace DownloadManager.Core.Domain;

/// <summary>Stable identity for a download. Embedded in every log line for diagnosis (spec §9).</summary>
public readonly record struct DownloadId(Guid Value)
{
    public static DownloadId New() => new(Guid.NewGuid());

    /// <summary>Parse the canonical 32-char hex form produced by <see cref="ToString"/> (e.g. for lifecycle-log replay).</summary>
    public static DownloadId Parse(string value) => new(Guid.ParseExact(value, "N"));

    public override string ToString() => Value.ToString("N");
}