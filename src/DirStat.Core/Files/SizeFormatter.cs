using System.Globalization;

namespace DirStat.Core.Files;

/// <summary>Human-readable byte and count formatting.</summary>
public static class SizeFormatter
{
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private static readonly string[] DecimalUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>Use 1024-based units. Matches what filesystems actually allocate.</summary>
    public static bool UseBinaryUnits { get; set; } = true;

    /// <summary>
    /// Formats a byte count with adaptive precision: more decimals for small magnitudes,
    /// none for raw byte counts, so columns stay visually aligned and readable.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "-" + Format(-bytes);
        if (bytes == 0) return "0 B";

        var units = UseBinaryUnits ? BinaryUnits : DecimalUnits;
        double divisor = UseBinaryUnits ? 1024 : 1000;

        var value = (double)bytes;
        var unit = 0;
        while (value >= divisor && unit < units.Length - 1)
        {
            value /= divisor;
            unit++;
        }

        if (unit == 0) return $"{bytes:N0} B";

        var decimals = value switch
        {
            < 10 => 2,
            < 100 => 1,
            _ => 0,
        };

        return value.ToString($"N{decimals}", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    /// <summary>Formats a count with thousands separators.</summary>
    public static string FormatCount(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Formats a fraction in the range 0..1 as a percentage.</summary>
    public static string FormatPercent(double fraction)
    {
        if (double.IsNaN(fraction) || double.IsInfinity(fraction)) return "0.0 %";
        var percent = fraction * 100;
        var decimals = percent switch
        {
            >= 10 => 1,
            >= 1 => 1,
            > 0 => 2,
            _ => 1,
        };
        return percent.ToString($"N{decimals}", CultureInfo.CurrentCulture) + " %";
    }

    /// <summary>Formats a throughput figure in bytes per second.</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0) return "—";
        return Format((long)bytesPerSecond) + "/s";
    }

    /// <summary>Formats a duration compactly: sub-minute in seconds, longer as m:ss or h:mm:ss.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1) return $"{duration.TotalMilliseconds:N0} ms";
        if (duration.TotalMinutes < 1) return $"{duration.TotalSeconds:N1} s";
        if (duration.TotalHours < 1) return $"{duration.Minutes}:{duration.Seconds:D2}";
        return $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }
}
