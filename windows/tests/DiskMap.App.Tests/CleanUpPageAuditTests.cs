using DiskMap.App.Pages;
using static DiskMap.App.Pages.CleanUpPage;

namespace DiskMap.App.Tests;

/// <summary>The audit's Clean up page findings: ticks hidden under "N more", and words that promised too much.</summary>
public class CleanUpPageAuditTests
{
    private static List<CleanItem> Rows(CleanGroup group, int count) =>
        Enumerable.Range(0, count)
            .Select(i => new CleanItem($"r-{i}", group, $"row {i}", "why", 1_000 - i, [(i, $@"C:\x\{i}")], $@"C:\x\{i}"))
            .ToList();

    [Fact]
    public void DefaultTicksPastThePreviewOpenTheGroup()
    {
        var rows = Rows(CleanGroup.Rebuilds, 11);
        var ticks = DefaultTicks(rows, _ => false, tickRebuilds: true);
        Assert.Equal(11, ticks.Count);
        Assert.Equal(11, RowsShown(rows, Preview(CleanGroup.Rebuilds), ticks, expanded: false, showAll: false));
        // Nothing ticked: folded to the preview again.
        Assert.Equal(8, RowsShown(rows, Preview(CleanGroup.Rebuilds), new HashSet<string>(), false, false));
    }

    [Fact]
    public void OneTickedRowPastThePreviewIsEnoughToOpenIt()
    {
        var rows = Rows(CleanGroup.ReviewFirst, 6);
        int preview = Preview(CleanGroup.ReviewFirst);
        Assert.Equal(4, RowsShown(rows, preview, new HashSet<string> { "r-0", "r-3" }, false, false));
        Assert.Equal(6, RowsShown(rows, preview, new HashSet<string> { "r-5" }, false, false));
        // The group's own tick box ticks every row: all of them show.
        Assert.Equal(6, RowsShown(rows, preview, rows.Select(r => r.Id).ToHashSet(), false, false));
    }

    [Fact]
    public void AnOpenGroupDrawsAtMostTheLimitUntilShowAll()
    {
        var rows = Rows(CleanGroup.Downloads, 250);
        int preview = Preview(CleanGroup.Downloads);
        Assert.Equal(ExpandedLimit, RowsShown(rows, preview, new HashSet<string>(), expanded: true, showAll: false));
        Assert.Equal(250, RowsShown(rows, preview, new HashSet<string>(), expanded: true, showAll: true));
        // Every row ticked: the limit still holds, and the link says how many ticks are below it.
        var all = rows.Select(r => r.Id).ToHashSet();
        int shown = RowsShown(rows, preview, all, false, false);
        Assert.Equal(ExpandedLimit, shown);
        Assert.StartsWith("Show all 250 · 150 more ticked", MoreLink(250, shown, preview, 250 - shown, 1));
        Assert.StartsWith("246 more · ", MoreLink(250, preview, preview, 0, 1));
    }

    [Fact]
    public void TheBigButtonFillsTheBasket()
    {
        Assert.Equal("Tick something to free", MoveTitle(0));
        Assert.Equal("Add 1 item to the basket", MoveTitle(1));
        Assert.Equal("Add 3 items to the basket", MoveTitle(3));
        Assert.DoesNotContain("Recycle Bin", MoveTitle(3));
    }

    [Fact]
    public void TheReceiptSaysTheSpaceComesBackOnceTheBinIsEmptied()
    {
        var terms = ReceiptView.Terms(freeBefore: 100_000_000_000, total: 5_000_000_000);
        Assert.Equal(["Where it goes", "Free once emptied", "Deleted forever", "Undo"], terms.Select(t => t.Label));
        Assert.Equal("Only if you confirm", terms.Single(t => t.Label == "Deleted forever").Value);
        Assert.Equal("Put Back, until it's emptied", terms.Single(t => t.Label == "Undo").Value);
        Assert.DoesNotContain(terms, t => t.Value.Contains("any time") || t.Value == "Nothing");
        // Without the volume's free space there is no before/after line.
        Assert.DoesNotContain(ReceiptView.Terms(null, 5), t => t.Label == "Free once emptied");
        Assert.Contains("empty the Recycle Bin", ReceiptView.Footnote);
    }

    [Fact]
    public void OldDownloadsAreDatedByTheirLastChange() =>
        Assert.Equal("Not changed in a long time", GroupSubtitle(CleanGroup.Downloads));
}
