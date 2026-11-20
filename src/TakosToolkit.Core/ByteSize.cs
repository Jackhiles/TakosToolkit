using System.Globalization;

namespace TakosToolkit.Core;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Formats a byte count using 1024-based units, e.g. "1.5 GB".</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }
}
