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
}
