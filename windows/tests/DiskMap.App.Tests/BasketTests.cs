using DiskMap.App.Pages;

namespace DiskMap.App.Tests;

/// <summary>The basket's groups and words (Mac: CleanupQueueView.source / detail / freeSummary).</summary>
public class BasketTests
{
    [Theory]
    [InlineData("Clean up", "Clean up")]
    [InlineData("Biggest file: Wedding edit 4K.mov", "Biggest file")]
    [InlineData("old download: setup.exe", "Old download")]
    [InlineData("duplicate", "Duplicates")]
    [InlineData("caches", "Caches")]
    [InlineData("leftover of Slack", "App leftovers")]
    [InlineData("", "Other")]
    [InlineData("  : x", "Other")]
    public void ItemsGroupByWhereTheyCameFrom(string reason, string expected) =>
        Assert.Equal(expected, BasketWindow.Source(reason));

    [Theory]
    [InlineData("Biggest file: Wedding edit 4K.mov", "Wedding edit 4K.mov")]
    [InlineData("Clean up", null)]
    [InlineData("Explore: ", null)]
    public void DetailIsWhatFollowsTheColon(string reason, string? expected) =>
        Assert.Equal(expected, BasketWindow.Detail(reason));
}
