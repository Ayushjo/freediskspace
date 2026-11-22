using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// WIN-013: staging a folder measures from the scan tree when the journal
/// proves the subtree unchanged. The journal read is seam-replaced
/// (JournalChangesForTest) — the volume ioctl needs admin; the seeded
/// math itself runs unelevated.
/// </summary>
public class SeededStagingTests : IDisposable
{
    private readonly string _dir;

    public SeededStagingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "diskmap-seed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllBytes(Path.Combine(_dir, "sub", "a.bin"), new byte[8192]);
        File.WriteAllBytes(Path.Combine(_dir, "sub", "b.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(_dir, "other.bin"), new byte[2048]);
    }

    private FileTree Scan() =>
        Win32Scanner.Walk(_dir, null, null, CancellationToken.None)!.Tree;

    private int NodeOf(FileTree tree, string name) =>
        Enumerable.Range(0, tree.Count).First(i => tree.NameOf(i) == name);

    [Fact]
    public void UnchangedSubtreeMeasuresFromTheTree()
    {
        var tree = Scan();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, _dir, new UsnJournal.Marker(1, 1), []);
        queue.JournalChangesForTest = _ => [];   // journal says: nothing changed

        var profile = queue.TrySeededProfile(Path.Combine(_dir, "sub"));
        Assert.NotNull(profile);
        Assert.True(profile!.IsComplete);
        // The tree's numbers, not a guess: the two files' real allocation.
        Assert.Equal(8192 + 4096, profile.AllocatedBytes);
        Assert.Equal(8192 + 4096, profile.OwnedBytes);
        Assert.Equal(2, profile.FileCount);
    }

    [Fact]
    public void BatchMeasurementsReadTheJournalOnce()
    {
        var tree = Scan();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, _dir, new UsnJournal.Marker(1, 1), []);
        int reads = 0;
        queue.JournalChangesForTest = _ => { reads++; return []; };

        Assert.NotNull(queue.TrySeededProfile(Path.Combine(_dir, "sub")));
        Assert.NotNull(queue.TrySeededProfile(Path.Combine(_dir, "other.bin")));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task StagingWaveSharesOneJournalRead()
    {
        var tree = Scan();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, _dir, new UsnJournal.Marker(1, 1), []);
        int reads = 0;
        using var start = new ManualResetEventSlim(false);
        queue.JournalChangesForTest = _ =>
        {
            start.Wait();
            Interlocked.Increment(ref reads);
            return [];
        };

        Assert.True(queue.Stage(Path.Combine(_dir, "sub"), 0, "test"));
        Assert.True(queue.Stage(Path.Combine(_dir, "other.bin"), 0, "test"));
        start.Set();
        await queue.WaitForMeasurements();

        Assert.Equal(1, reads);
        Assert.All(queue.AllItems(), item => Assert.False(item.IsMeasuring));
    }

    [Fact]
    public void ChangedSubtreeFallsBackToTheWalk()
    {
        var tree = Scan();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, _dir, new UsnJournal.Marker(1, 1), []);
        // Journal reports the dir's own frn changed — the seeded path
        // must refuse.
        long subFrn = tree.FileId[NodeOf(tree, "sub")];
        queue.JournalChangesForTest = _ => [Change(subFrn)];

        Assert.Null(queue.TrySeededProfile(Path.Combine(_dir, "sub")));
    }

    private static UsnJournal.Entry Change(long frn, long parent = 0) => new(frn, parent, "", 0, 0, 0);

    /// <summary>
    /// A tree whose ids carry an NTFS sequence number above the 48-bit record
    /// number, as the walk scanner's do (0x000500000069971D seen live).
    /// </summary>
    private static (FileTree Tree, string Root) SequencedTree()
    {
        var tree = new FileTree();
        int root = tree.AddNode("seed", -1, true, 0, 0, 0, fileId: 0x0001_0000_0000_0005);
        int sub = tree.AddNode("sub", root, true, 0, 0, 0, fileId: 0x0003_0000_0000_0123);
        tree.AddNode("a.bin", sub, false, 8192, 8192, 0, fileId: 0x0005_0000_0069_971D);
        tree.AddNode("other.bin", root, false, 2048, 2048, 0, fileId: 0x0002_0000_0000_0777);
        return (tree, @"C:\seed-fixture");
    }

    [Fact]
    public void AChangedFileIsSeenThoughTheTreeIdCarriesASequenceNumber()
    {
        var (tree, root) = SequencedTree();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, root, new UsnJournal.Marker(1, 1), []);
        // The journal names a.bin by its record number alone, as UsnJournal.ReadAll does.
        queue.JournalChangesForTest = _ => [Change(0x69971D, parent: 0x123)];

        Assert.Null(queue.TrySeededProfile(Path.Combine(root, "sub", "a.bin")));
    }

    [Fact]
    public void AFileCreatedInsideTheFolderIsSeenThroughItsParent()
    {
        var (tree, root) = SequencedTree();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, root, new UsnJournal.Marker(1, 1), []);
        // A new file: an id the scan never saw, whose parent is sub.
        queue.JournalChangesForTest = _ => [Change(0x9999, parent: 0x123)];
        Assert.Null(queue.TrySeededProfile(Path.Combine(root, "sub")));

        // The same new file beside sub, not inside it, leaves sub seeded.
        var beside = new CleanupQueue();
        beside.SetScanContext(tree, root, new UsnJournal.Marker(1, 1), []);
        beside.JournalChangesForTest = _ => [Change(0x9999, parent: 0x5)];
        Assert.NotNull(beside.TrySeededProfile(Path.Combine(root, "sub")));
    }

    [Fact]
    public void NoJournalAnswerIsHonestFallback()
    {
        var tree = Scan();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, _dir, new UsnJournal.Marker(1, 1), []);
        queue.JournalChangesForTest = _ => null;   // journal unreadable
        Assert.Null(queue.TrySeededProfile(Path.Combine(_dir, "sub")));
    }

    [Fact]
    public void OutsideTheScanRootNeverSeeds()
    {
        var tree = Scan();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, _dir, new UsnJournal.Marker(1, 1), []);
        queue.JournalChangesForTest = _ => [];
        Assert.Null(queue.TrySeededProfile(Path.GetTempPath()));   // parent of the scan root
    }

    [Fact]
    public async Task VerifyingReportsProgressOnlyWhenItMoves()
    {
        var tree = Scan();
        var queue = new CleanupQueue();
        queue.SetScanContext(tree, _dir, new UsnJournal.Marker(1, 1), []);
        using var release = new ManualResetEventSlim(false);
        queue.JournalChangesForTest = _ => { release.Wait(); return []; };   // holds the measurement
        Assert.True(queue.Stage(Path.Combine(_dir, "sub"), 0, "test"));
        int verifying = 0;
        var progress = new SyncProgress(p => { if (p.Phase.StartsWith("Verif")) Interlocked.Increment(ref verifying); });

        var commit = Task.Run(() => queue.Commit(progress));
        await Task.Delay(1000);   // the loop polls every 150 ms meanwhile
        release.Set();
        await commit;

        // Each report rebuilds the basket on the UI thread: one per change, not one per poll.
        Assert.InRange(verifying, 0, 1);
    }

    private sealed class SyncProgress(Action<CleanupQueue.CommitProgress> report) : IProgress<CleanupQueue.CommitProgress>
    {
        public void Report(CleanupQueue.CommitProgress value) => report(value);
    }

    [Fact]
    public async Task NoContextStillStagesViaTheWalk()
    {
        var queue = new CleanupQueue();
        Assert.True(queue.Stage(Path.Combine(_dir, "sub"), 0, "test"));
        await queue.WaitForMeasurements();
        var item = queue.AllItems().Single();
        Assert.False(item.IsMeasuring);
        Assert.NotNull(item.Sharing);
        Assert.Equal(8192 + 4096, item.Sharing!.AllocatedBytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
