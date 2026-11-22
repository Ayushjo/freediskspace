using System.IO;
using DiskMap.App.Controls;
using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>
/// The audit fixes in Explore, the charts, the inspector and Overview that can be checked
/// without a window: what the selection toolbar acts on, the table cap, what screen readers
/// hear, the unreadable-folders notice, when Overview rebuilds, and the basket wording.
/// </summary>
public class ExploreUiTests
{
    [Fact]
    public void SelectionToolbarActsOnlyOnWhatThePageLists()
    {
        // Selected on another page (7, 9) or filtered out since: never added under this page's name.
        var selection = new HashSet<int> { 3, 7, 9, 12 };
        Assert.Equal(new HashSet<int> { 3, 12 }, ListKit.VisibleSelection(selection, [1, 3, 5, 12]));
        Assert.Empty(ListKit.VisibleSelection(selection, [1, 2]));
        Assert.Empty(ListKit.VisibleSelection(new HashSet<int>(), [1, 2]));
    }

    [Fact]
    public void ContentsTableShowsTwoHundredAndSaysHowManyMore()
    {
        var rows = Enumerable.Range(0, 20_000).Select(i => (i, 20_000L - i)).ToList();
        var (shown, hidden) = ExplorePage.Capped(rows);
        Assert.Equal(ExplorePage.ContentsLimit, shown.Count);
        Assert.Equal(200, shown.Count);
        Assert.Equal(19_800, hidden);
        Assert.Equal(rows.Take(200), shown);   // the biggest, in order
        Assert.Equal("+19,800 more — see them all in List", ExplorePage.MoreLine(hidden));

        var few = rows.Take(12).ToList();
        Assert.Equal((12, 0), (ExplorePage.Capped(few).Shown.Count, ExplorePage.Capped(few).Hidden));
    }

    [Fact]
    public void ChartCellsTellScreenReadersHowToOpenAFolder()
    {
        string folder = ChartPeer.ItemName("Photos", 25_000_000_000, 100_000_000_000, isDirectory: true);
        Assert.StartsWith("Photos, ", folder);
        Assert.Contains("25%", folder);
        Assert.EndsWith(", folder — press Enter to open", folder);
        Assert.DoesNotContain("double-click", folder);
        string file = ChartPeer.ItemName("clip.mp4", 1, 0, isDirectory: false);   // an empty parent never divides by zero
        Assert.DoesNotContain("folder", file);
        Assert.StartsWith("clip.mp4, ", file);
    }

    [Fact]
    public void OnlyArrowKeysAnnounceAMove()
    {
        Assert.True(ChartNavigation.IsMove(System.Windows.Input.Key.Left));
        Assert.True(ChartNavigation.IsMove(System.Windows.Input.Key.Down));
        Assert.False(ChartNavigation.IsMove(System.Windows.Input.Key.Enter));
        Assert.False(ChartNavigation.IsMove(System.Windows.Input.Key.Escape));
    }

    [Fact]
    public void UnreadableNoticeDoesNotTellAnAdministratorToElevate()
    {
        var (title, asUser) = OverviewPage.UnreadableWords(3, elevated: false);
        Assert.Equal("3 folders couldn’t be read.", title);
        Assert.Contains("as administrator", asUser);

        var (one, asAdmin) = OverviewPage.UnreadableWords(1, elevated: true);
        Assert.Equal("1 folder couldn’t be read.", one);
        Assert.DoesNotContain("as administrator", asAdmin);
        Assert.Contains("even to an administrator", asAdmin);
    }

    [Fact]
    public void UnreadableListShowsEveryPathUpToTheCap()
    {
        Assert.Equal(@"C:\a" + Environment.NewLine + @"C:\b", OverviewPage.UnreadableList([@"C:\a", @"C:\b"]));
        var many = Enumerable.Range(0, 1_234).Select(i => $@"C:\Users\other{i}").ToList();
        var lines = OverviewPage.UnreadableList(many).Split(Environment.NewLine);
        Assert.Equal(1_001, lines.Length);
        Assert.Equal(@"C:\Users\other999", lines[999]);
        Assert.Equal("… and 234 more", lines[^1]);
    }

    [Theory]
    [InlineData(Rd.WidthClass.Compact, 1_200, false)]
    [InlineData(Rd.WidthClass.Compact, 1_399, false)]   // a pixel of dragging is no rebuild
    [InlineData(Rd.WidthClass.Compact, 1_400, true)]
    [InlineData(Rd.WidthClass.Regular, 1_699, false)]
    [InlineData(Rd.WidthClass.Wide, 1_650, true)]
    public void OverviewRebuildsOnlyForANewWidthClass(Rd.WidthClass built, double width, bool rebuild) =>
        Assert.Equal(rebuild, OverviewPage.WidthClassChanged(built, width));

    [Fact]
    public void InspectorBreakdownIsTypeBreakdownBiggestFirstWithoutEmptyKinds()
    {
        var tree = new FileTree();
        int root = tree.AddNode("root", -1, true, 0, 0, 0);
        int folder = tree.AddNode("Mixed", root, true, 0, 0, 0);
        tree.AddNode("a.mp4", folder, false, 300, 300, 0);
        tree.AddNode("b.pdf", folder, false, 500, 500, 0);
        tree.AddNode("c.zip", folder, false, 0, 0, 0);
        var totals = tree.RollUpSizes(SizeBasis.Logical);
        var inside = InspectorPanel.WhatsInside(tree, totals, root);
        Assert.Equal(["document", "video"], inside.Select(kv => kv.Key));
        Assert.Equal([500L, 300L], inside.Select(kv => kv.Value));
    }

    private static string AppRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app", "DiskMap.App"));

    private static IEnumerable<string> AppSources() =>
        Directory.EnumerateFiles(AppRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void ThingsGoInTheBasketAndOnlyTheBasketMovesThemToTheRecycleBin()
    {
        var cleanupWording = AppSources().Where(f => File.ReadAllText(f).Contains("Add to Cleanup", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Empty(cleanupWording);
        var recycle = AppSources().Where(f => File.ReadAllText(f).Contains("\"Move to Recycle Bin", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.All(recycle, name => Assert.Equal("BasketWindow.cs", name));
    }
}
