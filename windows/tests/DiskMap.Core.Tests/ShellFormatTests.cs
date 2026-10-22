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

    /// <summary>Every value printed by macOS ByteCountFormatter (.file) on the Mac.</summary>
    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(1L, "1 byte")]
    [InlineData(2L, "2 bytes")]
    [InlineData(999L, "999 bytes")]
    [InlineData(1000L, "1 KB")]
    [InlineData(1001L, "1 KB")]
    [InlineData(1499L, "1 KB")]
    [InlineData(1500L, "2 KB")]
    [InlineData(1501L, "2 KB")]
    [InlineData(2500L, "3 KB")]
    [InlineData(999499L, "999 KB")]
    [InlineData(999500L, "1 MB")]
    [InlineData(999999L, "1 MB")]
    [InlineData(1000000L, "1 MB")]
    [InlineData(1049999L, "1 MB")]
    [InlineData(1050000L, "1.1 MB")]
    [InlineData(1150000L, "1.2 MB")]
    [InlineData(8360000L, "8.4 MB")]
    [InlineData(8350000L, "8.4 MB")]
    [InlineData(8250000L, "8.3 MB")]
    [InlineData(99950000L, "100 MB")]
    [InlineData(999949999L, "999.9 MB")]
    [InlineData(999950000L, "1 GB")]
    [InlineData(1000000000L, "1 GB")]
    [InlineData(1005000000L, "1.01 GB")]
    [InlineData(1015000000L, "1.02 GB")]
    [InlineData(1125000000L, "1.13 GB")]
    [InlineData(17725000000L, "17.73 GB")]
    [InlineData(26100000000L, "26.1 GB")]
    [InlineData(183860000000L, "183.86 GB")]
    [InlineData(500000000000L, "500 GB")]
    [InlineData(999995000000L, "1 TB")]
    [InlineData(1000000000000L, "1 TB")]
    [InlineData(1234567890123L, "1.23 TB")]
    [InlineData(393200L, "393 KB")]
    [InlineData(640000000L, "640 MB")]
    [InlineData(2400000L, "2.4 MB")]
    [InlineData(120000000000L, "120 GB")]
    [InlineData(28540000000L, "28.54 GB")]
    [InlineData(12000000000L, "12 GB")]
    [InlineData(196140000000L, "196.14 GB")]
    [InlineData(5605000000L, "5.61 GB")]
    [InlineData(5615000000L, "5.62 GB")]
    public void BytesMatchTheMacFormatter(long bytes, string expected) =>
        Assert.Equal(expected, ShellFormat.Bytes(bytes));

    [Fact]
    public void NegativeBytesKeepTheirSign() => Assert.Equal("-1.5 MB", ShellFormat.Bytes(-1_500_000));

    [Fact]
    public void HumanUnitsUsesTheSameFormat() => Assert.Equal("28.54 GB", HumanUnits.Format(28_540_000_000));

    /// <summary>The Mac's ByteFormat.hero, values printed by Swift (digits picked before rounding).</summary>
    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(999L, "999 bytes")]
    [InlineData(1000L, "1.00 KB")]
    [InlineData(120000000000L, "120 GB")]
    [InlineData(500000000000L, "500 GB")]
    [InlineData(28540000000L, "28.5 GB")]
    [InlineData(8405000000L, "8.40 GB")]
    [InlineData(8415000000L, "8.41 GB")]
    [InlineData(1000000000L, "1.00 GB")]
    [InlineData(99950000000L, "100.0 GB")]
    [InlineData(999600000000L, "1000 GB")]
    [InlineData(12345L, "12.3 KB")]
    [InlineData(45550000000L, "45.5 GB")]
    [InlineData(1234567890123L, "1.23 TB")]
    public void HeroBytesMatchTheMac(long bytes, string expected) =>
        Assert.Equal(expected, ShellFormat.HeroBytes(bytes));
}
