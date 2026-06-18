namespace DownloadManager.UI.ViewModels;

/// <summary>Human-readable formatting for byte sizes, transfer rates, and ETA. Pure BCL, culture-invariant.</summary>
public static class DisplayFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {Units[unit]}" : $"{value:0.0} {Units[unit]}";
    }

    /// <summary>Rate as e.g. "1.2 MB/s"; "—" when speed is unknown (null).</summary>
    public static string Rate(double? bytesPerSecond) =>
        bytesPerSecond is { } rate && rate >= 0 ? $"{Bytes((long)rate)}/s" : "—";

    /// <summary>
    /// ETA from remaining bytes and smoothed speed. "—" when speed is unknown/zero or the total is
    /// unknown — never "Infinity" (spec Phase 5).
    /// </summary>
    public static string Eta(long completedBytes, long totalBytes, double? bytesPerSecond)
    {
        if (totalBytes <= 0 || bytesPerSecond is not { } rate || rate <= 0)
        {
            return "—";
        }

        var remaining = totalBytes - completedBytes;
        if (remaining <= 0)
        {
            return "00:00";
        }

        var eta = TimeSpan.FromSeconds(remaining / rate);
        return eta.TotalHours >= 1
            ? $"{(int)eta.TotalHours:00}:{eta.Minutes:00}:{eta.Seconds:00}"
            : $"{eta.Minutes:00}:{eta.Seconds:00}";
    }
}