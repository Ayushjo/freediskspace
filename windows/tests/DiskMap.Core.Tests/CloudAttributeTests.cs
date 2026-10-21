using DiskMap.Core;
using DiskMap.Core.Native;

namespace DiskMap.Core.Tests;

/// <summary>
/// One cloud-placeholder rule for every scan path. 0x40000 is
/// RECALL_ON_OPEN only in directory listings; in NTFS's on-disk record
/// (what the MFT scanner reads) the same bit is FILE_ATTRIBUTE_EA.
/// </summary>
public class CloudAttributeTests
{
    private const uint Ea = 0x40000;   // same value as RECALL_ON_OPEN

    [Fact]
    public void ExtendedAttributesOnDiskAreNotACloudFile()
    {
        var file = ScanEngine.DecideFromAttributes(Ea, 4096, 4096, fromDirectoryEnumeration: false);
        Assert.True(file.Include);
        Assert.False(file.NotDownloaded);
        Assert.Equal(4096, file.AllocatedSize);

        var folder = ScanEngine.DecideFromAttributes(Ea | Win32.FILE_ATTRIBUTE_DIRECTORY, 0, 0, fromDirectoryEnumeration: false);
        Assert.True(folder.Include);
        Assert.False(folder.SkipDescendants);   // its contents are still scanned
    }

    [Fact]
    public void RecallOnOpenFromAListingIsACloudFile()
    {
        var d = ScanEngine.DecideFromAttributes(Win32.FILE_ATTRIBUTE_RECALL_ON_OPEN, 1000, 0, fromDirectoryEnumeration: true);
        Assert.True(d.NotDownloaded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OfflineAndRecallOnDataAccessAreCloudFromEitherSource(bool fromListing)
    {
        Assert.True(ScanEngine.IsCloudPlaceholder(Win32.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS, fromListing));
        Assert.True(ScanEngine.IsCloudPlaceholder(Win32.FILE_ATTRIBUTE_OFFLINE, fromListing));
        Assert.False(ScanEngine.IsCloudPlaceholder(0x20 /* ARCHIVE */, fromListing));
    }

    [Fact]
    public void ACloudFolderIsRecordedButNotDescended()
    {
        var d = ScanEngine.DecideFromAttributes(
            Win32.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Win32.FILE_ATTRIBUTE_DIRECTORY | Win32.FILE_ATTRIBUTE_REPARSE_POINT,
            0, 0, fromDirectoryEnumeration: true);
        Assert.True(d.Include);
        Assert.True(d.NotDownloaded);
        Assert.True(d.SkipDescendants);
    }

    [Fact]
    public void ANonCloudReparsePointIsSkipped()
    {
        var d = ScanEngine.DecideFromAttributes(Win32.FILE_ATTRIBUTE_REPARSE_POINT | Win32.FILE_ATTRIBUTE_DIRECTORY, 0, 0);
        Assert.False(d.Include);
    }
}
