using System.Diagnostics;
using DiskMap.Core;
using DiskMap.Core.Native;

namespace DiskMap.Core.Tests;

/// <summary>
/// The delete path's safety audit: what may be deleted permanently and how,
/// the never-stage list, decoy paths, a commit's late additions, and a Put
/// Back record that may have been rewritten by anyone.
/// </summary>
public class DeletePathSafetyTests : IDisposable
{
    // Outside %TEMP%: under Temp everything classifies as a cache.
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "delete-safety", Guid.NewGuid().ToString("N")[..8]);

    public DeletePathSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Dir(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private string FileAt(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "fixture junction");
    }

    private sealed class SyncProgress(Action<CleanupQueue.CommitProgress> report) : IProgress<CleanupQueue.CommitProgress>
    {
        public void Report(CleanupQueue.CommitProgress value) => report(value);
    }

    // ---- 1. The permanent-delete gate ----

    [Fact]
    public void ReviewItemsAreNeverDeletedPermanently()
    {
        // Each sits in a class the gate used to accept, but is marked Review: maybe the only copy.
        var items = new[] { "scene.blend1", "part.sv$", "model.skb", "model.3dmbak", "sim.vcd" }
            .Select(name => (Path: FileAt(Path.Combine("design", name)), IsDirectory: false))
            .Append((Path: Dir(@"AppData\Local\Autodesk\Revit\2025\CollaborationCache"), IsDirectory: true));
        foreach (var (path, isDirectory) in items)
        {
            var verdict = StorageClassifier.Classify(path, isDirectory);
            Assert.Equal(CleanupAdvice.Review, verdict.Advice);
            Assert.Contains(verdict.Class, new[] { StorageClassifier.Caches, StorageClassifier.Dependencies });
            Assert.False(CleanupQueue.IsRegenerable(path), path);
        }
    }

    [Fact]
    public void NodeModulesIsStillRegenerable()
    {
        string nm = Dir(@"web\node_modules");
        FileAt(@"web\node_modules\left-pad\index.js");
        Assert.True(CleanupQueue.IsRegenerable(nm));
    }

    [Fact]
    public void AVenvCountsOnlyWithItsPyvenvCfg()
    {
        string venv = Dir(@"proj\venv");
        FileAt(@"proj\venv\Lib\site.py");
        string dotVenv = Dir(@"proj\.venv");
        Assert.False(CleanupQueue.IsRegenerable(venv));
        Assert.False(CleanupQueue.IsRegenerable(Path.Combine(venv, "Lib")));
        Assert.False(CleanupQueue.IsRegenerable(dotVenv));

        File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"), @"home = C:\Python312");
        File.WriteAllText(Path.Combine(dotVenv, "pyvenv.cfg"), @"home = C:\Python312");
        Assert.True(CleanupQueue.IsRegenerable(venv));
        Assert.True(CleanupQueue.IsRegenerable(Path.Combine(venv, "Lib")));
        Assert.True(CleanupQueue.IsRegenerable(dotVenv));
    }

    [Fact]
    public void AFolderReachedThroughALinkIsRefused()
    {
        string real = Dir(@"real\node_modules");
        FileAt(@"real\node_modules\dep\index.js");
        string link = Path.Combine(_root, "linked");
        Junction(link, Path.GetDirectoryName(real)!);
        Assert.True(CleanupQueue.IsRegenerable(real));
        Assert.False(CleanupQueue.IsRegenerable(Path.Combine(link, "node_modules")));
    }

    // ---- 2. A permanent delete never follows a link out ----

    [Fact]
    public void AJunctionRootIsRefusedAndOnlyItsLinkIsEverRemoved()
    {
        string precious = FileAt(@"outside\precious.txt");
        string nm = Path.Combine(Dir("proj"), "node_modules");
        Junction(nm, Path.GetDirectoryName(precious)!);

        Assert.False(CleanupQueue.IsRegenerable(nm));
        var queue = new CleanupQueue();
        Assert.True(queue.Stage(nm, 1, "developer storage"));
        var report = queue.DeletePermanently([queue.AllItems().Single().Id]);
        Assert.NotNull(report.Entries.Single().Error);
        Assert.True(File.Exists(precious));

        // Even handed the junction directly, the delete removes the link alone.
        CleanupQueue.DeleteTree(nm);
        Assert.False(Directory.Exists(nm));
        Assert.True(File.Exists(precious), "a junction root's target must survive");
    }

    [Fact]
    public void APinnedFolderCantBeMovedNorAnythingAboveIt()
    {
        string folder = Dir(@"a\b\pinned");
        using (CleanupQueue.PinFolder(folder))
        {
            Assert.ThrowsAny<Exception>(() => Directory.Move(folder, folder + "-moved"));
            Assert.ThrowsAny<Exception>(() => Directory.Move(Path.Combine(_root, "a"), Path.Combine(_root, "a-moved")));
            Assert.True(Directory.Exists(folder));
        }
        Directory.Move(folder, folder + "-moved");   // free again once let go

        string link = Path.Combine(_root, "link");
        Junction(link, Dir("target"));
        Assert.Throws<IOException>(() => CleanupQueue.PinFolder(link));   // a link is never listed
    }

    // ---- 3. The never-stage list ----

    [Theory]
    [InlineData(@"D:\$RECYCLE.BIN")]
    [InlineData(@"D:\$Recycle.Bin\S-1-5-21-1-2-3-1001\$RAB12CD.txt")]
    [InlineData(@"D:\System Volume Information")]
    [InlineData(@"E:\System Volume Information\tracking.log")]
    [InlineData(@"D:\Recovery")]
    [InlineData(@"D:\pagefile.sys")]
    [InlineData(@"D:\hiberfil.sys")]
    [InlineData(@"D:\swapfile.sys")]
    [InlineData(@"D:\DumpStack.log")]
    [InlineData(@"D:\DumpStack.log.tmp")]
    public void EveryDrivesSystemItemsAreNeverStaged(string path)
    {
        Assert.True(CleanupQueue.IsExcludedPath(path));
        Assert.False(new CleanupQueue().Stage(path, 1, "test"));
    }

    [Theory]
    [InlineData(@"D:\Data\Recovery")]   // only at a drive's root
    [InlineData(@"D:\$RECYCLE.BIN-notes")]
    [InlineData(@"D:\Projects\app\node_modules")]
    [InlineData(@"\\server\share\projects\node_modules")]
    public void LookalikesStayStageable(string path) => Assert.False(CleanupQueue.IsExcludedPath(path));

    [Fact]
    public void OtherSpellingsOfAProtectedPathAreRefused()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string drive = windows[..1], below = windows[3..];
        string[] spellings =
        [
            $@"\\?\{windows}\Temp",
            $@"\\.\{windows}\Temp",
            $@"\\?\UNC\localhost\{drive}$\{below}\Temp",
            $@"\\localhost\{drive}$\{below}\Temp",
            $@"\\some-pc\{drive.ToLowerInvariant()}$\{below}\System32",
            @"\\localhost\ADMIN$\Temp",
            @"\\?\Volume{00000000-0000-0000-0000-000000000000}\Users",
            @"\\?\GLOBALROOT\Device\HarddiskVolume3\Windows",
        ];
        foreach (var path in spellings) Assert.True(CleanupQueue.IsExcludedPath(path), path);
    }

    [Fact]
    public void KnownFoldersAreRefusedWhereverTheyLive()
    {
        var moved = new HashSet<string>([@"D:\Documents"], StringComparer.OrdinalIgnoreCase);
        Assert.True(CleanupQueue.IsExcludedPath(@"D:\Documents", moved));
        Assert.True(CleanupQueue.IsExcludedPath(@"D:\Documents\", moved));
        Assert.False(CleanupQueue.IsExcludedPath(@"D:\Documents\old-project", moved));   // what's inside still can be
        Assert.False(CleanupQueue.IsExcludedPath(@"D:\Documents", new HashSet<string>()));   // the rule, not the name

        string?[] real =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
            Win32.DownloadsFolder(),
        ];
        Assert.False(string.IsNullOrEmpty(real[^1]), "Downloads comes from SHGetKnownFolderPath");
        foreach (var folder in real.Where(f => !string.IsNullOrEmpty(f)))
        {
            Assert.True(CleanupQueue.IsExcludedPath(folder!), folder);
            Assert.False(new CleanupQueue().Stage(folder!, 1, "test"));
        }
        Assert.False(CleanupQueue.IsExcludedPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "dm-some-project")));
    }

    [Theory]
    [InlineData(@"C:\Users\me\.ssh")]
    [InlineData(@"C:\Users\me\.ssh\id_ed25519")]
    [InlineData(@"C:\Users\me\.gnupg")]
    [InlineData(@"C:\Users\me\.aws\credentials")]
    [InlineData(@"C:\Users\me\.azure")]
    [InlineData(@"D:\boot")]
    [InlineData(@"C:\$WinREAgent")]
    public void WhatTheClassifierNeverOffersIsNeverStaged(string path)
    {
        Assert.True(CleanupQueue.IsExcludedPath(path));
        Assert.False(new CleanupQueue().Stage(path, 1, "test"));
    }

    // ---- 4. Staging acts on exactly the item shown ----

    [Fact]
    public void DecoySpellingsAreNeverStaged()
    {
        string cache = Dir(@"shared\cache");
        var queue = new CleanupQueue();
        Assert.False(queue.Stage(cache + ".", 1, "decoy"));   // "cache." is "cache" to Windows
        Assert.False(queue.Stage(cache + " ", 1, "decoy"));
        Assert.False(queue.Stage(Path.Combine(_root, @"shared\x\..\cache"), 1, "decoy"));
        Assert.False(queue.Stage(@"\\?\" + cache, 1, "decoy"));
        Assert.False(queue.Stage(@"\\.\" + cache, 1, "decoy"));
        Assert.False(queue.Stage("//?/" + cache.Replace('\\', '/'), 1, "decoy"));
        Assert.Empty(queue.AllItems());

        Assert.True(queue.Stage(cache + @"\", 1, "real"));   // one trailing separator is the same item
        Assert.Equal(cache, queue.AllItems().Single().Path);
    }

    // ---- 5. A commit keeps what was staged while it ran ----

    [Fact]
    public async Task AnItemStagedDuringTheMoveStaysStaged()
    {
        // Paths that don't exist: no Recycle Bin is touched.
        string first = Path.Combine(_root, "gone-before-commit");
        string second = Path.Combine(_root, "staged-mid-move");
        var queue = new CleanupQueue();
        Assert.True(queue.Stage(first, 1, "test"));
        await queue.WaitForMeasurements();
        bool added = false;

        var report = queue.Commit(new SyncProgress(p =>
        {
            if (!added && p.Phase.StartsWith("Moving")) added = queue.Stage(second, 1, "added mid-move");
        }));

        Assert.True(added);
        Assert.Equal(first, report.Entries.Single().Item.Path);   // the commit only ever saw the first
        Assert.Contains(queue.AllItems(), i => i.Path == second);
    }

    // ---- 7. Put Back trusts nothing in its record ----

    [Fact]
    public void ARecordPointingOutsideTheBinMovesNothing()
    {
        // A hand-written record: an ordinary file dressed as a bin item, a "$I" beside it.
        string source = FileAt(@"bait\$RAB12CD.txt");
        string info = FileAt(@"bait\$IAB12CD.txt");
        string destination = Path.Combine(_root, "elsewhere", "moved.txt");

        var report = DiskMap.Core.PutBack.Run(new CleanupRecord(DateTimeOffset.Now, [new CleanupRecord.Item(destination, source, 1)]));

        Assert.Empty(report.Restored);
        Assert.Single(report.Skipped);
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(info), "a neighbouring $I file must survive");
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void AProtectedDestinationIsSkipped()
    {
        string source = FileAt(@"bait\$RXY34.txt");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var target in new[] { Path.Combine(windows, "dm-putback-test.txt"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            var report = DiskMap.Core.PutBack.Run(new CleanupRecord(DateTimeOffset.Now, [new CleanupRecord.Item(target, source, 1)]));
            Assert.Empty(report.Restored);
            Assert.Contains("protected", Assert.Single(report.Skipped).Reason, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void ABinCopyOfSomethingElseIsSkippedAndTheRealOneComesBackAsTheShellUser()
    {
        // The real bin (TEMP's drive); skips where the bin is unavailable.
        string dir = Path.Combine(Path.GetTempPath(), $"dm-putback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "real.txt");
        File.WriteAllText(file, "restore me");
        try
        {
            if (Shell32.RecycleItem(file) is not null) return;
            if (RecycleBinStore.Resolve(file) is not { } entry) return;

            string other = Path.Combine(dir, "other.txt");
            var forged = DiskMap.Core.PutBack.Run(new CleanupRecord(DateTimeOffset.Now, [new CleanupRecord.Item(other, entry.DataPath, 1)]));
            Assert.Empty(forged.Restored);
            Assert.False(File.Exists(other));
            Assert.True(File.Exists(entry.DataPath) && File.Exists(entry.InfoPath), "the $I mismatch left the bin alone");

            var honest = new CleanupRecord(DateTimeOffset.Now, [new CleanupRecord.Item(file, entry.DataPath, 1)]);
            if (UnelevatedLaunch.ShellProcessId() == 0)
            {
                DiskMap.Core.PutBack.Run(honest);   // no desktop shell to act as: just tidy the bin
                return;
            }
            var report = DiskMap.Core.PutBack.Run(honest, asShellUser: true);
            Assert.Single(report.Restored);
            Assert.Equal("restore me", File.ReadAllText(file));
            Assert.False(File.Exists(entry.InfoPath), "its $I record went with it");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            Directory.Delete(dir, true);
        }
    }

    // ---- 8. The restore privilege stays off ----

    [Fact]
    public void NothingEnablesTheRestorePrivilege()
    {
        // It lets backup-semantics opens write and delete past ACLs; every privileged caller only reads.
        string core = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DiskMap.Core"));
        var offenders = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("SeRestorePrivilege", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }
}
