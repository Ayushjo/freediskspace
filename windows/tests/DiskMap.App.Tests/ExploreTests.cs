using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>Explore's map layout, table wording and list filters (Mac: ExploreCanvas, VisualizeView, BiggestFilesView).</summary>
public class ExploreTests
{
    private static (FileTree Tree, long[] Totals) Tree(params (string Path, long Size)[] files)
    {
        var tree = new FileTree();
        int root = tree.AddNode("Root", -1, true, 0, 0, 0);
        var dirs = new Dictionary<string, int> { [""] = root };
        foreach (var (path, size) in files)
        {
            string[] parts = path.Split('\\');
            string key = "";
            int parent = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                key = key.Length == 0 ? parts[i] : key + "\\" + parts[i];
                if (!dirs.TryGetValue(key, out int dir)) dirs[key] = dir = tree.AddNode(parts[i], parent, true, 0, 0, 0);
                parent = dir;
            }
            tree.AddNode(parts[^1], parent, false, size, size, 0);
        }
        return (tree, tree.RollUpSizes(SizeBasis.Logical));
    }

    [Fact]
    public void BigFoldersHoldTheirBiggestChildrenAndAMoreCell()
    {
        var files = Enumerable.Range(0, 20).Select(i => ($@"Big\f{i:00}.bin", 100L - i)).ToArray();
        var (tree, totals) = Tree(files);
        var rects = SquarifiedTreemap.Layout(tree.ChildrenOf(0, totals), new DmRect(0, 0, 800, 600));
        var (kids, more) = ExploreMap.NestedLayout(rects, tree, totals);
        int big = rects.Single().Id;
        Assert.Equal(9, kids[big].Count);                        // at most 9 shown
        Assert.True(more.ContainsKey(big));                      // the rest as "+ more"
        Assert.Equal(totals[big] - kids[big].Sum(k => totals[k.Id]), more[big].Item2);
        // Children sit inside the box below its 24 pt header, 6 pt in.
        foreach (var k in kids[big])
        {
            Assert.True(k.Rect.Y >= 2 + ExploreMap.HeaderHeight - 0.01);
            Assert.True(k.Rect.X >= 2 + ExploreMap.InnerPadding - 0.01);
        }
    }

    [Fact]
    public void SmallTilesAndFilesHoldNoChildren()
    {
        var (tree, totals) = Tree((@"Tiny\a.bin", 1), (@"huge.bin", 1_000_000));
        var rects = SquarifiedTreemap.Layout(tree.ChildrenOf(0, totals), new DmRect(0, 0, 400, 300));
        var (kids, more) = ExploreMap.NestedLayout(rects, tree, totals);
        Assert.Empty(kids);   // Tiny is a sliver; huge.bin is a file
        Assert.Empty(more);
    }

    [Fact]
    public void ShownChildrenStopAtNinetyPercentOnceTwoAreShown()
    {
        var (tree, totals) = Tree((@"Big\a.bin", 900), (@"Big\b.bin", 60), (@"Big\c.bin", 30), (@"Big\d.bin", 10));
        var rects = SquarifiedTreemap.Layout(tree.ChildrenOf(0, totals), new DmRect(0, 0, 800, 600));
        var (kids, more) = ExploreMap.NestedLayout(rects, tree, totals);
        int big = rects.Single().Id;
        Assert.Equal(2, kids[big].Count);           // 900 + 60 > 90%
        Assert.Equal(40, more[big].Item2);
    }

    [Theory]
    [InlineData(0, 100, "—")]
    [InlineData(100, 100, "Today")]
    [InlineData(99, 100, "Yesterday")]
    [InlineData(94, 100, "6 days ago")]
    [InlineData(93, 100, "1 week ago")]
    [InlineData(86, 100, "2 weeks ago")]
    [InlineData(41, 100, "8 weeks ago")]
    [InlineData(40, 100, "2 months ago")]
    [InlineData(1, 1000, "2 years ago")]
    public void LastUsedMatchesTheMac(int day, int today, string expected) =>
        Assert.Equal(expected, ExplorePage.LastUsed(day, today));

    [Fact]
    public void WhatItIsNamesTheClassButNotOther()
    {
        Assert.Equal("Rebuilds by itself", ExplorePage.WhatItIs(true, StorageClassifier.Media));
        Assert.Equal("—", ExplorePage.WhatItIs(false, StorageClassifier.Other));
        Assert.Equal(StorageClassifier.Media.Title, ExplorePage.WhatItIs(false, StorageClassifier.Media));
    }

    [Theory]
    [InlineData("Wedding edit 4K.mov", FileKind.Video, "MOV")]
    [InlineData("model.safetensors", FileKind.Other, "SAF")]   // the Mac render shows SAF
    [InlineData("README", FileKind.Other, "FILE")]
    [InlineData("backup", FileKind.DeviceBackup, "BAK")]
    [InlineData("disk.vhdx", FileKind.VirtualDisk, "VHDX")]
    [InlineData("archive.tar.gz", FileKind.Archive, "GZ")]
    public void BadgesShowTheExtension(string name, FileKind kind, string expected) =>
        Assert.Equal(expected, ListKit.Badge(name, kind));

    [Fact]
    public void KindColumnFallsBackToTheStorageClass()
    {
        Assert.Equal("Disk image", ListKit.KindText(FileKind.DiskImage, StorageClassifier.Other));
        Assert.Equal("File", ListKit.KindText(FileKind.Other, StorageClassifier.Other));
        Assert.Equal(StorageClassifier.AiModels.Title, ListKit.KindText(FileKind.Other, StorageClassifier.AiModels));
    }

    private static BiggestFilesExplorePage.Entry Entry(int id, string name, FileKind kind, long size, int day, string folder, string cls = "other") =>
        new(id, name, $@"C:\u\{folder}\{name}", folder, kind, size, day, name.ToLowerInvariant(), folder.ToLowerInvariant(),
            new StorageVerdict(StorageClassifier.All.First(c => c.Id == cls), CleanupAdvice.Fine));

    [Fact]
    public void FiltersCombineAndSortLargestFirst()
    {
        var entries = new[]
        {
            Entry(1, "a.mov", FileKind.Video, 10, 100, "Videos", "media"),
            Entry(2, "b.mov", FileKind.Video, 30, 900, "Videos", "media"),
            Entry(3, "c.zip", FileKind.Archive, 20, 100, "Downloads", "downloads"),
            Entry(4, "d.bin", FileKind.Other, 40, 0, "Other"),
        };
        int today = 1000;
        var all = BiggestFilesExplorePage.Filter(entries, ListQuery.Parse(""), null, BiggestFilesExplorePage.AgeFilter.Any, null, today);
        Assert.Equal([4, 2, 3, 1], all.Select(e => e.Id));
        var videos = BiggestFilesExplorePage.Filter(entries, ListQuery.Parse(""), FileKind.Video, BiggestFilesExplorePage.AgeFilter.Any, null, today);
        Assert.Equal([2, 1], videos.Select(e => e.Id));
        var old = BiggestFilesExplorePage.Filter(entries, ListQuery.Parse(""), null, BiggestFilesExplorePage.AgeFilter.ThreeMonths, null, today);
        Assert.Equal([3, 1], old.Select(e => e.Id));            // unknown dates never count as old
        var category = BiggestFilesExplorePage.Filter(entries, ListQuery.Parse(""), null, BiggestFilesExplorePage.AgeFilter.Any, "downloads", today);
        Assert.Equal([3], category.Select(e => e.Id));
        var searched = BiggestFilesExplorePage.Filter(entries, ListQuery.Parse("in:videos size>15"), null, BiggestFilesExplorePage.AgeFilter.Any, null, today);
        Assert.Equal([2], searched.Select(e => e.Id));
    }

    [Theory]
    [InlineData(@"C:\Users\you\Downloads\misc\a.zip", @"C:\Users\you", @"Downloads\misc")]
    [InlineData(@"C:\Users\you\a.zip", @"C:\Users\you", @"\")]
    public void RelativeParentIsFromTheScan(string path, string root, string expected) =>
        Assert.Equal(expected, BiggestFilesExplorePage.RelativeParent(path, root));
}
