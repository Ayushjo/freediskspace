using DiskMap.Core;
using static DiskMap.App.Pages.DeveloperBoardPage;

namespace DiskMap.App.Tests;

/// <summary>The Developer page's bulk adds take only what rebuilds by itself; By tool's adds exactly what it lists.</summary>
public class DeveloperPageAuditTests
{
    private static DeveloperItem Item(DeveloperReclaimability reclaim, bool isProtected = false, bool trashUnsafe = false) =>
        new(7, "board-backups", @"C:\p\board-backups", 9_000, DeveloperCategory.Other, DeveloperEcosystem.Pcb,
            isProtected, reclaim, "", null, null, 0, false, RebuildCost.Never, null, null, null,
            trashUnsafe ? new CleanupRecipe { Id = "d", Title = "D", Command = "d", Why = "", TrashIsUnsafe = true } : null);

    [Fact]
    public void BulkAddsTakeOnlyWhatRebuildsByItself()
    {
        Assert.True(StagesInBulk(Item(DeveloperReclaimability.Reclaimable)));
        Assert.False(StagesInBulk(Item(DeveloperReclaimability.ReviewFirst)));   // backups, history, dependencies
        Assert.False(StagesInBulk(Item(DeveloperReclaimability.Keep)));
        Assert.False(StagesInBulk(Item(DeveloperReclaimability.Reclaimable, isProtected: true)));
        Assert.False(StagesInBulk(Item(DeveloperReclaimability.Reclaimable, trashUnsafe: true)));
        // A review-first item can still be added on its own, from its panel.
        Assert.True(CanStage(Item(DeveloperReclaimability.ReviewFirst)));
        Assert.Equal(RowAction.Review, ActionFor(Item(DeveloperReclaimability.ReviewFirst)));
    }

    [Fact]
    public void ByToolsAddNamesExactlyWhatItLists()
    {
        Assert.Equal("Add all to basket", AddListedTitle(12, 12));
        Assert.Equal("Add these 30 to basket", AddListedTitle(30, 45));
    }

    private static ReviewableTarget Target(string id, params int[] nodeIds) =>
        new(id, ReviewableCategory.Caches, id, "", 1, "", nodeIds.Select(n => $"n{n}").ToList(), nodeIds,
            new SafetyAssessment(SafetyLevel.Safe, "", id), "", "folder", null);

    [Fact]
    public void ByToolsAddsTempAsTheEntriesCleanUpStagesNeverTheFolder()
    {
        var tree = new FileTree();
        int root = tree.AddNode("you", -1, true, 0, 0, 0);
        int local = tree.AddNode("Local", root, true, 0, 0, 0);
        int temp = tree.AddNode("Temp", local, true, 0, 0, 0);
        int big = tree.AddNode("setup.tmp", temp, false, 900, 900, 0);
        int folder = tree.AddNode("7zS1234", temp, true, 0, 0, 0);
        tree.AddNode("payload.bin", folder, false, 50, 50, 0);
        int npm = tree.AddNode("npm-cache", local, true, 0, 0, 0);
        int cacache = tree.AddNode("_cacache", npm, true, 0, 0, 0);
        tree.AddNode("blob", cacache, false, 70, 70, 0);
        tree.AddNode("index", npm, false, 5, 5, 0);
        int empty = tree.AddNode("pip", local, true, 0, 0, 0);
        var totals = tree.RollUpSizes(SizeBasis.Logical);
        // What Clean up built: Temp as its entries, npm-cache as itself beside a cache group inside it.
        var reviewables = new List<ReviewableTarget>
        {
            Target("qw-temp", big, folder), Target("cache-npm", cacache), Target("qw-npm", npm),
        };

        Assert.Equal([big, folder], ToolTargets([temp], reviewables, tree, totals));
        Assert.Equal([npm], ToolTargets([npm], reviewables, tree, totals));
        Assert.Equal([big, folder, npm], ToolTargets([temp, empty, npm], reviewables, tree, totals));   // nothing for a folder of no bytes
        Assert.Equal([npm], ToolTargets([npm], [], tree, totals));   // nothing built yet: the folder as before
    }
}
