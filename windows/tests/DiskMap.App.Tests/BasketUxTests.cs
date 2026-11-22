using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>
/// The basket's, the confirm sheets' and History's words and decisions after the audit: no
/// promise of undo on a permanent delete, outcomes reported as they happened, refusals that
/// don't always blame size, names that can't hide what they are.
/// </summary>
public class BasketUxTests
{
    private static CleanupQueue.StagedItem Item(string path, long size = 1_000) =>
        new() { Path = path, Size = size, Reason = "Clean up" };

    private static CleanupQueue.CommitReport Report(long freed, params (string Path, bool Ok)[] entries) =>
        new(entries.Select(e => new CleanupQueue.CommitEntry(Item(e.Path), e.Ok ? null : new System.IO.IOException("in use"), false, 0)).ToList(),
            freed, false);

    // ---- 1. The permanent-delete prompt promises no undo ----

    [Fact]
    public void ConfirmHintKeepsTodaysTextUnlessGivenOne()
    {
        Assert.Equal("Put Back returns everything while it's in the Recycle Bin.", Dialogs.ConfirmHint(danger: true));
        Assert.Equal("Nothing moves yet.", Dialogs.ConfirmHint(danger: false));
        Assert.Equal("Gone for good.", Dialogs.ConfirmHint(danger: true, hint: "Gone for good."));
    }

    [Fact]
    public void PermanentDeletePromptSaysItCantBeUndone()
    {
        var prompt = BasketWindow.PermanentDeletePrompt(2, 0);
        Assert.Equal("This can't be undone — Put Back doesn't cover these.", prompt.Hint);
        Assert.DoesNotContain("Put Back returns", prompt.Hint + prompt.Message);
        Assert.Equal("Delete 2 permanently", prompt.ConfirmLabel);
        Assert.Null(prompt.Footnote);
    }

    // ---- 2. Outcomes as they happened ----

    [Fact]
    public void MovedOnlyReadsAsMovedWithPutBack()
    {
        var outcome = BasketWindow.DescribeCommit(Report(4_096, (@"C:\a\x.iso", true), (@"C:\a\y.iso", true)), null, left: 0);
        Assert.Equal("2 items moved to the Recycle Bin", outcome.Headline);
        Assert.Equal((2, 0), (outcome.Moved, outcome.Deleted));
        Assert.Contains("Put Back returns them", outcome.Note);
        Assert.Contains("is freed when you empty the Recycle Bin", outcome.FreedLine);
        Assert.Equal("Moved 2 items to the Recycle Bin", outcome.Toast);
    }

    [Fact]
    public void PermanentDeletesAloneAreNeverCalledMovedAndAreNamed()
    {
        var refused = Report(0, (@"C:\code\app\node_modules", false));
        var deleted = Report(9_000_000, (@"C:\code\app\node_modules", true));
        var outcome = BasketWindow.DescribeCommit(refused, deleted, left: 0);
        Assert.Equal("1 item deleted permanently", outcome.Headline);
        Assert.Equal(0, outcome.Moved);   // the success screen hides Put Back
        Assert.DoesNotContain("moved", outcome.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Recycle Bin", outcome.FreedLine);
        Assert.Contains("freed now", outcome.FreedLine);
        Assert.Contains(@"Deleted permanently C:\code\app\node_modules", outcome.Details);
        Assert.Contains("can't return it", outcome.Note);
        Assert.Equal("Deleted 1 item permanently", outcome.Toast);
    }

    [Fact]
    public void BothSayBothAndListEveryPath()
    {
        var report = Report(4_096, (@"C:\a\x.iso", true), (@"C:\code\b\node_modules", false));
        var deleted = Report(1_000, (@"C:\code\b\node_modules", true));
        var outcome = BasketWindow.DescribeCommit(report, deleted, left: 3);
        Assert.Equal("1 item moved to the Recycle Bin, 1 deleted permanently", outcome.Headline);
        Assert.Equal(new[] { @"Moved C:\a\x.iso", @"Deleted permanently C:\code\b\node_modules", "3 items stayed in the basket — see the list for why." },
            outcome.Details);
        Assert.Contains("not what was deleted permanently", outcome.Note);
        // "Moved 1 items" was the old toast.
        Assert.Equal("Moved 1 item to the Recycle Bin · deleted 1 permanently · 3 still in the basket", outcome.Toast);
    }

    [Fact]
    public void AFailedPermanentDeleteNeverClaimsNothingWasDeleted()
    {
        Assert.DoesNotContain("nothing was deleted", BasketWindow.DeleteFailedReason("Access denied"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Access denied", BasketWindow.DeleteFailedReason("Access denied"));
        Assert.DoesNotContain("nothing was deleted", BasketWindow.NothingToast(refused: 2, deleteFailed: 1), StringComparison.OrdinalIgnoreCase);
    }

    // ---- 3. A refusal isn't always size ----

    [Fact]
    public void RefusalsNameBothReasons()
    {
        foreach (bool regenerable in new[] { true, false })
        {
            string why = BasketWindow.RefusedReason(regenerable);
            Assert.StartsWith("Couldn’t move: the Recycle Bin wouldn't take it (too big, or this drive has no Recycle Bin) — nothing was deleted", why);
        }
        Assert.Contains("delete it yourself", BasketWindow.RefusedReason(false));
        Assert.Contains("too big, or this drive has no Recycle Bin", BasketWindow.NothingToast(refused: 1, deleteFailed: 0));
        var prompt = BasketWindow.PermanentDeletePrompt(3, 1);
        Assert.Equal("The Recycle Bin can't take these 3 items", prompt.Title);
        Assert.StartsWith("The Recycle Bin can't take these — they're too big for it, or this drive has no Recycle Bin", prompt.Message);
        Assert.StartsWith("1 other item the Recycle Bin wouldn't take isn't regenerable, so it stays in the basket.", prompt.Footnote);
        Assert.Equal("The Recycle Bin can't take this item", BasketWindow.PermanentDeletePrompt(1, 0).Title);
    }

    // ---- 5. Quitting mid-move ----

    [Fact]
    public void StillWorkingSaysWhichWork()
    {
        Assert.StartsWith("Still moving to the Recycle Bin", BasketWindow.StillWorkingToast("Moving to the Recycle Bin…"));
        Assert.StartsWith("Still moving to the Recycle Bin", BasketWindow.StillWorkingToast(null));
        Assert.StartsWith("Still deleting permanently", BasketWindow.StillWorkingToast("Deleting permanently…"));
    }

    // ---- 6. Names that can't hide what they are ----

    [Fact]
    public void BidiOverridesAndControlsShowAsEscapes()
    {
        string disguised = "photo" + (char)0x202E + "gpj.exe";   // displays as "photoexe.jpg"
        Assert.Equal("photo[U+202E]gpj.exe", Dialogs.SafeDisplay(disguised));
        Assert.Equal("a[U+200E]b[U+2066]c[U+0009]d[U+000A]e[U+200B]f[U+FEFF]", Dialogs.SafeDisplay(
            "a" + (char)0x200E + "b" + (char)0x2066 + "c\td\ne" + (char)0x200B + "f" + (char)0xFEFF));
        const string plain = @"C:\Users\me\Pictures\café 2024.jpg";
        Assert.Same(plain, Dialogs.SafeDisplay(plain));
        // Zero-width joiners build emoji and scripts: they stay.
        string family = "👨" + (char)0x200D + "👩" + (char)0x200D + "👧.png";
        Assert.Equal(family, Dialogs.SafeDisplay(family));
    }

    // ---- 7. Warnings the commit sheet gives ----

    [Fact]
    public void CommitWarningsNameDuplicatesLeftWithNoCopyAndCloudItems()
    {
        Assert.Null(BasketWindow.CommitWarnings([], 0));
        string one = BasketWindow.CommitWarnings([[@"C:\a\IMG_1.jpg", @"D:\b\IMG_1.jpg"]], 0)!;
        Assert.Equal(@"Every copy of “IMG_1.jpg” is in the basket, so none would be left: C:\a\IMG_1.jpg, D:\b\IMG_1.jpg.", one);
        var many = Enumerable.Range(1, 7).Select(n => (IReadOnlyList<string>)[$@"C:\a\f{n}.mov", $@"C:\b\f{n}.mov"]).ToList();
        string several = BasketWindow.CommitWarnings(many, 0)!;
        Assert.StartsWith("Every copy of 7 duplicate files is in the basket, so none would be left: “f1.mov” (2 copies), ", several);
        Assert.EndsWith("“f5.mov” (2 copies) and 2 more.", several);
        Assert.Equal("3 items are in a cloud-synced folder: moving them also removes them from the cloud and your other devices.",
            BasketWindow.CommitWarnings([], 3));
        Assert.Equal("1 item is in a cloud-synced folder: moving it also removes it from the cloud and your other devices.",
            BasketWindow.CommitWarnings([], 1));
        Assert.Contains("\n\n", BasketWindow.CommitWarnings([[@"C:\a\x", @"C:\b\x"]], 1));
    }

    // ---- 9. The window fits a 1366 × 768 screen at 125 % ----

    [Fact]
    public void WindowFitsTheWorkArea()
    {
        Assert.Equal((1000d, 760d), BasketWindow.Fit(1000, 760, 1920));
        // 1366 × 768 at 125 %, a 48 px taskbar: 1092.8 × 576 DIPs.
        var (height, min) = BasketWindow.Fit(680, 480, 576);
        Assert.InRange(height, 480, 576 - 1);
        Assert.Equal(480, min);
        // Smaller than the minimum: the minimum gives way, never off the screen.
        Assert.Equal((368d, 368d), BasketWindow.Fit(680, 480, 400));
    }

    // ---- 10. Always "basket", never "queue" ----

    [Fact]
    public void WordsSayBasketNotQueue()
    {
        var folder = Item(@"C:\a\cache");
        var inside = Item(@"C:\a\cache\x.bin");
        var estimate = new CleanupQueue.ReclaimEstimate
        {
            Bytes = 1_000, HeldByUnqueuedCopies = 2_000_000,
            PerItem = new Dictionary<Guid, long> { [folder.Id] = 1_000, [inside.Id] = 0 },
        };
        string message = BasketWindow.ConfirmMessage([folder, inside], estimate);
        Assert.Contains("aren’t in the basket", message);
        Assert.DoesNotContain("queue", message, StringComparison.OrdinalIgnoreCase);
        var covered = CleanupQueue.CoveredItemIds([folder, inside]);
        Assert.Equal("Inside a folder in the basket — counted there", BasketWindow.Caption(inside, covered, estimate));
        var shared = Item(@"C:\b\link.bin", 5_000);
        var none = new CleanupQueue.ReclaimEstimate { PerItem = new Dictionary<Guid, long> { [shared.Id] = 0 } };
        Assert.EndsWith("freed only when every copy or link is in the basket", BasketWindow.Caption(shared, [], none));
    }

    // ---- 11. History ----

    [Fact]
    public void HistoryCallsSpaceFreedOnlyOnceTheBinIsEmptied()
    {
        var history = new CleanupHistory
        {
            Entries =
            [
                new CleanupHistory.Entry
                {
                    Number = 1,
                    Record = new CleanupRecord(DateTimeOffset.Now, [
                        new(@"C:\a\in-bin.iso", @"C:\$Recycle.Bin\S\$R1.iso", 100),
                        new(@"C:\a\emptied.iso", @"C:\$Recycle.Bin\S\$R2.iso", 20),
                        new(@"C:\a\back.iso", @"C:\$Recycle.Bin\S\$R3.iso", 5),
                    ]),
                    Restored = [@"C:\$Recycle.Bin\S\$R3.iso"],
                },
            ],
            DeletedPermanentlyBytes = 3,
        };
        bool InBin(string p) => p.EndsWith("$R1.iso", StringComparison.Ordinal);
        Assert.Equal(20 + 3, HistoryPage.FreedBytes(history, InBin));   // emptied + deleted permanently; not what's in the bin
        Assert.Equal(120, history.FreedBytes);                            // moved and not put back: the title's "Moved"
        Assert.StartsWith("Moved ", HistoryPage.MovedTitle(120));
        Assert.EndsWith(" to the Recycle Bin with freedisk.space", HistoryPage.MovedTitle(120));
    }

    [Fact]
    public void HistoryCountsPermanentDeletesInItems()
    {
        Assert.Equal("1 item deleted permanently", HistoryPage.DeletedText(1));
        Assert.Equal("2 items deleted permanently", HistoryPage.DeletedText(2));
        Assert.Equal("0 items deleted permanently", HistoryPage.DeletedText(0));
    }

    [Fact]
    public void HistoryCapsRowsPerCleanupUntilShowAll()
    {
        Assert.Equal(HistoryPage.RowCap, HistoryPage.ShownRows(1_000, showAll: false));
        Assert.Equal(1_000, HistoryPage.ShownRows(1_000, showAll: true));
        Assert.Equal(3, HistoryPage.ShownRows(3, showAll: false));
    }
}
