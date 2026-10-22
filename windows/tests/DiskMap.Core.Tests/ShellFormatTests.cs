using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>The frame's strings match the Mac's output (values checked with Swift's formatter).</summary>
public class ShellFormatTests
{
    [Theory]
    [InlineData(28_540_000_000, "28.5G")]
    [InlineData(31_640_000_000, "31.6G")]
    [InlineData(950_000_000, "0.9G")]      // the G threshold; 0.95 is 0.9499… in binary
    [InlineData(960_000_000, "1.0G")]
    [InlineData(949_000_000, "949M")]
    [InlineData(640_000_000, "640M")]
    [InlineData(400_000, "1M")]            // never "0M"
    [InlineData(0, "1M")]
    [InlineData(100_000_000_000, "100G")]
    [InlineData(183_860_000_000, "184G")]
    [InlineData(2_250_000_000, "2.2G")]    // exact tie: printf rounds to even
    [InlineData(2_750_000_000, "2.8G")]    // exact tie: to even
    [InlineData(1_150_000_000, "1.1G")]    // 1.15 is 1.1499… in binary
    [InlineData(102_500_000_000, "102G")]  // exact .5 tie, to even
    [InlineData(103_500_000_000, "104G")]
    public void CompactBytesMatchesTheMac(long bytes, string expected) =>
        Assert.Equal(expected, ShellFormat.CompactBytes(bytes));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(44, "just now")]
    [InlineData(45, "a minute ago")]
    [InlineData(89, "a minute ago")]
    [InlineData(90, "2 minutes ago")]      // 1.5 rounds away from zero, as Swift's rounded()
    [InlineData(120, "2 minutes ago")]     // the harness's pinned value
    [InlineData(150, "3 minutes ago")]     // 2.5 → 3, not banker's 2
    [InlineData(3_599, "60 minutes ago")]
    [InlineData(3_600, "an hour ago")]
    [InlineData(5_400, "2 hours ago")]
    [InlineData(9_000, "3 hours ago")]     // 2.5 → 3
    [InlineData(86_400, "yesterday")]
    [InlineData(129_599, "yesterday")]
    [InlineData(129_600, "2 days ago")]
    [InlineData(-30, "just now")]          // a clock step backwards
    public void RelativeTimeMatchesTheMac(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, ShellFormat.RelativeTime(now.AddSeconds(-secondsAgo), now));
    }

    [Theory]
    [InlineData(1_280, 1.0, 200)]
    [InlineData(1_399, 1.0, 200)]
    [InlineData(1_400, 1.0, 220)]
    [InlineData(1_440, 1.0, 220)]          // the parity renders' width
    [InlineData(1_700, 1.0, 236)]
    [InlineData(1_440, 0.9, 220)]          // smaller text never narrows it
    [InlineData(1_440, 1.15, 253)]
    [InlineData(1_280, 1.3, 260)]
    public void SidebarWidthFollowsTheWindow(double window, double scale, double expected) =>
        Assert.Equal(expected, ShellFormat.SidebarWidth(window, scale));
}
