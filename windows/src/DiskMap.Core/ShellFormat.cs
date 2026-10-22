using System.Globalization;
using System.Numerics;

namespace DiskMap.Core;

/// <summary>
/// The window frame's small strings, written to match the Mac app character for character
/// (AppShellView.compactBytes, Kit/Redesign.swift RelativeTime, AppShellView.sidebarWidth).
/// Swift's <c>rounded()</c> rounds halves away from zero and <c>String(format: "%.1f")</c>
/// rounds the exact binary value with ties to even; both are reproduced here.
/// </summary>
public static class ShellFormat
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Every size in the UI: macOS ByteCountFormatter (.file): decimal units, "0 bytes", "1 byte",
    /// whole KB, one decimal for MB, two for GB and up, trailing zeros dropped. It rounds the
    /// decimal value half away from zero (1.005 GB is "1.01 GB") and moves up a unit when
    /// rounding reaches 1000 (999.95 MB is "1 GB"). Checked against the Mac formatter.
    /// </summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "-" + Bytes(bytes == long.MinValue ? long.MaxValue : -bytes);
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        int unit = 1;
        decimal scale = 1000m;
        while (unit < Units.Length - 1 && bytes >= scale * 1000m) { scale *= 1000m; unit++; }
        while (true)
        {
            int digits = unit switch { 1 => 0, 2 => 1, _ => 2 };
            decimal value = Math.Round(bytes / scale, digits, MidpointRounding.AwayFromZero);
            if (value >= 1000m && unit < Units.Length - 1) { scale *= 1000m; unit++; continue; }
            return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
        }
    }

    /// <summary>
    /// Big display figures, three significant digits: "120 GB", "28.5 GB", "8.41 GB"
    /// (Mac ByteFormat.hero: repeated /1000 in doubles, then printf "%.Nf").
    /// </summary>
    public static string HeroBytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1000 && unit < Units.Length - 1) { value /= 1000; unit++; }
        if (unit == 0) return $"{(long)value} bytes";
        int digits = value >= 100 ? 0 : value >= 10 ? 1 : 2;
        return Printf(value, digits) + " " + Units[unit];
    }

    /// <summary>printf("%.Nf") — "0.4" seconds, "12.0" — rounded exactly as Swift's String(format:).</summary>
    public static string Fixed(double value, int digits) =>
        value < 0 ? "-" + Printf(-value, digits) : Printf(value, digits);

    /// <summary>A map tile's size (DevChip.short): "26.1" for gigabytes, "640M", "12K".</summary>
    public static string ShortBytes(long bytes)
    {
        if (bytes >= 1_000_000_000) return Printf(bytes / 1e9, 1);
        if (bytes >= 1_000_000) return $"{(long)Math.Round(bytes / 1e6, MidpointRounding.AwayFromZero)}M";
        return $"{Math.Max(1, (long)Math.Round(bytes / 1e3, MidpointRounding.AwayFromZero))}K";
    }

    /// <summary>A nav figure: "31.6G", "640M", "120G".</summary>
    public static string CompactBytes(long bytes)
    {
        double g = bytes / 1_000_000_000.0;
        if (g >= 100) return Printf(g, 0) + "G";
        if (g >= 0.95) return Printf(g, 1) + "G";
        return Printf(Math.Max(1, bytes / 1_000_000.0), 0) + "M";
    }

    /// <summary>"Scanned 2 minutes ago": just now, a minute ago, N minutes, an hour, N hours, yesterday, N days.</summary>
    public static string RelativeTime(DateTimeOffset since, DateTimeOffset now)
    {
        double s = Math.Max(0, (now - since).TotalSeconds);
        if (s < 45) return "just now";
        if (s < 90) return "a minute ago";
        if (s < 3_600) return $"{SwiftRounded(s / 60)} minutes ago";
        if (s < 5_400) return "an hour ago";
        if (s < 86_400) return $"{SwiftRounded(s / 3_600)} hours ago";
        long days = SwiftRounded(s / 86_400);
        return days == 1 ? "yesterday" : $"{days} days ago";
    }

    /// <summary>The sidebar: 200 under a 1400-wide window, 220 under 1700, else 236; times the text scale.</summary>
    public static double SidebarWidth(double windowWidth, double textScale) =>
        Math.Round((windowWidth < 1_400 ? 200 : windowWidth < 1_700 ? 220 : 236) * Math.Max(1, textScale),
            MidpointRounding.AwayFromZero);

    private static long SwiftRounded(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// printf("%.Nf") for a non-negative value: the exact binary value scaled by 10^N, rounded
    /// half to even, then printed. Exact (BigInteger), so 0.95 — 0.9499… in binary — gives
    /// "0.9" as on the Mac, whatever .NET's own formatter does with it.
    /// </summary>
    private static string Printf(double value, int decimals)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xF_FFFF_FFFF_FFFFL;
        BigInteger mantissa = exponent == 0 ? fraction : fraction | (1L << 52);
        int shift = (exponent == 0 ? 1 : exponent) - 1075;   // value = mantissa * 2^shift
        BigInteger numerator = mantissa * BigInteger.Pow(10, decimals);
        BigInteger units;
        if (shift >= 0)
        {
            units = numerator << shift;
        }
        else
        {
            BigInteger denominator = BigInteger.One << -shift;
            units = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
            int half = (remainder * 2).CompareTo(denominator);
            if (half > 0 || (half == 0 && !units.IsEven)) units += 1;
        }
        if (decimals == 0) return units.ToString(CultureInfo.InvariantCulture);
        BigInteger whole = BigInteger.DivRem(units, BigInteger.Pow(10, decimals), out BigInteger part);
        return $"{whole.ToString(CultureInfo.InvariantCulture)}.{part.ToString(CultureInfo.InvariantCulture).PadLeft(decimals, '0')}";
    }
}
