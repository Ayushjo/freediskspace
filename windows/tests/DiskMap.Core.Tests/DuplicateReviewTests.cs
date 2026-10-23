using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The Duplicates page's rules (Mac: DuplicatesView's static helpers, DuplicatePreTick and
/// DuplicateGroup's clone sets). Pure logic plus one small real-file search, so these run on
/// any OS.
/// </summary>
public class DuplicateReviewTests
{
    private static string Slashes(string path) => path.Replace('\\', '/');

    // ---- Clone sets and reclaim ----

    [Fact]
    public void ACloneFamilyAndAPlainCopyFreeOneCopyUntilTheFamilyGoes()
    {
        // 1 and 2 share blocks; 3 is a plain copy of the same contents.
        var group = new DuplicateGroup("h", [1, 2, 3], 100, new[] { new[] { 2, 1 } });
        Assert.False(group.SharesStorage);
        Assert.Equal([1, 2], group.CloneSets.Single());
        Assert.Equal(2, group.PhysicalCopies);
        Assert.Equal([1, 2], group.CloneSetOf(2));
        Assert.Null(group.CloneSetOf(3));

        Assert.Equal(100, group.ReclaimableBytes(new HashSet<int> { 3 }));
        Assert.Equal(0, group.ReclaimableBytes(new HashSet<int> { 1 }));          // its clone keeps the blocks
        Assert.Equal(100, group.ReclaimableBytes(new HashSet<int> { 1, 2 }));
        Assert.Equal(200, group.ReclaimableBytes(new HashSet<int> { 1, 2, 3 }));
    }

    [Fact]
    public void OnDiskReclaimCountsEachPhysicalCopyAtItsLargestMember()
    {
        var group = new DuplicateGroup("h", [1, 2, 3], 100, new[] { new[] { 1, 2 } });
        var onDisk = new Dictionary<int, long> { [1] = 4096, [2] = 8192, [3] = 4096 };
        Assert.Equal(4096, group.ReclaimableBytes(new HashSet<int> { 3 }, id => onDisk[id]));
        Assert.Equal(8192, group.ReclaimableBytes(new HashSet<int> { 1, 2 }, id => onDisk[id]));
        Assert.Equal(0, group.ReclaimableBytes(new HashSet<int> { 2 }, id => onDisk[id]));
    }

    [Fact]
    public void ASetCoveringEveryCopySharesStorage()
    {
        var group = new DuplicateGroup("h", [4, 5], 10, new[] { new[] { 5, 4 } });
        Assert.True(group.SharesStorage);
        // The old positional form keeps working: one set of all its members.
        var legacy = new DuplicateGroup("shared-extents", [4, 5], 10, true);
        Assert.Equal([4, 5], legacy.CloneSets.Single());
        Assert.Equal(0, legacy.ReclaimableBytes(new HashSet<int> { 4 }));
        Assert.Equal(10, legacy.ReclaimableBytes(new HashSet<int> { 4, 5 }));
        Assert.Empty(new DuplicateGroup("h", [4, 5], 10, false).CloneSets);
    }

    // ---- Keeper ----

    private static readonly Dictionary<int, int> Days = new() { [1] = 300, [2] = 100, [3] = 100, [4] = 500 };
    private static readonly Dictionary<int, int> PathLengths = new() { [1] = 40, [2] = 30, [3] = 20, [4] = 20 };
    private static readonly DuplicateGroup Four = new("h", [1, 2, 3, 4], 10, false);

    private static int? KeeperFor(DuplicateKeepPolicy policy) =>
        DuplicateReview.Keeper(Four, policy, id => Days[id], id => PathLengths[id]);

    [Fact]
    public void TheOldestCopyStaysByDefaultAndTiesGoToTheLowestId()
    {
        Assert.Equal(DuplicateKeepPolicy.Oldest, DuplicateReview.ParsePolicy(null));
        Assert.Equal(2, KeeperFor(DuplicateKeepPolicy.Oldest));
        Assert.Equal(4, KeeperFor(DuplicateKeepPolicy.Newest));
        // Shortest path: 3 and 4 tie on length; 3 is older.
        Assert.Equal(3, KeeperFor(DuplicateKeepPolicy.ShortestPath));
    }

    [Fact]
    public void PolicyWordsMatchTheMac()
    {
        Assert.Equal("keeps the shortest path", DuplicateKeepPolicy.ShortestPath.Tag());
        Assert.Equal("oldest copy", DuplicateKeepPolicy.Oldest.KeepPhrase());
        Assert.Equal("copy with the shortest path", DuplicateKeepPolicy.ShortestPath.KeepPhrase());
        foreach (var policy in Enum.GetValues<DuplicateKeepPolicy>())
            Assert.Equal(policy, DuplicateReview.ParsePolicy(policy.Key()));
    }

    [Fact]
    public void TheKeptCopyIsListedFirst()
    {
        Assert.Equal([3, 1, 2, 4], DuplicateReview.Ordered(Four, 3));
        Assert.Equal([1, 2, 3, 4], DuplicateReview.Ordered(Four, null));
    }

    // ---- Ticking ----

    [Fact]
    public void TickingTheKeptCopySwapsToTheFirstUntickedOther()
    {
        var swapped = DuplicateReview.Swap(Four, 2, new HashSet<int> { 1, 4 });
        Assert.NotNull(swapped);
        Assert.Equal(3, swapped.Value.Keeper);
        Assert.Equal(new HashSet<int> { 1, 2, 4 }, swapped.Value.Checked);
    }

    [Fact]
    public void ToggleTicksUnticksAndNeverTicksEveryCopy()
    {
        var pair = new DuplicateGroup("h", [1, 2], 10, false);
        var on = DuplicateReview.Toggle(pair, 2, isKeeper: false, new HashSet<int>());
        Assert.Equal(new HashSet<int> { 2 }, on.Checked);
        Assert.False(on.KeepOneRefused);

        var off = DuplicateReview.Toggle(pair, 2, isKeeper: false, on.Checked);
        Assert.Empty(off.Checked);

        // Ticking the keeper of a pair whose other copy is ticked swaps which one stays.
        var swap = DuplicateReview.Toggle(pair, 1, isKeeper: true, new HashSet<int> { 2 });
        Assert.Equal(2, swap.NewKeeper);
        Assert.Equal(new HashSet<int> { 1 }, swap.Checked);
        Assert.False(swap.KeepOneRefused);

        // A plain tick that would leave no copy is undone.
        var trio = new DuplicateGroup("h", [1, 2, 3], 10, false);
        var refused = DuplicateReview.Toggle(trio, 3, isKeeper: false, new HashSet<int> { 1, 2 });
        Assert.True(refused.KeepOneRefused);
        Assert.Equal(new HashSet<int> { 1, 2 }, refused.Checked);
    }

    [Fact]
    public void CopiesInTheBasketCountAsGone()
    {
        var trio = new DuplicateGroup("h", [1, 2, 3], 10, false);
        static bool staged(int id) => id == 3;

        // Ticking 2 when 1 is ticked and 3 is already in the basket would leave nothing.
        var refused = DuplicateReview.Toggle(trio, 2, isKeeper: false, new HashSet<int> { 1 }, staged);
        Assert.True(refused.KeepOneRefused);
        Assert.Equal(new HashSet<int> { 1 }, refused.Checked);

        // A swap never hands "kept" to a copy that is in the basket.
        var swap = DuplicateReview.Swap(trio, 1, new HashSet<int>(), staged);
        Assert.Equal(2, swap?.Keeper);

        // ...and with every other copy in the basket there's nothing to swap to.
        var pair = new DuplicateGroup("h", [1, 2], 10, false);
        var stuck = DuplicateReview.Toggle(pair, 1, isKeeper: true, new HashSet<int>(), id => id == 2);
        Assert.True(stuck.KeepOneRefused);
        Assert.Null(stuck.NewKeeper);
        Assert.Empty(stuck.Checked);
    }

    [Fact]
    public void ExtraCopiesStartTickedExceptStagedToolOwnedAndCloneGroups()
    {
        var content = new DuplicateGroup("a", [1, 2, 3, 4], 10, false);
        var clones = new DuplicateGroup("shared-extents", [5, 6], 10, true);
        var paths = new Dictionary<int, string>
        {
            [1] = @"C:\Users\you\Movies\a.mov",
            [2] = @"C:\Users\you\Desktop\a.mov",
            [3] = "C:/Users/you/code/app/node_modules/pkg/a.mov",
            [4] = @"C:\Users\you\Downloads\a.mov",
            [5] = @"C:\x\b.bin",
            [6] = @"C:\y\b.bin",
        };
        var ticked = DuplicateReview.PreTicked([content, clones], _ => 1, id => paths[id],
            path => path.EndsWith(@"Downloads\a.mov", StringComparison.Ordinal));
        Assert.Equal(new HashSet<int> { 2 }, ticked);
    }

    [Fact]
    public void ToolOwnedLooksAtFoldersAboveTheFileWithEitherSeparator()
    {
        Assert.True(DuplicatePreTick.IsToolOwned(@"C:\src\web\node_modules\left-pad\index.js"));
        Assert.True(DuplicatePreTick.IsToolOwned("/Users/you/src/App/.git/objects/ab"));
        Assert.True(DuplicatePreTick.IsToolOwned(@"C:\Apps\Thing.app\Contents\x.png"));
        Assert.True(DuplicatePreTick.IsToolOwned(@"C:\src\Tool\obj\Debug\a.dll"));
        Assert.False(DuplicatePreTick.IsToolOwned(@"C:\Users\you\Movies\build"));      // the file itself
        Assert.False(DuplicatePreTick.IsToolOwned("/Users/you/Pictures/holiday.jpg"));
    }

    [Fact]
    public void GroupKeysTellCloneOnlyGroupsApart()
    {
        var a = new DuplicateGroup("shared-extents", [7, 3], 1, true);
        var b = new DuplicateGroup("shared-extents", [9, 8], 1, true);
        Assert.NotEqual(DuplicateReview.GroupKey(a), DuplicateReview.GroupKey(b));
    }

    // ---- Words ----

    [Fact]
    public void KindsFollowTheMacsBuckets()
    {
        Assert.Equal(DuplicateKind.Photos, DuplicateReview.KindOf("IMG_0001.HEIC"));
        Assert.Equal(DuplicateKind.Audio, DuplicateReview.KindOf("song.flac"));
        Assert.Equal(DuplicateKind.Videos, DuplicateReview.KindOf("Holiday.mov"));
        Assert.Equal(DuplicateKind.Archives, DuplicateReview.KindOf("backup.zip"));
        Assert.Equal(DuplicateKind.Archives, DuplicateReview.KindOf("installer.iso"));
        Assert.Equal(DuplicateKind.Documents, DuplicateReview.KindOf("report.pdf"));
        Assert.Equal(DuplicateKind.Other, DuplicateReview.KindOf("data.bin"));
        Assert.Equal("Documents", DuplicateKind.Documents.Title());
    }

    [Fact]
    public void PlaceIsHomeRelativeThenScanRelative()
    {
        Assert.Equal("~/Movies/Holiday.mov",
            Slashes(DuplicateReview.Place(@"C:\Users\you\Movies\Holiday.mov", @"C:\Users\you", home: @"C:\Users\you")));
        Assert.Equal("dupdemo/Movies/Holiday.mov",
            Slashes(DuplicateReview.Place(@"D:\dupdemo\Movies\Holiday.mov", @"D:\dupdemo\", home: @"C:\Users\you")));
        Assert.Equal("D:/Movies/Holiday.mov",
            Slashes(DuplicateReview.Place(@"D:\Movies\Holiday.mov", @"D:\", home: @"C:\Users\you")));
        Assert.Equal("E:/other/x.bin",
            Slashes(DuplicateReview.Place(@"E:\other\x.bin", @"D:\dupdemo", home: @"C:\Users\you")));
    }

    [Fact]
    public void DatesReadLikeTheMac()
    {
        Assert.Equal("—", DuplicateReview.DateText(0));
        Assert.Equal("12 Jan 2024", DuplicateReview.DateText((int)(new DateTime(2024, 1, 12) - DateTime.UnixEpoch).TotalDays));
    }

    // ---- The search ----

    [Fact]
    public async Task SearchReportsProgressAndFindsPlainCopies()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"DiskMap-dupreview-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        try
        {
            var bytes = new byte[100_000];
            new Random(7).NextBytes(bytes);
            string a = Path.Combine(dir, "a.bin"), b = Path.Combine(dir, "b.bin"), c = Path.Combine(dir, "c.bin");
            File.WriteAllBytes(a, bytes);
            File.WriteAllBytes(b, bytes);
            bytes[^1] ^= 0xFF;   // same size, same first 64 KB, different contents
            File.WriteAllBytes(c, bytes);
            var phases = new List<DuplicateScanPhase>();
            var groups = await DuplicateFinder.FindDuplicatesAsync(
                [(1, a, bytes.Length), (2, b, bytes.Length), (3, c, bytes.Length)],
                (phase, _, _) => { lock (phases) phases.Add(phase); }, CancellationToken.None);
            var group = Assert.Single(groups);
            Assert.Equal([1, 2], group.FileIDs);
            Assert.False(group.SharesStorage);
            Assert.Contains(DuplicateScanPhase.Hashing, phases);
            Assert.Equal(DuplicateScanPhase.Complete, phases[^1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ACancelledSearchThrowsAndPublishesNothing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateFinder.FindDuplicatesAsync(
            [(1, "x", 10), (2, "y", 10)], null, cancel.Token));
        var tree = new FileTree();
        int root = tree.AddNode("root", -1, true, 0, 0, 0);
        tree.AddNode("a.bin", root, false, 8, 8, 1);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            DuplicateFinder.SizeCollidingCandidates(tree, "root", cancel.Token));
        Assert.Equal("Hashing colliding files…", DuplicateScanPhase.Hashing.Title());
    }
}
