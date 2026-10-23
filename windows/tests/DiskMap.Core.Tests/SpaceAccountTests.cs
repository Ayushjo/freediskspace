using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>"Where's the rest?": parts that add up to the used space, measured or the remainder (Mac: SpaceAccount).</summary>
public class SpaceAccountTests
{
    private const long GB = 1_000_000_000;
    private static readonly SpaceAccount.SystemFile[] Files =
        [new("pagefile.sys", 8 * GB), new("hiberfil.sys", 6 * GB), new("swapfile.sys", GB / 4)];

    [Fact]
    public void AFolderScanListsWindowsFilesAndTheRecycleBinApart()
    {
        var account = SpaceAccount.Build(380 * GB, scanIsDriveRoot: false, 184 * GB, 3 * GB, Files, 2, isElevated: false);
        Assert.Equal(["Found by this scan", "Page file", "Hibernation file", "Swap file", "Recycle Bin", "Elsewhere on this drive"],
            account.Rows.Select(r => r.Title));
        Assert.Equal(380 * GB, account.Total);   // the parts add up to what's used
        var rest = account.Rows[^1];
        Assert.Equal(SpaceAccount.Confidence.Remainder, rest.Confidence);
        Assert.Equal(380 * GB - 184 * GB - 8 * GB - 6 * GB - GB / 4 - 3 * GB, rest.Bytes);
        Assert.Contains("outside the folder you scanned", rest.Detail);
        Assert.Contains("2 folders the scan couldn't read", rest.Detail);
        Assert.Contains("System Volume Information", rest.Detail);
        Assert.All(account.Rows.Take(5), r => Assert.Equal(SpaceAccount.Confidence.Exact, r.Confidence));
    }

    [Fact]
    public void AWholeDriveScanAlreadyCountedThem()
    {
        var account = SpaceAccount.Build(380 * GB, scanIsDriveRoot: true, 370 * GB, 3 * GB, Files, 0, isElevated: true);
        Assert.Equal(["Found by this scan", "Not seen by the scan"], account.Rows.Select(r => r.Title));
        Assert.Equal(10 * GB, account.Rows[1].Bytes);
        Assert.DoesNotContain("System Volume Information", account.Rows[1].Detail);   // admin can read it
        Assert.DoesNotContain("outside the folder", account.Rows[1].Detail);
    }

    [Fact]
    public void AScanBiggerThanTheDriveIsCappedAndSaysWhy()
    {
        var account = SpaceAccount.Build(100 * GB, scanIsDriveRoot: true, 120 * GB, null, [], 0, isElevated: true);
        Assert.True(account.ScanExceedsCovered);
        var only = Assert.Single(account.Rows);
        Assert.Equal(100 * GB, only.Bytes);
        Assert.Contains("listed 120 GB, more than this", only.Detail);
        Assert.Equal(100 * GB, account.Total);
    }

    [Fact]
    public void EmptyOrUnknownPartsAreLeftOut()
    {
        var account = SpaceAccount.Build(50 * GB, scanIsDriveRoot: false, 50 * GB, 0, [new("pagefile.sys", 0)], 0, isElevated: true);
        Assert.Equal(["Found by this scan"], account.Rows.Select(r => r.Title));
        Assert.Null(SpaceAccount.Read(null, @"C:\Users\you", 1, 0, true));
    }
}
