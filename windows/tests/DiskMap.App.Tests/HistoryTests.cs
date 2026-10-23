using DiskMap.App.Pages;
using DiskMap.Core;
using S = DiskMap.Core.CleanupHistory.ItemState;

namespace DiskMap.App.Tests;

/// <summary>History's tags and dates (Mac: HistoryView.tag / whenText / dayText).</summary>
public class HistoryTests
{
    [Fact]
    public void TagsSayWhereTheItemsAre()
    {
        Assert.Equal(("All in Recycle Bin", true), HistoryPage.CardTag([S.InTrash, S.InTrash]));
        Assert.Equal(("1 in Recycle Bin", true), HistoryPage.CardTag([S.InTrash, S.PutBack]));
        Assert.Equal(("Put back", false), HistoryPage.CardTag([S.PutBack, S.PutBack]));
        Assert.Equal(("Bin emptied", false), HistoryPage.CardTag([S.PutBack, S.TrashEmptied]));
        Assert.Equal(("Bin emptied", false), HistoryPage.CardTag([]));
    }

    [Fact]
    public void WhenTextCountsCalendarDays()
    {
        var now = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 23)));
        Assert.Equal("Today, 08:05", HistoryPage.WhenText(now.AddMinutes(-55), now));
        Assert.Equal("Yesterday, 23:30", HistoryPage.WhenText(now.AddHours(-9.5), now));
        Assert.StartsWith("Monday, ", HistoryPage.WhenText(new DateTimeOffset(2026, 10, 19, 12, 0, 0, now.Offset), now));
        Assert.Equal("2 Oct", HistoryPage.WhenText(new DateTimeOffset(2026, 10, 2, 12, 0, 0, now.Offset), now));
        Assert.Equal("2 Oct 2025", HistoryPage.DayText(new DateTimeOffset(2025, 10, 2, 12, 0, 0, now.Offset), now));
    }
}
