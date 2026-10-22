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
    /// printf("%.Nf") for a non-negative value and N = 0 or 1: the exact binary value scaled by
    /// 10^N, rounded half to even, then printed. Exact (BigInteger), so 0.95 — 0.9499… in
    /// binary — gives "0.9" as on the Mac, whatever .NET's own formatter does with it.
    /// </summary>
    private static string Printf(double value, int decimals)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        int exponent = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xF_FFFF_FFFF_FFFFL;
        BigInteger mantissa = exponent == 0 ? fraction : fraction | (1L << 52);
        int shift = (exponent == 0 ? 1 : exponent) - 1075;   // value = mantissa * 2^shift
        BigInteger numerator = mantissa * (decimals == 0 ? 1 : 10);
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
        BigInteger whole = BigInteger.DivRem(units, 10, out BigInteger tenth);
        return $"{whole.ToString(CultureInfo.InvariantCulture)}.{tenth.ToString(CultureInfo.InvariantCulture)}";
    }
}
