using System.Globalization;

namespace Alexbed.HyperCopy.Core;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(double bytes)
    {
        if (bytes < 0) bytes = 0;
        int u = 0;
        while (bytes >= 1024 && u < Units.Length - 1) { bytes /= 1024; u++; }
        string fmt = u == 0 ? "0" : bytes >= 100 ? "0" : bytes >= 10 ? "0.0" : "0.00";
        return bytes.ToString(fmt, CultureInfo.InvariantCulture) + " " + Units[u];
    }

    public static string Speed(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "<1s";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
