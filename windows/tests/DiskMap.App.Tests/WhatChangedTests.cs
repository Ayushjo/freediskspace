using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>What changed's words and chart maths (Mac: SnapshotsView.signed / sinceText / split / beforeAfter, UsageChart).</summary>
public class WhatChangedTests
{
    private static SnapshotComparison.Entry E(string name, long before, long after) =>
        new(name, name, 1, 1, before, after, true);

    [Fact]
    public void SignedUsesHeroFiguresAndAProperMinus()
    {
        Assert.Equal("+18.6 GB", WhatChangedPage.Signed(18_600_000_000));
        Assert.Equal("−4.30 GB", WhatChangedPage.Signed(-4_300_000_000));
        Assert.Equal("±0", WhatChangedPage.Signed(0));
    }

    [Fact]
    public void SinceTextCountsCalendarDays()
    {
        var now = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 23)));
        Assert.Equal("since earlier today", WhatChangedPage.SinceText(now.AddHours(-2), now));
        Assert.Equal("since yesterday", WhatChangedPage.SinceText(now.AddDays(-1), now));
        Assert.Equal("since Monday", WhatChangedPage.SinceText(new DateTimeOffset(2026, 10, 19, 12, 0, 0, now.Offset), now));
        Assert.Equal("since last week", WhatChangedPage.SinceText(now.AddDays(-9), now));
        Assert.Equal("since 2 Oct", WhatChangedPage.SinceText(new DateTimeOffset(2026, 10, 2, 12, 0, 0, now.Offset), now));
        Assert.Equal("10 Oct", WhatChangedPage.ShortDate(new DateTimeOffset(2026, 10, 10, 12, 0, 0, now.Offset)));
    }

    [Fact]
    public void SplitOrdersBiggestChangeFirst()
    {
        var (grew, shrank) = WhatChangedPage.Split([E("a", 0, 5), E("b", 0, 10), E("c", 3, 0), E("d", 8, 0), E("e", 4, 4)]);
        Assert.Equal(["b", "a"], grew.Select(e => e.Name).ToArray());
        Assert.Equal(["d", "c"], shrank.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void BeforeAfterSharesTheLargerUnit()
    {
        Assert.Equal("22.8 → 31.2 GB", WhatChangedPage.BeforeAfter(22_800_000_000, 31_200_000_000));
        Assert.Equal("0.5 → 2.0 MB", WhatChangedPage.BeforeAfter(500_000, 2_000_000));
        Assert.Equal("0.1 → 0.9 KB", WhatChangedPage.BeforeAfter(100, 900));
    }

    [Fact]
    public void PlaceIsHomeRelativeOrFromTheScannedFolder()
    {
        const string home = @"C:\Users\me";
        Assert.Equal(@"~\AppData\Local\Docker", WhatChangedPage.Place(@"C:\Users\me\AppData\Local\Docker", @"C:\", home));
        Assert.Equal(@"scan\Movies\a.mov", WhatChangedPage.Place(@"D:\scan\Movies\a.mov", @"D:\scan", home));
        Assert.Equal(@"E:\other\x", WhatChangedPage.Place(@"E:\other\x", @"D:\scan", home));
    }

    [Fact]
    public void AxisRoundsToAFewGridLines()
    {
        // The Mac render: one 184 GB bar on 175 … 190 in steps of 5.
        Assert.Equal((175.0, 190.0, 5.0), UsageChart.Axis([184.0]));
        var (low, high, step) = UsageChart.Axis([120.0, 184.0]);
        Assert.True(low <= 120 && high >= 184 && step > 0);
        Assert.Equal((0.0, 1.0, 1.0), UsageChart.Axis([]));
    }

    [Fact]
    public void ChartUnitAndFigures()
    {
        Assert.Equal("GB", UsageChart.Unit([new UsageChart.Point("a", "TODAY", 1_000_000_000, UsageChart.Role.After, null)]).Name);
        Assert.Equal("MB", UsageChart.Unit([new UsageChart.Point("a", "TODAY", 999_999_999, UsageChart.Role.After, null)]).Name);
        Assert.Equal("184", UsageChart.Figure(184.4));
        Assert.Equal("12.4", UsageChart.Figure(12.44));
    }
}
