using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>The Caches, Old downloads and Large media rows (Mac: CachesReviewView.rows, OldDownloadsView.row, LargeMediaView.row).</summary>
public class CleanDetailPagesTests
{
    private const long MB = 1_000_000;
    private const long GB = 1_000_000_000;
    private const string Root = @"C:\Users\you";

    [Fact]
    public void ACacheRowIsTheAppWithItsFirstFolderAndHowManyPlaces()
    {
        var chrome = new ReviewableTarget("cache-Chrome", ReviewableCategory.Caches, "Chrome", "Browser cache", 2_400 * MB,
            $@"{Root}\AppData\Local\Google\Chrome\User Data\Default\Cache",
            [$@"{Root}\AppData\Local\Google\Chrome\User Data\Default\Cache", $@"{Root}\AppData\Local\Google\Chrome\User Data\Default\Code Cache"],
            [40, 41], new SafetyAssessment(SafetyLevel.Safe, "", "Cache"),
            "Chrome can recreate this. Some sites may load more slowly next time.", "globe", null);
        var npm = chrome with { Id = "qw-9", Category = ReviewableCategory.PackageCaches, DisplayName = "npm cache" };
        var rows = CleanCachesPage.Rows([npm, chrome], Root);
        var row = Assert.Single(rows);
        Assert.Equal("Chrome", row.Name);
        Assert.Equal("APP", row.Badge);
        Assert.Equal(@"AppData\Local\Google\Chrome\User Data\Default", row.Place);
        Assert.Equal("Rebuilds", row.Kind);
        Assert.Equal("2 places", row.Changed);
        Assert.True(row.Accent);
        Assert.Equal(40, row.NodeId);
        Assert.Equal(2, row.Requests.Count);
        Assert.Equal(2, row.StagePaths.Count);
    }

    [Fact]
    public void ADownloadRowSaysWhetherItIsLikelyDoneWith()
    {
        int today = AgeMap.Today();
        var iso = new OldDownloadsCandidate(7, "ubuntu-24.04-desktop.iso", $@"{Root}\Downloads\ubuntu-24.04-desktop.iso",
            @"Downloads\ubuntu-24.04-desktop.iso", 6_100 * MB, today - 240, 240, FileKind.DiskImage,
            OldDownloadsStatus.LikelyDisposable, new SafetyAssessment(SafetyLevel.Review, "", "iso"),
            "Installer or disk image (6.1 GB) in Downloads", "rec", 1);
        var row = CleanDownloadsPage.Row(iso, Root, today);
        Assert.Equal("ISO", row.Badge);
        Assert.Equal("Downloads", row.Place);
        Assert.Equal("Likely done with", row.Kind);
        Assert.Equal("8 months ago", row.Changed);
        Assert.Equal("Installer or disk image (6.1 GB) in Downloads", row.Note);
        Assert.True(row.Accent);
        Assert.Equal("Old Downloads: ubuntu-24.04-desktop.iso", Assert.Single(row.Requests).Reason);
    }

    [Fact]
    public void AMediaFolderReadsAsDirAndProtectedMediaCannotBeAdded()
    {
        int today = AgeMap.Today();
        var library = new MediaCandidate(3, "Photos Library", $@"{Root}\Pictures\Photos Library", @"Pictures\Photos Library",
            38_200 * MB, today - 2, 2, MediaKind.Project, MediaLocationBucket.Pictures, MediaStatus.ReviewFirst,
            new SafetyAssessment(SafetyLevel.Protected, "", "Photos Library"), "why", "", true, 1);
        var row = CleanMediaPage.Row(library, Root, today);
        Assert.Equal("DIR", row.Badge);
        Assert.Equal("Media project", row.Kind);
        Assert.Equal("Pictures", row.Place);
        Assert.Equal("why", row.Note);
        Assert.False(row.Accent);
        Assert.Empty(row.Requests);
        Assert.False(CleanDetail.InBasket(row, _ => true));
    }
}
