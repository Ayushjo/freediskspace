using DiskMap.App.Pages;
using DiskMap.Core;
using static DiskMap.App.Pages.DeveloperBoardPage;

namespace DiskMap.App.Tests;

/// <summary>Developer's rows, tabs and tool-storage links (Mac: DeveloperStorageView.itemRow / XcodeStorageList).</summary>
public class DeveloperBoardTests
{
    private static DeveloperItem Item(DeveloperReclaimability reclaim, bool isProtected = false, CleanupRecipe? recipe = null) =>
        new(7, "node_modules", @"C:\p\node_modules", 9_000, DeveloperCategory.Dependencies, DeveloperEcosystem.Node,
            isProtected, reclaim, "", null, null, 0, false, RebuildCost.Networked, null, null, null, recipe);

    private static readonly CleanupRecipe Docker = new()
    {
        Id = "docker", Title = "Docker", Command = "docker system prune -a", Why = "", TrashIsUnsafe = true,
    };

    [Fact]
    public void TabsReadLikeTheMacWithWindowsTools()
    {
        Assert.Equal(["Where it lives", "Projects", "By tool", "WSL & SDKs"], PrimaryTabs.Select(TabTitle));
        Assert.Equal("Every item", TabTitle(Tab.AllItems));
        Assert.Equal("Biggest wins", TabTitle(Tab.Opportunities));
        Assert.Equal(5, TileLimit(Rd.WidthClass.Compact));
        Assert.Equal(9, TileLimit(Rd.WidthClass.Wide));
        Assert.Equal(320, InspectorWidth(Rd.WidthClass.Regular));
    }

    [Fact]
    public void OnlyWhatRebuildsGetsCleanAndProtectedGetsNothing()
    {
        Assert.Equal(RowAction.Clean, ActionFor(Item(DeveloperReclaimability.Reclaimable)));
        Assert.Equal(RowAction.Review, ActionFor(Item(DeveloperReclaimability.ReviewFirst)));
        Assert.Equal(RowAction.Review, ActionFor(Item(DeveloperReclaimability.Reclaimable, recipe: Docker)));
        Assert.Equal(RowAction.None, ActionFor(Item(DeveloperReclaimability.Keep, isProtected: true)));
        Assert.False(CanStage(Item(DeveloperReclaimability.Reclaimable, recipe: Docker)));
        Assert.False(CanStage(Item(DeveloperReclaimability.Reclaimable, isProtected: true)));
        Assert.True(CanStage(Item(DeveloperReclaimability.ReviewFirst)));
    }

    [Fact]
    public void TagsAndSublinesSayWhatHappens()
    {
        Assert.Equal(("Dependencies", (long?)9_000, true), ItemTag(Item(DeveloperReclaimability.Reclaimable)));
        Assert.Equal(("Review", (long?)null, false), ItemTag(Item(DeveloperReclaimability.ReviewFirst)));
        Assert.Equal(("Keep", (long?)null, false), ItemTag(Item(DeveloperReclaimability.Keep)));
        Assert.Equal("rebuilds", ItemSubline(Item(DeveloperReclaimability.Reclaimable)));
        Assert.Equal("not ticked", ItemSubline(Item(DeveloperReclaimability.ReviewFirst)));
        Assert.Equal("keep", ItemSubline(Item(DeveloperReclaimability.Keep)));
    }

    [Fact]
    public void ToolStorageStagesOnlyTrashAndNeverTheKeptVersion()
    {
        var entry = new SystemStorageCatalog.Entry("x", "Versions", ["p"], SystemStorageCatalog.Action.Trash, "why", [], true,
            PerChild: true, KeepNewest: true);
        var item = new SystemStorageCatalog.Item(entry,
            [new(@"C:\v\1.0", 1, 3_000_000_000), new(@"C:\v\2.0", 2, 4_000_000_000), new(@"C:\v\0.9", 3, 1_000_000_000)],
            false, new HashSet<string> { @"C:\v\2.0" });
        var targets = ToolStageTargets(item);
        Assert.Equal([1, 3], targets.Select(t => t.NodeId));
        Assert.Equal("Add 2 older versions to the basket · 4 GB", ToolStageTitle(item, targets.Count));

        var guide = item with { Entry = entry with { Action = SystemStorageCatalog.Action.Guide } };
        Assert.Empty(ToolStageTargets(guide));
        var single = item with { Entry = entry with { PerChild = false } };
        Assert.Equal("Add to basket · 4 GB", ToolStageTitle(single, 2));
    }
}
