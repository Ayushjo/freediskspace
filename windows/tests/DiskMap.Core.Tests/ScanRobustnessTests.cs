using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DiskMap.Core;
using DiskMap.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Tests;

/// <summary>
/// Scan robustness against hostile or odd input: user-writable caches and
/// snapshots (the app runs elevated), corrupt volumes, malformed journal
/// pages, very deep trees. The MFT and USN readers need administrator
/// rights, so their parsers are fed crafted bytes here.
/// </summary>
public class ScanRobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "diskmap-robust-" + Guid.NewGuid().ToString("N")[..8]);

    public ScanRobustnessTests() => Directory.CreateDirectory(_dir);

    private readonly List<string> _links = [];

    public void Dispose()
    {
        // A recursive delete is refused at a junction ("access denied"), so links go first, on their own.
        foreach (var link in _links)
            try { Directory.Delete(link); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- 1. Untrusted packed trees ----

    /// <summary>A tree's packed arrays, copied so one field can be corrupted.</summary>
    private sealed class Packed(FileTree t)
    {
        public readonly List<string> Names = [.. t.NameTable];
        public readonly int[] NameIndex = [.. t.NameIndex];
        public readonly int[] Parent = [.. t.Parent];
        public readonly int[] FirstChild = [.. t.FirstChild];
        public readonly int[] NextSibling = [.. t.NextSibling];
        public readonly long[] Logical = [.. t.LogicalSize];
        public readonly long[] Allocated = [.. t.AllocatedSize];
        public readonly int[] Modified = [.. t.ModifiedDay];
        public readonly int[] Created = [.. t.CreatedDay];
        public readonly bool[] IsDirectory = [.. t.IsDirectory];
        public readonly byte[] Flags = [.. t.Flags];
        public readonly long[] FileId = [.. t.FileId];

        public bool Into(FileTree target) => target.ReplacePacked(Names, NameIndex, Parent, FirstChild,
            NextSibling, Logical, Allocated, Modified, Created, IsDirectory, Flags, FileId);

        public void Rename(int node, string name)
        {
            Names.Add(name);
            NameIndex[node] = Names.Count - 1;
        }
    }

    /// <summary>
    /// 0 "C:" → { 4 b.bin, 1 docs → { 3 "trailing. ", 2 notes.txt } } (children
    /// are prepended, so each list runs newest first). A drive root's name has a
    /// colon; only child names are checked.
    /// </summary>
    private static FileTree Sample()
    {
        var tree = new FileTree();
        int root = tree.AddNode("C:", -1, true, 0, 0, 0);
        int docs = tree.AddNode("docs", root, true, 0, 0, 0, fileId: 10);
        tree.AddNode("notes.txt", docs, false, 100, 4096, 0, fileId: 11);
        tree.AddNode("trailing. ", docs, false, 5, 4096, 0, fileId: 12);
        tree.AddNode("b.bin", root, false, 50, 4096, 0, fileId: 13);
        return tree;
    }

    [Fact]
    public void ARealTreeRoundTripsThroughTheCodec()
    {
        var tree = Sample();
        var decoded = SnapshotCodec.Decode(SnapshotCodec.Encode(
            new DiskSnapshot(@"C:\", DateTimeOffset.UnixEpoch, tree))).Tree;
        Assert.Equal(tree.Count, decoded.Count);
        for (int i = 0; i < tree.Count; i++)
            Assert.Equal(tree.PathOf(i, @"C:\"), decoded.PathOf(i, @"C:\"));
        Assert.Equal(tree.RollUpSizes(), decoded.RollUpSizes());
        Assert.True(new Packed(tree).Into(new FileTree()));
    }

    [Fact]
    public void CorruptPackedTreesAreRejectedAndLeaveTheTreeAlone()
    {
        var corruptions = new (string Why, Action<Packed> Corrupt)[]
        {
            // docs and notes.txt become each other's parent, off the root:
            // PathOf(1) used to loop forever.
            ("parent cycle", p =>
            {
                p.NextSibling[4] = -1;
                p.Parent[1] = 2;
                p.Parent[2] = 1;
                p.FirstChild[2] = 1;
            }),
            ("sibling cycle", p => p.NextSibling[2] = 3),
            ("child names another parent", p => p.Parent[2] = 0),
            ("root has a parent", p => p.Parent[0] = 4),
            ("root has a sibling", p => p.NextSibling[0] = 4),
            ("node never reached", p => p.FirstChild[0] = 1),
            ("link past the end", p => p.FirstChild[4] = 5),
            ("link below -1", p => p.NextSibling[2] = -2),
            ("name index past the table", p => p.NameIndex[2] = 99),
            ("negative logical size", p => p.Logical[2] = -1),
            ("negative allocated size", p => p.Allocated[4] = long.MinValue),
            ("name climbs out", p => p.Rename(2, @"..\..\Windows")),
            ("name with a slash", p => p.Rename(2, "a/b")),
            ("name with a stream colon", p => p.Rename(2, "a:b")),
            ("name with NUL", p => p.Rename(2, "a\0b")),
            ("name ..", p => p.Rename(2, "..")),
            ("name .", p => p.Rename(2, ".")),
            ("empty name", p => p.Rename(2, "")),
        };
        foreach (var (why, corrupt) in corruptions)
        {
            var packed = new Packed(Sample());
            corrupt(packed);
            var target = Sample();
            Assert.False(packed.Into(target), why);
            Assert.Equal(5, target.Count);
            Assert.Equal("notes.txt", target.NameOf(2));
        }
    }

    [Fact]
    public void SafeNamesKeepTrailingDotsAndSpaces()
    {
        foreach (var ok in new[] { "a", "a.", "a ", "...", "node_modules", "日本語" })
            Assert.True(FileTree.IsSafeName(ok), ok);
        foreach (var bad in new[] { "", ".", "..", @"a\b", "a/b", "a:b", "a\0b" })
            Assert.False(FileTree.IsSafeName(bad), bad);
    }

    [Fact]
    public void ASnapshotClaimingHugeCountsFailsWithoutAllocatingThem()
    {
        var w = new MemoryStream();
        w.Write("DMAP"u8);
        w.Write(BitConverter.GetBytes(4u));
        w.Write(BitConverter.GetBytes(0L));
        w.Write(BitConverter.GetBytes(0u));            // empty root path
        w.Write(BitConverter.GetBytes(49_999_999));    // node count — and no arrays follow
        w.Write(BitConverter.GetBytes(1));
        byte[] bytes = w.ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<SnapshotException>(() => SnapshotCodec.Decode(bytes));
        Assert.Equal(SnapshotError.Corrupt, ex.Error);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 << 20, "decoded a 200 MB array first");
    }

    // ---- 2. A failing accelerated backend falls back ----

    [Fact]
    public void ABackendThatThrowsFallsBackWithItsMessage()
    {
        Assert.Null(ScanEngine.Guarded((out string? why) => throw new InvalidDataException("baseline is corrupt"),
            out var reason));
        Assert.Equal("baseline is corrupt", reason);
        Assert.Throws<OperationCanceledException>(() =>
            ScanEngine.Guarded((out string? why) => throw new OperationCanceledException(), out _));
    }

    // ---- 3. Incremental-rescan trust ----

    /// <summary>root → d0..d(n-1), each holding f.txt; created on disk under <paramref name="root"/> unless <paramref name="onDisk"/> is false.</summary>
    private static FileTree Folders(string root, int count, bool onDisk = true)
    {
        var tree = new FileTree();
        int top = tree.AddNode("r", -1, true, 0, 0, 0);
        for (int i = 0; i < count; i++)
        {
            int dir = tree.AddNode($"d{i}", top, true, 0, 0, 0, fileId: 100 + i);
            tree.AddNode("f.txt", dir, false, 1, 1, 0, fileId: 1000 + i);
            if (!onDisk) continue;
            Directory.CreateDirectory(Path.Combine(root, $"d{i}"));
            File.WriteAllText(Path.Combine(root, $"d{i}", "f.txt"), "x");
        }
        return tree;
    }

    private static bool SpotCheck(FileTree tree, string root, out string? why) =>
        IncrementalScan.SpotCheck(tree, root, rootFrn: 5, _ => true, _ => true, _ => [], out why);

    [Fact]
    public void SpotCheckPassesOnAMatchAndFailsOnAMiss()
    {
        var tree = Folders(_dir, 3);
        Assert.True(SpotCheck(tree, _dir, out var why), why);

        File.WriteAllText(Path.Combine(_dir, "d1", "stray.txt"), "the journal missed this");
        Assert.False(SpotCheck(tree, _dir, out why));
        Assert.StartsWith("spot check disagreed", why);
    }

    [Fact]
    public void SpotCheckFailsWhenNoFolderCouldBeListed()
    {
        // Every listing throws — this used to count as a pass.
        string missing = Path.Combine(_dir, "missing");
        Assert.False(SpotCheck(Folders(missing, 3, onDisk: false), missing, out var why));
        Assert.Equal("spot check verified 0 of 3 folders", why);
    }

    [Fact]
    public void SpotCheckToleratesAFewUnlistableFoldersInABigTree()
    {
        var tree = Folders(_dir, 20);
        Directory.Delete(Path.Combine(_dir, "d7"), recursive: true);
        Assert.True(SpotCheck(tree, _dir, out var why), why);
    }

    /// <summary>
    /// A prepared (fixups undone) FILE record: $STANDARD_INFORMATION, an
    /// optional $FILE_NAME under the root, and a non-resident unnamed $DATA.
    /// </summary>
    private static byte[] FileRecord(ushort flags = 1, long baseRecord = 0, string? name = "f.txt",
        long logical = 100, long allocated = 4096)
    {
        var rec = new byte[1024];
        "FILE"u8.CopyTo(rec);
        BitConverter.GetBytes((ushort)56).CopyTo(rec, 20);   // first attribute
        BitConverter.GetBytes(flags).CopyTo(rec, 22);        // 1 in use, 2 directory
        BitConverter.GetBytes(baseRecord).CopyTo(rec, 32);
        int pos = Resident(rec, 56, 0x10, new byte[48]);
        if (name is not null)
        {
            var value = new byte[66 + name.Length * 2];
            BitConverter.GetBytes(5L).CopyTo(value, 0);      // parent: the root record
            value[64] = (byte)name.Length;
            value[65] = 1;                                   // Win32 namespace
            Encoding.Unicode.GetBytes(name).CopyTo(value, 66);
            pos = Resident(rec, pos, 0x30, value);
        }
        BitConverter.GetBytes(0x80u).CopyTo(rec, pos);
        BitConverter.GetBytes(72u).CopyTo(rec, pos + 4);
        rec[pos + 8] = 1;                                    // non-resident, unnamed, VCN 0
        BitConverter.GetBytes((ushort)64).CopyTo(rec, pos + 32);
        BitConverter.GetBytes(allocated).CopyTo(rec, pos + 40);
        BitConverter.GetBytes(logical).CopyTo(rec, pos + 48);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(rec, pos + 72);
        return rec;
    }

    private static int Resident(byte[] rec, int pos, uint type, byte[] value)
    {
        int length = (24 + value.Length + 7) & ~7;
        BitConverter.GetBytes(type).CopyTo(rec, pos);
        BitConverter.GetBytes((uint)length).CopyTo(rec, pos + 4);
        BitConverter.GetBytes((uint)value.Length).CopyTo(rec, pos + 16);
        BitConverter.GetBytes((ushort)24).CopyTo(rec, pos + 20);
        value.CopyTo(rec, pos + 24);
        return pos + length;
    }

    /// <summary>A 1 GiB volume with 4 KiB clusters.</summary>
    private static readonly MftScanner.Geometry SmallVolume = new(1024, 4096, 1L << 30, 0);

    [Fact]
    public void OnlyAFreeOrReusedSlotReadsAsDeleted()
    {
        Assert.True(MftScanner.ParseEntry(FileRecord(), SmallVolume, out var info));
        Assert.Equal("f.txt", info.Name);
        Assert.Equal(5, info.ParentFrn);
        Assert.False(MftScanner.IsUnused(FileRecord()));

        Assert.False(MftScanner.ParseEntry(FileRecord(flags: 0), SmallVolume, out _));
        Assert.True(MftScanner.IsUnused(FileRecord(flags: 0)));            // free slot: gone
        Assert.True(MftScanner.IsUnused(FileRecord(baseRecord: 7)));       // now another file's extension
        // Live but nameless: not "deleted" — the rescan must fail instead.
        Assert.False(MftScanner.ParseEntry(FileRecord(name: null), SmallVolume, out _));
        Assert.False(MftScanner.IsUnused(FileRecord(name: null)));
    }

    // ---- 4. "Full rescan" never reuses the baseline ----

    [Fact]
    public void RemoveDropsTheBaselineEvenWhenTheTreeFileIsHeldOpen()
    {
        string root = Path.Combine(_dir, "scanned");
        var tree = new FileTree();
        tree.AddNode("a.txt", tree.AddNode("scanned", -1, true, 0, 0, 0), false, 10, 4096, 1, fileId: 42);
        ScanCache.Save(root, tree, new UsnJournal.Marker(1, 2));
        Assert.NotNull(ScanCache.Load(root));
        try
        {
            using (new FileStream(ScanCache.TreePath(root), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ScanCache.Remove(root);
                Assert.True(File.Exists(ScanCache.TreePath(root)), "fixture: a held file must refuse the delete");
                Assert.False(File.Exists(ScanCache.MarkerPath(root)), "the marker goes first");
                Assert.Null(ScanCache.Load(root));
            }
            Assert.Null(ScanCache.Load(root));
            // The next full scan's save makes it a baseline again.
            ScanCache.Save(root, tree, new UsnJournal.Marker(1, 3));
            Assert.Equal(3, ScanCache.Load(root)?.Marker.NextUsn);
        }
        finally { ScanCache.Remove(root); }
    }

    // ---- 5. Admin-only cache files ----

    [Fact]
    public void AdminOnlyCacheFilesGrantOnlySystemAndAdministrators()
    {
        // Creation only: moving an admin-only file needs the elevation the
        // app has and this test may not (Save does that move).
        string path = Path.Combine(_dir, "cache.bin");
        using var identity = WindowsIdentity.GetCurrent();
        using (var stream = ScanCache.CreateNew(path, adminOnly: true)) stream.Write([1, 2, 3]);
        try
        {
            var acl = new FileInfo(path).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToList();
            Assert.Equal(2, rules.Count);
            Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
            Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.LocalSystemSid));
            Assert.Contains(rules, r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllBytes(path));
        }
        finally
        {
            // This test created the file, so it owns it and may hand itself access to clean up.
            var mine = new FileSecurity();
            mine.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(mine);
            File.Delete(path);
        }

        string plain = Path.Combine(_dir, "plain.bin");
        using (var stream = ScanCache.CreateNew(plain, adminOnly: false)) stream.Write([7]);
        Assert.Equal([7], File.ReadAllBytes(plain));
        Assert.False(new FileInfo(plain).GetAccessControl().AreAccessRulesProtected);
    }

    // ---- 6. MFT reader on hostile or odd volumes ----

    /// <summary>NTFS volume data for <paramref name="path"/>'s volume, read through its root folder (works unelevated); null off NTFS.</summary>
    private static Win32.NTFS_VOLUME_DATA_BUFFER? VolumeData(string path)
    {
        using var root = Win32.CreateFileW(Path.GetPathRoot(Path.GetFullPath(path))!, Win32.FILE_READ_ATTRIBUTES,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        return !root.IsInvalid && MftScanner.GetVolumeData(root, out var data) ? data : null;
    }

    [Fact]
    public void VolumeGeometryCapsTheRecordTableAndRejectsCorruptShapes()
    {
        if (VolumeData(_dir) is { } real)
        {
            var geometry = MftScanner.GeometryOf(real);
            Assert.NotNull(geometry);
            Assert.Contains(geometry.Value.RecordSize, new[] { 1024, 4096 });
            Assert.InRange(geometry.Value.RecordCount, 25, geometry.Value.VolumeBytes / geometry.Value.RecordSize);
        }

        // A 4 MB volume claiming an 8 EB $MFT gets a 4,000-record table, not 9e15.
        var sane = new Win32.NTFS_VOLUME_DATA_BUFFER
        {
            BytesPerFileRecordSegment = 1024, BytesPerCluster = 4096, TotalClusters = 1000,
            MftValidDataLength = long.MaxValue,
        };
        Assert.Equal(4096L * 1000 / 1024, MftScanner.GeometryOf(sane)!.Value.RecordCount);
        var corrupt = new[]
        {
            sane with { BytesPerFileRecordSegment = 0 },
            sane with { BytesPerFileRecordSegment = 8192 },
            sane with { BytesPerFileRecordSegment = uint.MaxValue },
            sane with { BytesPerCluster = 256 },
            sane with { BytesPerCluster = 4 << 20 },
            sane with { TotalClusters = 0 },
            sane with { TotalClusters = -5 },
            sane with { TotalClusters = long.MaxValue },
        };
        foreach (var data in corrupt) Assert.Null(MftScanner.GeometryOf(data));
    }

    [Fact]
    public void RecordSizesAreHeldToWhatTheVolumeAllows()
    {
        MftScanner.ParseEntry(FileRecord(logical: -5, allocated: long.MaxValue), SmallVolume, out var hostile);
        Assert.Equal(0, hostile.Logical);
        Assert.Equal(1L << 30, hostile.Allocated);
        // A sparse file may be bigger than its volume — not bigger than NTFS's 2^32 clusters.
        MftScanner.ParseEntry(FileRecord(logical: 1L << 40, allocated: 4096), SmallVolume, out var sparse);
        Assert.Equal(1L << 40, sparse.Logical);
        MftScanner.ParseEntry(FileRecord(logical: long.MaxValue), SmallVolume, out var huge);
        Assert.Equal(4096L << 32, huge.Logical);
    }

    private void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        _links.Add(link);
        Assert.True(Directory.Exists(link), "fixture junction");
    }

    private static SafeFileHandle OpenNoFollow(string path) => Win32.CreateFileW(
        Win32.ExtendedPath(path), Win32.FILE_READ_ATTRIBUTES,
        Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero,
        Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

    [Fact]
    public void TheMftRefusesARootBehindALinkOrOnAnotherVolume()
    {
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        string link = Path.Combine(_dir, "link");
        Junction(link, target);

        Assert.False(MftScanner.CrossesLink(Path.GetPathRoot(_dir)!));
        Assert.False(MftScanner.CrossesLink(Path.Combine(target, "sub")));
        Assert.True(MftScanner.CrossesLink(link));
        Assert.True(MftScanner.CrossesLink(Path.Combine(link, "sub")));

        if (VolumeData(_dir) is not { } data) return;   // the record-number half needs NTFS
        uint serial = (uint)data.VolumeSerialNumber;
        Assert.True(MftScanner.TryGetFileRecordIndex(target, serial, out long frn));
        Assert.True(frn > 0);
        Assert.False(MftScanner.TryGetFileRecordIndex(target, serial ^ 1, out _));   // a volume we aren't reading
        Assert.False(MftScanner.TryGetFileRecordIndex(link, serial, out _));         // the junction itself
    }

    // ---- 7. Journal record sanity ----

    /// <summary>One USN_RECORD_V2 with its length padded to 8 bytes.</summary>
    private static byte[] UsnRecord(string name = "a.txt", ushort major = 2, long usn = 77)
    {
        int length = (60 + name.Length * 2 + 7) & ~7;
        var rec = new byte[length];
        BitConverter.GetBytes(length).CopyTo(rec, 0);
        BitConverter.GetBytes(major).CopyTo(rec, 4);
        BitConverter.GetBytes(0x0003_0000_0000_002AL).CopyTo(rec, 8);    // sequence 3, record 42
        BitConverter.GetBytes(5L).CopyTo(rec, 16);
        BitConverter.GetBytes(usn).CopyTo(rec, 24);
        BitConverter.GetBytes(0x100u).CopyTo(rec, 40);                   // FILE_CREATE
        BitConverter.GetBytes((ushort)(name.Length * 2)).CopyTo(rec, 56);
        BitConverter.GetBytes((ushort)60).CopyTo(rec, 58);
        Encoding.Unicode.GetBytes(name).CopyTo(rec, 60);
        return rec;
    }

    [Fact]
    public void JournalPagesParseWholeRecordsAndRejectMalformedOnes()
    {
        var entries = new List<UsnJournal.Entry>();
        byte[] page = [.. UsnRecord(), .. UsnRecord("b", usn: 78), 0, 0, 0, 0];   // a short tail is padding
        Assert.True(UsnJournal.ParsePage(page, entries));
        Assert.Equal(2, entries.Count);
        Assert.Equal(new UsnJournal.Entry(42, 5, "a.txt", 0x100, 77, 0), entries[0]);
        Assert.Equal("b", entries[1].Name);

        // Lengths 1–5 used to throw out of the slice; any length under the V2
        // header, past the page, or another version now fails the read.
        foreach (int length in new[] { 0, 1, 2, 3, 4, 5, 8, 59, 4096 })
        {
            var bad = UsnRecord();
            BitConverter.GetBytes(length).CopyTo(bad, 0);
            Assert.False(UsnJournal.ParsePage(bad, []), $"length {length}");
        }
        Assert.False(UsnJournal.ParsePage(UsnRecord(major: 3), []));
    }

    // ---- 8. Walk scanner ----

    private static List<string> Names(FileTree tree) => Enumerable.Range(0, tree.Count).Select(tree.NameOf).ToList();

    [Fact]
    public void TheWalkFollowsALinkChosenAsRootButNeverOneBelowIt()
    {
        string target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(Path.Combine(target, "sub"));
        File.WriteAllText(Path.Combine(target, "sub", "f.txt"), "x");
        string link = Path.Combine(_dir, "link");
        Junction(link, target);

        Assert.Contains("f.txt", Names(Win32Scanner.Walk(link, null, null)!.Tree));
        var whole = Names(Win32Scanner.Walk(_dir, null, null)!.Tree);
        Assert.DoesNotContain("link", whole);
        Assert.Single(whole, n => n == "f.txt");   // counted once, where it really lives

        // The mid-scan swap check: opened without following, a junction reads as a link.
        using (var handle = OpenNoFollow(link)) Assert.True(Win32Scanner.IsLink(handle));
        using (var handle = OpenNoFollow(target)) Assert.False(Win32Scanner.IsLink(handle));
    }

    private sealed class Reporter(Action<ScanEngine.ScanProgress> report) : IProgress<ScanEngine.ScanProgress>
    {
        public void Report(ScanEngine.ScanProgress value) => report(value);
    }

    [Fact]
    public void AThrowOnAWalkThreadNeverEndsTheProcess()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a", "b"));
        File.WriteAllText(Path.Combine(_dir, "a", "b", "f.txt"), "x");
        int calls = 0;
        // The first report runs on a worker thread; before the fix this
        // exception killed the test host.
        var progress = new Reporter(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("boom");
        });
        var result = Win32Scanner.Walk(_dir, progress, null);
        Assert.NotNull(result);
        Assert.True(calls >= 2, "the throwing report ran, then the final one");
        Assert.Contains("f.txt", Names(result.Tree));
    }

    // ---- 9. Exports ----

    [Fact]
    public void AFiveThousandLevelChainExportsInEveryFormat()
    {
        const int depth = 5000;
        var tree = new FileTree();
        int node = tree.AddNode("root", -1, true, 0, 0, 0);
        for (int i = 0; i < depth; i++) node = tree.AddNode("d", node, true, 0, 0, 0);
        tree.AddNode("leaf.txt", node, false, 7, 4096, 0);
        var totals = tree.RollUpSizes();
        var deep = new JsonDocumentOptions { MaxDepth = 4 * depth };

        using (var json = JsonDocument.Parse(TreeExporter.Export(tree, totals, @"C:\root", TreeExporter.Format.Json), deep))
        {
            var e = json.RootElement;
            for (int i = 0; i <= depth; i++) e = e.GetProperty("children")[0];
            Assert.Equal("leaf.txt", e.GetProperty("name").GetString());
            Assert.Equal(4096, json.RootElement.GetProperty("allocatedBytes").GetInt64());
        }
        using (var ncdu = JsonDocument.Parse(TreeExporter.Export(tree, totals, @"C:\root", TreeExporter.Format.Ncdu), deep))
        {
            var list = ncdu.RootElement[4];
            for (int i = 0; i < depth; i++)
            {
                Assert.Equal("d", list[0].GetProperty("name").GetString());
                list = list[1];
            }
            Assert.Equal("leaf.txt", list[0].GetProperty("name").GetString());
        }
        var ndjson = TreeExporter.Export(tree, totals, @"C:\root", TreeExporter.Format.Ndjson).TrimEnd().Split('\n');
        Assert.Equal(depth + 2, ndjson.Length);
        Assert.EndsWith(@"\d\leaf.txt", JsonDocument.Parse(ndjson[^1]).RootElement.GetProperty("path").GetString());
        var csv = TreeExporter.Export(tree, totals, @"C:\root", TreeExporter.Format.Csv).TrimEnd().Split('\n');
        Assert.Equal(depth + 3, csv.Length);
        Assert.Contains(@"\d\leaf.txt,leaf.txt,7,4096,", csv[^1]);
    }

    [Fact]
    public void CsvNeutralisesNamesThatASpreadsheetWouldRun()
    {
        var tree = new FileTree();
        int root = tree.AddNode("root", -1, true, 0, 0, 0);
        foreach (var name in new[] { "=cmd|' /C calc'!A0", "+1", "-2", "@SUM(1)", "\tx", "\rx", "plain.txt" })
            tree.AddNode(name, root, false, 1, 1, 0);
        var csv = TreeExporter.Export(tree, tree.RollUpSizes(), @"C:\root", TreeExporter.Format.Csv);
        Assert.Contains(",'=cmd|' /C calc'!A0,", csv);
        Assert.Contains(",'+1,", csv);
        Assert.Contains(",'-2,", csv);
        Assert.Contains(",'@SUM(1),", csv);
        Assert.Contains(",'\tx,", csv);
        Assert.Contains(",\"'\rx\",", csv);
        Assert.Contains(",plain.txt,", csv);
    }

    // ---- 10. DLL search hardening ----

    [Fact]
    public void NativeLibrariesLoadFromSystem32Only()
    {
        var attribute = typeof(FileTree).Assembly
            .GetCustomAttributes(typeof(DefaultDllImportSearchPathsAttribute), inherit: false)
            .Cast<DefaultDllImportSearchPathsAttribute>().Single();
        Assert.Equal(DllImportSearchPath.System32, attribute.Paths);
    }
}
