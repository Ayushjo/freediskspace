using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>"What Windows keeps" rows in Where's the rest? (Mac: SystemStorageList).</summary>
public class SystemStorageListTests
{
    private static SystemStorageCatalog.Item Item(long bytes, bool partial = false, bool unknown = false, bool measured = true) =>
        new(new SystemStorageCatalog.Entry("x", "X", [@"C:\x"], SystemStorageCatalog.Action.Guide, "why", ["step"], false,
                Measured: measured),
            [new(@"C:\x", -1, bytes)], partial, new HashSet<string>(), unknown);

    [Fact]
    public void IconsMatchTheMac()
    {
        Assert.Equal("BsLockFill", Dialogs.SystemStorageIcon(SystemStorageCatalog.Action.Never));
        Assert.Equal("BsGearFill", Dialogs.SystemStorageIcon(SystemStorageCatalog.Action.Guide));
        Assert.Equal("BsTrash3Fill", Dialogs.SystemStorageIcon(SystemStorageCatalog.Action.Trash));
    }

    [Fact]
    public void SizeSaysWhenItIsALowerBoundOrUnknown()
    {
        Assert.Equal(ShellFormat.Bytes(2_000_000_000), Dialogs.SystemStorageSize(Item(2_000_000_000)));
        Assert.Null(Dialogs.SystemStorageSizeHelp(Item(2_000_000_000)));
        Assert.Equal(ShellFormat.Bytes(2_000_000_000) + "+", Dialogs.SystemStorageSize(Item(2_000_000_000, partial: true)));
        Assert.Equal("Can't read", Dialogs.SystemStorageSize(Item(0, partial: true)));
        Assert.Equal("Not measured", Dialogs.SystemStorageSize(Item(0, unknown: true, measured: false)));
        Assert.StartsWith("Not measured on purpose", Dialogs.SystemStorageSizeHelp(Item(0, unknown: true, measured: false)));
    }

    [Fact]
    public void StageLinkNamesTheFolderOnlyWhenThereAreSeveral()
    {
        var found = new SystemStorageCatalog.Found(@"C:\Setup\Win11.iso", -1, 1);
        Assert.Equal("Add to the basket", Dialogs.SystemStorageStageTitle(found, 1));
        Assert.Equal("Add Win11.iso to the basket", Dialogs.SystemStorageStageTitle(found, 2));
    }
}
