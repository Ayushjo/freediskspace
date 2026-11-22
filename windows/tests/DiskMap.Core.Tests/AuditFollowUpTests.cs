using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskMap.Core;
using DiskMap.Core.Native;

namespace DiskMap.Core.Tests;

/// <summary>
/// Follow-ups found while merging the audit fixes: the elevated app's admin-only state folder, a
/// staged file that vanished, a commit that moved nothing, the full-rescan race, a scan of a folder
/// that isn't there, and the Yarn and NuGet homes.
/// </summary>
[Collection("ProcessEnvironment")]   // runs alone: one test commits and reads the shared Put Back record
public class AuditFollowUpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dm-followup-{Guid.NewGuid():N}");

    public AuditFollowUpTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "fixture junction");
    }

    // ---- The elevated state folder ----

    [Fact]
    public void OnlyAFolderSystemAndAdministratorsControlIsTrusted()
    {
        Assert.Null(AdminState.UntrustedSecurity(AdminState.ProtectedSecurity()));

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var readable = AdminState.ProtectedSecurity();
        readable.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.Null(AdminState.UntrustedSecurity(readable));

        // %ProgramData%'s own list lets Users create entries: a folder carrying it is not trusted.
        var creatable = AdminState.ProtectedSecurity();
        creatable.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.CreateFiles, AccessControlType.Allow));
        Assert.Contains("may change it", AdminState.UntrustedSecurity(creatable));

        var ownedByMe = AdminState.ProtectedSecurity();
        ownedByMe.SetOwner(WindowsIdentity.GetCurrent().User!);
        Assert.Equal("another account owns it", AdminState.UntrustedSecurity(ownedByMe));
    }

    [Fact]
    public void ALinkOrAFolderAPlainUserMadeIsNeverTheStateFolder()
    {
        string plain = Directory.CreateDirectory(Path.Combine(_dir, "plain")).FullName;
        Assert.NotNull(AdminState.Untrusted(plain));   // made by this (plain) account, under its own %TEMP% list

        string link = Path.Combine(_dir, "link");
        Junction(link, plain);
        Assert.Equal("it is a link to somewhere else", AdminState.Untrusted(link));
    }

    [Fact]
    public void TheOldStateIsCopiedInLinksAreSkippedAndTheOldScanCacheGoes()
    {
        string legacy = Directory.CreateDirectory(Path.Combine(_dir, "legacy")).FullName;
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"appearance\":\"dark\"}");
        File.WriteAllText(Path.Combine(legacy, "last-cleanup.json"), "{}");
        Directory.CreateDirectory(Path.Combine(legacy, "History"));
        File.WriteAllText(Path.Combine(legacy, "History", "h.json"), "[]");
        string elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "secret.snapshot"), "not ours");
        Junction(Path.Combine(legacy, "snapshots"), elsewhere);   // a folder swapped for a link is never followed
        string cache = Directory.CreateDirectory(Path.Combine(legacy, "ScanCache")).FullName;
        File.WriteAllText(Path.Combine(cache, "abc.tree"), "old index");
        string target = Directory.CreateDirectory(Path.Combine(_dir, "target")).FullName;

        AdminState.ImportLegacy(legacy, target);

        Assert.Equal("{\"appearance\":\"dark\"}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "last-cleanup.json")));
        Assert.True(File.Exists(Path.Combine(target, "History", "h.json")));
        Assert.False(File.Exists(Path.Combine(target, "snapshots", "secret.snapshot")));
        Assert.False(Directory.Exists(Path.Combine(target, "ScanCache")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")), "nothing in the old folder is moved");
        Assert.True(File.Exists(Path.Combine(elsewhere, "secret.snapshot")));
        if (UnelevatedLaunch.ShellProcessId() != 0)   // deleted as the shell's user; no shell, nothing to check
            Assert.False(File.Exists(Path.Combine(cache, "abc.tree")));
    }

    // ---- The delete path ----

    [Fact]
    public void AVanishedItemSaysSoAndACommitThatMovedNothingKeepsTheLastPutBack()
    {
        var previous = new CleanupRecord(DateTimeOffset.Now.AddDays(-1),
            [new CleanupRecord.Item(@"C:\x\old.bin", @"C:\$Recycle.Bin\S-1\$Rold.bin", 1)]);
        previous.Save();
        var queue = new CleanupQueue();
        Assert.True(queue.Stage(Path.Combine(_dir, "gone.bin"), 1, "test"));
        queue.WaitForMeasurements().Wait();

        var report = queue.Commit();

        var entry = Assert.Single(report.Entries);
        Assert.IsType<FileNotFoundException>(entry.Error);
        Assert.Contains("no longer there", entry.Error!.Message);
        Assert.Equal(previous.Date, CleanupRecord.Load()!.Date);
        Assert.Single(queue.AllItems());   // stays with its reason; the person takes it out
    }

    [Fact]
    public void RecyclingAPathThatVanishedNeverThrowsOrStopsTheBatch()
    {
        var refused = Shell32.RecycleItems([Path.Combine(_dir, "missing-1"), Path.Combine(_dir, "missing-2")]);
        Assert.Empty(refused);
    }

    // ---- Scanning ----

    [Fact]
    public async Task ScanningAFolderThatIsntThereFails() =>
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new ScanEngine().ScanAsync(Path.Combine(_dir, "missing")));

    [Fact]
    public void AScanThatBeganBeforeAFullRescanIsNeverSavedAsTheBaseline()
    {
        string root = Path.Combine(_dir, "scanned");
        var tree = new FileTree();
        tree.AddNode("a.txt", tree.AddNode("scanned", -1, true, 0, 0, 0), false, 10, 4096, 1, fileId: 42);
        var startedBefore = DateTime.UtcNow.AddMinutes(-1);
        ScanCache.Remove(root);

        ScanCache.SaveInBackground(root, tree, new UsnJournal.Marker(1, 2), startedBefore);
        ScanCache.WaitForPendingSaves();
        Assert.Null(ScanCache.Load(root));
        Assert.False(File.Exists(ScanCache.TreePath(root)));

        ScanCache.SaveInBackground(root, tree, new UsnJournal.Marker(1, 3), DateTime.UtcNow);
        ScanCache.WaitForPendingSaves();
        Assert.NotNull(ScanCache.Load(root));   // a scan that began after it saves as usual
        ScanCache.Remove(root);
    }

    // ---- Yarn and NuGet ----

    [Theory]
    [InlineData(@"C:\Users\me\web\.yarn", ".yarn", false)]
    [InlineData(@"C:\Users\me\web\.yarn\cache", "cache", false)]       // zero-install repositories commit it
    [InlineData(@"C:\Users\me\.yarn\berry\cache", "cache", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Yarn\Cache", "Cache", true)]
    public void OnlyYarnsOwnCachesAreSafe(string path, string name, bool safe) =>
        Assert.Equal(safe, SafetyClassifier.Assess(path, name, isDirectory: true).Level == SafetyLevel.Safe);

    [Fact]
    public void TheNuGetAndYarnHomesOfferOnlyTheirCaches()
    {
        var tree = new FileTree();
        int me = tree.AddNode("me", -1, true, 0, 0, 0);
        int Dir(int parent, string name) => tree.AddNode(name, parent, true, 0, 0, 0);
        void Fill(int parent) => tree.AddNode("payload.bin", parent, false, 20_000_000, 20_000_000, 10);
        int nuget = Dir(me, ".nuget");
        Fill(Dir(nuget, "packages"));
        Fill(Dir(nuget, "plugins"));                  // credential providers
        int yarn = Dir(me, ".yarn");
        Fill(Dir(Dir(yarn, "berry"), "cache"));
        Fill(Dir(yarn, "bin"));

        var items = DeveloperCatalog.Build(tree, @"C:\Users\me", tree.RollUpSizes(SizeBasis.Allocated)).Items;

        Assert.Equal([@"C:\Users\me\.nuget\packages", @"C:\Users\me\.yarn\berry\cache"],
            items.Select(i => i.AbsolutePath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(items, i => Assert.Equal(DeveloperReclaimability.Reclaimable, i.Reclaimability));
        Assert.Equal(["NuGet packages", "Yarn cache"], items.Select(i => i.DisplayName).Order(StringComparer.Ordinal));
    }
}
