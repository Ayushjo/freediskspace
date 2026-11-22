using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>
/// The Duplicates and Applications pages after the audit: one toast per Add that says what
/// stayed out, risky app data never added in bulk, and staging buttons that say "Add … to basket".
/// </summary>
public class DuplicatesAppsPagesAuditTests
{
    [Fact]
    public void OneToastSaysWhatWentInAndWhatStayedOut()
    {
        // As before the audit.
        Assert.Null(DuplicatesBoardPage.StagedToast(3, 0, 0, [], 0));   // the usual "Added 3 items" toast
        Assert.Equal("Keep at least one copy", DuplicatesBoardPage.StagedToast(0, 0, 2, [], 0));
        Assert.Equal("Already in the basket", DuplicatesBoardPage.StagedToast(0, 4, 0, [], 0));
        Assert.Equal("Added 2 items to the basket · left 1 group alone to keep one copy",
            DuplicatesBoardPage.StagedToast(2, 0, 1, [], 0));
        Assert.Equal("Added 1 item to the basket · skipped 3 risky items (a, b +1) — add those one at a time to see the warning",
            DuplicatesBoardPage.StagedToast(1, 0, 0, ["a", "b", "c"], 0));

        // Copies that changed since the search are named, with or without anything added.
        Assert.Equal("Added 2 items to the basket · left out 1 copy: files changed since the search, search again",
            DuplicatesBoardPage.StagedToast(2, 0, 0, [], 1));
        Assert.Equal("Left out 3 copies: files changed since the search, search again",
            DuplicatesBoardPage.StagedToast(0, 0, 0, [], 3));
        Assert.Equal("Skipped 1 risky item (Docker) — add those one at a time to see the warning",
            DuplicatesBoardPage.StagedToast(0, 0, 0, ["Docker"], 0));
    }

    [Fact]
    public void RiskyAppDataIsNeverAddedInBulk()
    {
        var docker = new ApplicationRelatedItem(ApplicationRelatedKind.LocalData, @"C:\Users\me\AppData\Local\Docker", 60_000_000_000);
        var desktop = new ApplicationRelatedItem(ApplicationRelatedKind.ProgramData, @"C:\ProgramData\DockerDesktop", 1_000_000);
        var plain = new ApplicationRelatedItem(ApplicationRelatedKind.Other, @"D:\Elsewhere\Spotify", 900_000_000);
        var (safe, risky) = AppsBoardPage.SplitRisky([docker, plain, desktop], _ => true);
        Assert.Equal(new[] { plain }, safe);
        Assert.Equal(new[] { docker, desktop }, risky);
    }

    [Fact]
    public void ApplicationsCallItAppDataAndAddsItToTheBasket()
    {
        var app = new ApplicationEntry { Id = "reg:Steam", Name = "Steam" };
        Assert.Equal("Add app data to basket", AppsBoardPage.PrimaryTitle(staged: false, tickedCount: 2));
        Assert.Equal("Tick the app data to add", AppsBoardPage.PrimaryTitle(staged: false, tickedCount: 0));
        foreach (string words in new[] { AppsBoardPage.PlacesLabel(2), AppsBoardPage.PrimaryTitle(false, 1), AppsBoardPage.Guidance(app) })
        {
            Assert.DoesNotContain("leftover", words, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Clean up", words, StringComparison.Ordinal);
            Assert.DoesNotContain("Recycle Bin", words, StringComparison.Ordinal);   // only the basket's commit says that
        }
    }
}
