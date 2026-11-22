using System.Runtime.InteropServices;
using DiskMap.Core;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Tests;

/// <summary>
/// The duplicates and applications audit: every copy ending in the basket, hashes trusted
/// after files changed, program and cloud files ticked for you, the scan root never checked,
/// hard links hidden behind holes in their maps, and the Applications page's app data
/// (a program's own folder, words in a name, junctions and shares).
/// </summary>
public class DuplicatesAppsAuditTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"DiskMap-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int Dir(FileTree tree, int parent, string name) => tree.AddNode(name, parent, true, 0, 0, 1);

    private static int Leaf(FileTree tree, int parent, string name) => tree.AddNode(name, parent, false, 50, 50, 1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLinkW(string newName, string existing, IntPtr security);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    // ---- 1. The kept copy is never one that goes with the basket ----

    [Fact]
    public void ACopyInsideAFolderInTheBasketIsNeverTheKeptOne()
    {
        var group = new DuplicateGroup("h", [1, 2, 3], 10, false);
        var days = new Dictionary<int, int> { [1] = 10, [2] = 20, [3] = 30 };
        static bool covered(int id) => id == 1;   // D:\Old backup\a.mov, its folder added from Biggest Folders
        Assert.Equal(1, DuplicateReview.Keeper(group, DuplicateKeepPolicy.Oldest, id => days[id], _ => 5));
        Assert.Equal(2, DuplicateReview.Keeper(group, DuplicateKeepPolicy.Oldest, id => days[id], _ => 5, covered));
        // Not even when the person chose it; another choice still stands.
        Assert.Equal(2, DuplicateReview.Keeper(group, DuplicateKeepPolicy.Oldest, id => days[id], _ => 5, covered, chosen: 1));
        Assert.Equal(3, DuplicateReview.Keeper(group, DuplicateKeepPolicy.Oldest, id => days[id], _ => 5, covered, chosen: 3));
        // With every copy going, there is nothing better to pick.
        Assert.Equal(1, DuplicateReview.Keeper(group, DuplicateKeepPolicy.Oldest, id => days[id], _ => 5, _ => true));

        // Ticking the last copy that doesn't go with the basket is refused.
        var refused = DuplicateReview.Toggle(group, 3, isKeeper: false, new HashSet<int> { 2 }, covered);
        Assert.True(refused.KeepOneRefused);
        Assert.Equal(new HashSet<int> { 2 }, refused.Checked);
    }

    // ---- 2. Hashes are checked again before staging ----

    [Fact]
    public async Task CopiesThatChangedSinceTheSearchAreLeftOut()
    {
        string dir = NewDir();
        try
        {
            var bytes = new byte[200_000];
            new Random(11).NextBytes(bytes);
            var paths = new Dictionary<int, string>
            {
                [1] = Path.Combine(dir, "a.bin"), [2] = Path.Combine(dir, "b.bin"), [3] = Path.Combine(dir, "c.bin"),
            };
            foreach (var path in paths.Values) File.WriteAllBytes(path, bytes);
            var group = Assert.Single(await DuplicateFinder.FindDuplicatesAsync(
                paths.Select(p => (p.Key, p.Value, (long)bytes.Length)).ToList()));
            Assert.Equal([1, 2, 3], group.Stamps!.Keys.Order());
            string pathOf(int id) => paths[id];
            static bool none(int _) => false;

            var fresh = DuplicateReview.Recheck(group, new HashSet<int> { 2, 3 }, none, pathOf);
            Assert.True(fresh.KeptCopyIntact);
            Assert.Empty(fresh.Changed);

            // c edited in place (same length, later write) and b grown: both ticks are dropped.
            File.SetLastWriteTimeUtc(paths[3], File.GetLastWriteTimeUtc(paths[3]).AddMinutes(1));
            File.AppendAllText(paths[2], "x");
            var edited = DuplicateReview.Recheck(group, new HashSet<int> { 2, 3 }, none, pathOf);
            Assert.True(edited.KeptCopyIntact);
            Assert.Equal(new HashSet<int> { 2, 3 }, edited.Changed);

            // The kept copy vanished and the other unticked one changed: nothing of the group may go in.
            File.Delete(paths[1]);
            var orphaned = DuplicateReview.Recheck(group, new HashSet<int> { 2 }, none, pathOf);
            Assert.False(orphaned.KeptCopyIntact);
            Assert.Equal(new HashSet<int> { 2 }, orphaned.Changed);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AKeptCopyInTheBasketDoesNotCountAndUnstampedGroupsAreNeverTrusted()
    {
        var stamp = new FileStamp(10, 42);
        var group = new DuplicateGroup("h", [1, 2, 3], 10, false)
        {
            Stamps = new Dictionary<int, FileStamp> { [1] = stamp, [2] = stamp, [3] = stamp },
        };
        var same = DuplicateReview.Recheck(group, new HashSet<int> { 3 }, id => id == 1, _ => "x", _ => stamp);
        Assert.True(same.KeptCopyIntact);   // 2 stays
        var allGoing = DuplicateReview.Recheck(group, new HashSet<int> { 3 }, id => id is 1 or 2, _ => "x", _ => stamp);
        Assert.False(allGoing.KeptCopyIntact);

        var unstamped = new DuplicateGroup("h", [1, 2], 10, false);
        var never = DuplicateReview.Recheck(unstamped, new HashSet<int> { 2 }, _ => false, _ => "x", _ => stamp);
        Assert.False(never.KeptCopyIntact);
        Assert.Equal(new HashSet<int> { 2 }, never.Changed);
    }

    // ---- 3. Program files ----

    [Fact]
    public void AFolderHoldingAProgramIsNeverSearched()
    {
        var tree = new FileTree();
        int root = tree.AddNode("D:", -1, true, 0, 0, 0);
        foreach (string game in new[] { "Alpha", "Beta" })
        {
            int folder = Dir(tree, Dir(tree, root, "Old PC " + game), game);
            Leaf(tree, folder, game + ".exe");
            Leaf(tree, folder, "UnityPlayer.dll");
            Leaf(tree, Dir(tree, folder, "Data"), "level0");   // the whole folder is the program's
        }
        int photos = Dir(tree, root, "Photos");
        Leaf(tree, photos, "a.jpg");
        Leaf(tree, photos, "b.jpg");
        // An .exe alone (an installer) or a .dll alone doesn't make a program.
        int setups = Dir(tree, root, "Setups");
        Leaf(tree, setups, "setup.exe");
        Leaf(tree, setups, "notes.txt");

        var found = DuplicateFinder.Candidates(tree, @"D:\").Select(c => tree.NameOf(c.Id)).Order().ToList();
        Assert.Equal(["a.jpg", "b.jpg", "notes.txt", "setup.exe"], found);

        // A profile with a stray tool in it is still where personal files live.
        var home = new FileTree();
        int me = Dir(home, Dir(home, home.AddNode("C:", -1, true, 0, 0, 0), "Users"), "me");
        Leaf(home, me, "tool.exe");
        Leaf(home, me, "tool.dll");
        int downloads = Dir(home, me, "Downloads");
        Leaf(home, downloads, "a.zip");
        Leaf(home, downloads, "b.zip");
        Assert.Equal(2, DuplicateFinder.Candidates(home, @"C:\").Count(c => home.NameOf(c.Id).EndsWith(".zip")));
    }

    [Fact]
    public void ProgramFilesAreNeverTickedForYou()
    {
        Assert.True(DuplicatePreTick.NeverPreTicked(@"D:\Old PC\Beta\UnityPlayer.dll"));
        Assert.True(DuplicatePreTick.NeverPreTicked(@"D:\Old PC\Beta\Beta.EXE"));
        Assert.True(DuplicatePreTick.NeverPreTicked(@"D:\drivers\filter.sys"));
        Assert.False(DuplicatePreTick.NeverPreTicked(@"D:\Photos\a.jpg"));
        var group = new DuplicateGroup("h", [1, 2], 10, false);
        var paths = new Dictionary<int, string> { [1] = @"D:\A\libcef.dll", [2] = @"D:\B\libcef.dll" };
        Assert.Empty(DuplicateReview.PreTicked([group], _ => 1, id => paths[id], _ => false));
    }

    // ---- 4. Cloud files ----

    [Fact]
    public void OnlineOnlyFilesAreNeverOpened()
    {
        Assert.True(DuplicateFinder.IsOnlineOnly(FileAttributes.Offline));
        Assert.True(DuplicateFinder.IsOnlineOnly((FileAttributes)0x400000));   // RECALL_ON_DATA_ACCESS
        Assert.True(DuplicateFinder.IsOnlineOnly((FileAttributes)0x40000));    // RECALL_ON_OPEN
        Assert.False(DuplicateFinder.IsOnlineOnly(FileAttributes.Archive | FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task AFileThatWentOnlineOnlySinceTheScanIsNotRead()
    {
        string dir = NewDir();
        string a = Path.Combine(dir, "a.bin"), b = Path.Combine(dir, "b.bin");
        try
        {
            var bytes = new byte[100_000];
            new Random(5).NextBytes(bytes);
            File.WriteAllBytes(a, bytes);
            File.WriteAllBytes(b, bytes);
            // The scan saw both local; b has since been freed up to the cloud.
            File.SetAttributes(b, FileAttributes.Offline);
            Assert.True(File.GetAttributes(b).HasFlag(FileAttributes.Offline), "fixture attribute");
            Assert.Empty(await DuplicateFinder.FindDuplicatesAsync([(1, a, bytes.Length), (2, b, bytes.Length)]));
        }
        finally
        {
            if (File.Exists(b)) File.SetAttributes(b, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\Users\me\OneDrive\Photos\a.jpg", true)]
    [InlineData(@"C:\Users\me\OneDrive - Contoso\backup.iso", true)]   // a file rule must not hide the folder
    [InlineData(@"C:\Users\me\Dropbox\a.jpg", true)]
    // OneDrive's backed-up known folders: a deeper rule must not win over the cloud folder.
    [InlineData(@"C:\Users\me\OneDrive\Pictures\Camera Roll\a.jpg", true)]
    [InlineData(@"C:\Users\me\OneDrive\Documents\report.pdf", true)]
    [InlineData(@"C:\Users\me\OneDrive\Desktop\Downloads\x.zip", true)]
    [InlineData(@"C:\Users\me\Google Drive\My Drive\a.mov", true)]
    [InlineData(@"C:\Users\me\Pictures\a.jpg", false)]
    public void CopiesInACloudFolderAreNeverTickedForYou(string path, bool never) =>
        Assert.Equal(never, DuplicatePreTick.NeverPreTicked(path));

    // ---- 5. The scan root ----

    [Fact]
    public void ARepositoryOrProgramScannedOnItsOwnOffersNothing()
    {
        var repo = new FileTree();
        int root = repo.AddNode("site", -1, true, 0, 0, 0);
        Dir(repo, root, ".git");
        Leaf(repo, root, "a.png");
        Leaf(repo, root, "b.png");
        Assert.Empty(DuplicateFinder.Candidates(repo, @"D:\src\site"));

        var game = new FileTree();
        root = game.AddNode("Alpha", -1, true, 0, 0, 0);
        Leaf(game, root, "Alpha.exe");
        Leaf(game, root, "UnityPlayer.dll");
        int data = Dir(game, root, "Data");
        Leaf(game, data, "a.bin");
        Leaf(game, data, "b.bin");
        Assert.Empty(DuplicateFinder.Candidates(game, @"D:\Old PC\Alpha"));

        // A profile scanned on its own keeps its exemption: a stray package.json doesn't hide it.
        var home = new FileTree();
        root = home.AddNode("me", -1, true, 0, 0, 0);
        Leaf(home, root, "package.json");
        int downloads = Dir(home, root, "Downloads");
        Leaf(home, downloads, "a.zip");
        Leaf(home, downloads, "b.zip");
        Assert.Equal(2, DuplicateFinder.Candidates(home, @"C:\Users\me").Count(c => home.NameOf(c.Id).EndsWith(".zip")));
    }

    // ---- 6. Hard links and clones ----

    /// <summary>64 KB, a 4 MB hole, 64 KB: the map has a sparse run, so maps can't be compared.</summary>
    private static void WriteSparse(string path, byte[] data)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        Assert.True(DeviceIoControl(stream.SafeFileHandle, 0x000900C4 /* FSCTL_SET_SPARSE */,
            IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "fixture sparse flag");
        stream.Write(data);
        stream.Seek(4 << 20, SeekOrigin.Current);
        stream.Write(data);
    }

    [Fact]
    public async Task HardLinksAreOneFileEvenWhenTheirMapHasHoles()
    {
        string dir = NewDir();
        try
        {
            string original = Path.Combine(dir, "sparse.bin"), link = Path.Combine(dir, "link.bin");
            var data = new byte[65_536];
            new Random(3).NextBytes(data);
            WriteSparse(original, data);
            Assert.True(CreateHardLinkW(link, original, IntPtr.Zero), "fixture hard link");
            // What the old check gave up on: a hole in the map, so the pair read as a duplicate freeing 0.
            Assert.Contains(CloneDetector.ExtentMapOf(original)!, e => e.DeviceOffset == -1);

            long length = new FileInfo(original).Length;
            var result = await DuplicateFinder.ScanAsync([(1, original, length), (2, link, length)]);
            Assert.Equal(0, result.FullContentHashCalls);
            var group = Assert.Single(result.Groups);
            Assert.True(group.SharesStorage);
            Assert.True(CloneDetector.AreLikelyClones(original, link));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheClonePassGroupsByFileRecordAndStopsWhenCancelled()
    {
        string dir = NewDir();
        try
        {
            var bytes = new byte[50_000];
            new Random(9).NextBytes(bytes);
            string a = Path.Combine(dir, "a.bin"), a2 = Path.Combine(dir, "a2.bin"), a3 = Path.Combine(dir, "a3.bin");
            string b = Path.Combine(dir, "b.bin"), c = Path.Combine(dir, "c.bin");
            File.WriteAllBytes(a, bytes);
            File.WriteAllBytes(b, bytes);
            File.WriteAllBytes(c, bytes);
            Assert.True(CreateHardLinkW(a2, a, IntPtr.Zero) && CreateHardLinkW(a3, a, IntPtr.Zero), "fixture hard links");

            var (families, alone) = CloneDetector.Families(new[] { a, b, a2, c, a3 }, p => p);
            Assert.Equal(new[] { a, a2, a3 }.Order(), Assert.Single(families).Order());
            Assert.Equal(new[] { b, c }.Order(), alone.Order());

            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => CloneDetector.Families(new[] { a, b }, p => p, cancel.Token));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AnExtentMapBufferIsBoundedHoweverBigTheFile()
    {
        Assert.Equal(4096, CloneDetector.MapBufferSize(10_000));
        Assert.InRange(CloneDetector.MapBufferSize(1L << 30), 4097, (1 << 20) - 1);
        Assert.Equal(1 << 20, CloneDetector.MapBufferSize(1L << 40));   // a sparse terabyte used to ask for ~490 MB
    }

    // ---- 8. Applications: what counts as an app's data ----

    private static readonly ApplicationsCatalog.Places Places = new(
        RoamingAppData: @"C:\Users\me\AppData\Roaming",
        LocalAppData: @"C:\Users\me\AppData\Local",
        ProgramData: @"C:\ProgramData",
        Documents: @"C:\Users\me\Documents",
        Programs: @"C:\Users\me\AppData\Local\Programs");

    [Fact]
    public void AProgramsOwnFolderIsNeverItsData()
    {
        // No InstallLocation in the registry (87 of 175 programs on the owner's PC): the path can't tell.
        var items = ApplicationsCatalog.RelatedItems(
        [
            (@"C:\Users\me\AppData\Local\Programs\Event Log Explorer", 90_000_000),
            (@"C:\Users\me\AppData\Roaming\Event Log Explorer", 4_000),
        ], installPath: "", Places);
        var item = Assert.Single(items);
        Assert.Equal(ApplicationRelatedKind.RoamingData, item.Kind);   // "Log" isn't its last word
        Assert.False(ApplicationsCatalog.TickedByDefault(item.Kind));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Roaming\Event Log Explorer", ApplicationRelatedKind.RoamingData)]
    [InlineData(@"C:\Users\me\AppData\Local\Cache Cleaner", ApplicationRelatedKind.LocalData)]
    [InlineData(@"C:\Users\me\AppData\Local\Zoom Cache", ApplicationRelatedKind.Caches)]
    [InlineData(@"C:\Users\me\AppData\Roaming\zoom.log", ApplicationRelatedKind.Logs)]
    [InlineData(@"C:\ProgramData\Vendor Logs", ApplicationRelatedKind.Logs)]
    public void TheLastWordSaysWhatAFolderIs(string path, ApplicationRelatedKind kind) =>
        Assert.Equal(kind, ApplicationsCatalog.RelatedKind(path, Places));

    // ---- 9. Sizes never follow a junction or reach a share ----

    [Fact]
    public void AppSizesNeverFollowAJunction()
    {
        string root = NewDir();
        string data = Path.Combine(root, "data"), outside = Path.Combine(root, "outside");
        string link = Path.Combine(data, "linked");
        try
        {
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(data, "own.bin"), new byte[5000]);   // two 4K clusters
            File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[3_000_000]);
            var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd", $"/c mklink /J \"{link}\" \"{outside}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            Assert.True(Directory.Exists(link), "fixture junction");

            Assert.Equal(8192, AppLeftoverFinder.AllocatedSize(data));
            Assert.Equal(0, AppLeftoverFinder.AllocatedSize(link));   // the link itself frees nothing
        }
        finally
        {
            // The link first (never what it points at), then the rest.
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(@"\\fileserver\apps\Tool", false)]
    [InlineData(@"""\\fileserver\apps\Tool""", false)]
    [InlineData(@"//fileserver/apps/Tool", false)]
    [InlineData(@"\\?\UNC\fileserver\apps", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    [InlineData(@"\??\C:\Tools\App", false)]
    [InlineData(@"D:\Apps\Tool", true)]
    public void OnlyLocalInstallFoldersAreMeasured(string path, bool local)
    {
        Assert.Equal(local, AppLeftoverFinder.IsLocalPath(path));
        Assert.Equal(local, ApplicationsCatalog.IsUsableInstallPath(path, []));
    }
}
