using System.Text.Json;
using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// "What Windows keeps" (WIN-116, P2; Mac: SystemStorageCatalogTests): system-storage-windows.json
/// loads completely, only trash entries can reach the basket, measuring never fails on a missing
/// or unreadable path, and Recall's store is refused by the queue (decision D2).
/// Tests that need a real Windows (the excluded list is built from Windows' own folders, and the
/// walk uses the scan engine) say so and only assert there; the rest run anywhere.
/// </summary>
public class SystemStorageWindowsTests
{
    private static readonly SystemStorageCatalog.Entry Template = new(
        "x", "X", [@"C:\x"], SystemStorageCatalog.Action.Guide, "why", ["step"], false);

    private static byte[] BundledJson()
    {
        var assembly = typeof(SystemStorageCatalog).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(SystemStorageCatalog.SystemResource, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void BundledFileLoadsCompletely()
    {
        var entries = SystemStorageCatalog.SystemEntries;
        using var json = JsonDocument.Parse(BundledJson());
        Assert.Equal(json.RootElement.GetProperty("entries").GetArrayLength(), entries.Count);
        Assert.True(entries.Count >= 7);
        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Why), e.Id);
            Assert.NotEmpty(e.Steps);
        });
        // Every Settings link survived the load (a dropped one would mean a typo in the file).
        int links = json.RootElement.GetProperty("entries").EnumerateArray().Count(e => e.TryGetProperty("settingsUri", out _));
        Assert.Equal(links, entries.Count(e => e.SettingsUri is not null));
        Assert.All(entries.Where(e => e.SettingsUri is not null),
            e => Assert.StartsWith("ms-settings:", e.SettingsUri!, StringComparison.Ordinal));
        // The research plan's rows are all there.
        Assert.Equal(
            ["component-store", "installer-cache", "recall-snapshots", "update-downloads", "upgrade-files", "windows-logs", "windows-old"],
            entries.Select(e => e.Id).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void EveryGuideEntryHasStepsAndASettingsPage()
    {
        var guides = SystemStorageCatalog.SystemEntries.Where(e => e.Action == SystemStorageCatalog.Action.Guide).ToList();
        Assert.NotEmpty(guides);
        Assert.All(guides, e =>
        {
            Assert.NotEmpty(e.Steps);
            Assert.All(e.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s)));
            Assert.NotNull(e.SettingsUri);
        });
    }

    /// <summary>
    /// Nothing Windows keeps goes to the basket: upgrade files and Windows.old are Windows' own
    /// (Temporary files), Recall only through its settings, WinSxS and the Installer cache never.
    /// WinSxS is never walked: Explorer-style counting over-counts its hard links.
    /// </summary>
    [Fact]
    public void BundledEntriesAreGuidanceOnly()
    {
        var entries = SystemStorageCatalog.SystemEntries.ToDictionary(e => e.Id);
        Assert.DoesNotContain(entries.Values, e => e.Action == SystemStorageCatalog.Action.Trash);
        Assert.Equal(SystemStorageCatalog.Action.Never, entries["component-store"].Action);
        Assert.False(entries["component-store"].Measured);
        Assert.Equal(SystemStorageCatalog.Action.Never, entries["installer-cache"].Action);
        Assert.Equal(SystemStorageCatalog.Action.Guide, entries["recall-snapshots"].Action);
        Assert.Equal("ms-settings:privacy", entries["recall-snapshots"].SettingsUri);
        Assert.Equal(@"%LOCALAPPDATA%\CoreAIPlatform.00", Assert.Single(entries["recall-snapshots"].Paths));
        Assert.All(entries.Values.Where(e => e.Id != "component-store"), e => Assert.True(e.Measured, e.Id));
    }

    [Fact]
    public void MalformedOrUnknownValuesAreDropped()
    {
        Assert.Empty(SystemStorageCatalog.LoadEntries("nope"u8.ToArray(), SystemStorageCatalog.SystemResource));
        Assert.Empty(SystemStorageCatalog.LoadEntries("""{ "entries": 3 }"""u8.ToArray()));
        var entries = SystemStorageCatalog.LoadEntries("""
            { "entries": [
              { "id": "a", "title": "A", "paths": ["x"], "action": "delete", "why": "w", "steps": ["s"] },
              { "id": "b", "title": "B", "paths": [], "action": "guide", "why": "w", "steps": ["s"] },
              { "id": "", "title": "C", "paths": ["x"], "action": "guide", "why": "w", "steps": ["s"] },
              { "id": "d", "title": "D", "paths": ["x"], "action": "guide", "why": "w", "steps": ["s"],
                "settingsUri": "https://example.com", "somethingNew": 1 },
              { "id": "e", "title": "E", "paths": ["x"], "action": "never", "why": "w", "steps": ["s"],
                "settingsUri": "ms-settings:storagesense", "measure": false },
              { "id": "f", "title": "F", "paths": ["x"], "action": "guide", "why": "w", "steps": ["s"],
                "settingsUri": "ms-settings:x & calc.exe" }
            ] }
            """u8.ToArray());
        Assert.Equal(["d", "e", "f"], entries.Select(e => e.Id).ToArray());
        Assert.Null(entries[0].SettingsUri);                      // not a Settings page: dropped
        Assert.True(entries[0].Measured);                         // the default
        Assert.Equal("ms-settings:storagesense", entries[1].SettingsUri);
        Assert.False(entries[1].Measured);
        Assert.Null(entries[2].SettingsUri);                      // not a plain page id
    }

    [Theory]
    [InlineData("ms-settings:storagesense", true)]
    [InlineData("ms-settings:privacy", true)]
    [InlineData("MS-SETTINGS:privacy-webcam", true)]
    [InlineData("ms-settings:", false)]
    [InlineData("https://ms-settings.example", false)]
    [InlineData("ms-settings:a b", false)]
    [InlineData("ms-settings:a/../../x", false)]
    [InlineData(null, false)]
    public void OnlySettingsPagesCanBeOpened(string? uri, bool allowed) =>
        Assert.Equal(allowed, SystemStorageCatalog.IsSettingsUri(uri));

    // ---- Only trash entries can be staged ----

    [Fact]
    public void OnlyTrashEntriesCanBeStaged()
    {
        string outside = Path.Combine(Path.GetTempPath(), "DiskMap-system-storage", "Installer.iso");
        var found = new SystemStorageCatalog.Found[] { new(outside, -1, 4_000_000_000) };
        foreach (var action in new[] { SystemStorageCatalog.Action.Never, SystemStorageCatalog.Action.Guide })
        {
            var item = new SystemStorageCatalog.Item(Template with { Action = action }, found, false, new HashSet<string>());
            Assert.Empty(SystemStorageCatalog.StageTargets(item));
        }
        // Recall's store never qualifies, even if a data file marked it trash.
        string recall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreAIPlatform.00");
        var trash = new SystemStorageCatalog.Item(Template with { Action = SystemStorageCatalog.Action.Trash },
            [new(recall, -1, 1), .. found], false, new HashSet<string>());
        var targets = SystemStorageCatalog.StageTargets(trash);
        Assert.DoesNotContain(targets, t => t.Path == recall);
        // Windows only: off Windows the excluded list (built from Windows' own folders) refuses every path.
        if (OperatingSystem.IsWindows()) Assert.Equal(outside, Assert.Single(targets).Path);
    }

    [Fact]
    public void BundledNeverAndGuidePathsUnderWindowsAreExcludedFromTheQueue()
    {
        // Windows only: %SystemRoot% and %LOCALAPPDATA% expand to real folders there. Windows.old
        // and the upgrade folders aren't on the excluded list; the card never offers them.
        if (!OperatingSystem.IsWindows()) return;
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var entry in SystemStorageCatalog.SystemEntries.Where(e => e.Action != SystemStorageCatalog.Action.Trash))
        {
            foreach (var pattern in entry.Paths)
            {
                string path = UserFolders.Expand(pattern);
                bool underWindows = path.StartsWith(windows + @"\", StringComparison.OrdinalIgnoreCase);
                if (underWindows || entry.Id == "recall-snapshots")
                    Assert.True(CleanupQueue.IsExcludedPath(path), $"{entry.Id}: {path}");
            }
        }
    }

    // ---- Decision D2: Recall's store ----

    [Fact]
    public void RecallStoreIsRefusedByTheQueue()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string store = Path.Combine(local, "CoreAIPlatform.00");
        var queue = new CleanupQueue();
        Assert.False(queue.Stage(store, 1, "test"));
        Assert.False(queue.Stage(Path.Combine(store, "UKP", "{0A1B}", "ImageStore"), 1, "test"));
        Assert.False(queue.Stage(Path.Combine(store, "UKP", "{0A1B}", "ukg.db"), 1, "test"));
        Assert.True(CleanupQueue.IsExcludedPath(store));
        Assert.Empty(queue.AllItems());
        // Windows only: a neighbour that merely starts with the same letters is still stageable.
        if (OperatingSystem.IsWindows())
            Assert.False(CleanupQueue.IsExcludedPath(Path.Combine(local, "CoreAIPlatform.00-notes")));
    }

    // ---- Measuring ----

    private static SystemStorageCatalog.Entry Entry(string id, string[] paths, bool measured = true) =>
        Template with { Id = id, Title = id, Paths = paths, Measured = measured };

    /// <summary>
    /// Missing paths are skipped, an entry with nothing is left out, one unreadable path marks the
    /// item partial, a path matched by two patterns is counted once, and a sizer that throws is
    /// an unreadable path, never an exception.
    /// </summary>
    [Fact]
    public void MeasuresWithoutDoubleCountingAndToleratesMissingOrUnreadable()
    {
        var sizes = new Dictionary<string, SystemStorageCatalog.Measurement>
        {
            [@"C:\a\one"] = new(100, true),
            [@"C:\a\two"] = new(50, false),
            [@"C:\c\big"] = new(900, true),
        };
        SystemStorageCatalog.Measurement? Sizer(string path) =>
            path == @"C:\d\locked" ? throw new UnauthorizedAccessException()
            : sizes.TryGetValue(path, out var m) ? m : null;
        var entries = new[]
        {
            Entry("a", [@"C:\a\one", @"C:\a\one", @"C:\a\two", @"C:\a\missing"]),
            Entry("b", [@"C:\b\missing"]),
            Entry("c", [@"C:\c\big"]),
            Entry("d", [@"C:\d\locked"]),
        };
        var items = SystemStorageCatalog.Measure(entries, null, null, null, expand: p => p, sizer: Sizer);
        Assert.Equal(["c", "a", "d"], items.Select(i => i.Id).ToArray());
        Assert.False(items[0].IsPartial);
        Assert.Equal(150, items[1].Bytes);
        Assert.Equal(2, items[1].FoundPaths.Count);
        Assert.True(items[1].IsPartial);
        var locked = items[2];
        Assert.True(locked.IsPartial);                // "Can't read", not 0 B
        Assert.False(locked.SizeUnknown);
        Assert.Equal(0, locked.Bytes);
    }

    /// <summary>
    /// C:\ (the scan root): Windows\Logs (3 MB, CBS denied), Windows\Installer (2 MB).
    /// Windows.old isn't in the scan, so it is walked on its own.
    /// </summary>
    [Fact]
    public void TheScanIsUsedWhereItCoversThePath()
    {
        var tree = new FileTree();
        int root = tree.AddNode(@"C:\", -1, true, 0, 0, 0);
        int windows = tree.AddNode("Windows", root, true, 0, 0, 0);
        int logs = tree.AddNode("Logs", windows, true, 0, 0, 0);
        tree.AddNode("dism.log", logs, false, 3_000_000, 3_000_000, 1);
        int cbs = tree.AddNode("CBS", logs, true, 0, 0, 0);
        int installer = tree.AddNode("Installer", windows, true, 0, 0, 0);
        tree.AddNode("1a2b.msi", installer, false, 2_000_000, 2_000_000, 1);
        var totals = tree.RollUpSizes(SizeBasis.Allocated);
        var asked = new List<string>();
        SystemStorageCatalog.Measurement? Sizer(string path)
        {
            asked.Add(path);
            return path == @"C:\Windows.old" ? new(7_000_000, true) : null;
        }
        var entries = new[]
        {
            Entry("logs", [@"C:\Windows\Logs"]),
            Entry("installer", [@"C:\Windows\Installer"]),
            Entry("old", [@"C:\Windows.old"]),
            Entry("gone", [@"C:\Windows\Nothing"]),
        };
        var items = SystemStorageCatalog.Measure(entries, tree, @"C:\", totals, deniedDirectoryIds: [cbs], expand: p => p, sizer: Sizer);
        Assert.Equal(["old", "logs", "installer"], items.Select(i => i.Id).ToArray());
        var logsItem = items[1];
        Assert.Equal(3_000_000, logsItem.Bytes);
        Assert.Equal(logs, Assert.Single(logsItem.FoundPaths).NodeId);
        Assert.True(logsItem.IsPartial);              // CBS couldn't be listed: at least this much
        Assert.False(items[2].IsPartial);
        Assert.Equal(-1, Assert.Single(items[0].FoundPaths).NodeId);
        // Only what the scan doesn't hold is walked.
        Assert.Equal([@"C:\Windows.old", @"C:\Windows\Nothing"], asked.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void AnUnmeasuredEntryIsOnlyLookedUp()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"DiskMap-winsxs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            int calls = 0;
            SystemStorageCatalog.Measurement? Sizer(string _) { calls++; return new(1, true); }
            var items = SystemStorageCatalog.Measure(
                [Entry("store", [dir], measured: false), Entry("absent", [dir + "-missing"], measured: false)],
                null, null, null, expand: p => p, sizer: Sizer);
            var item = Assert.Single(items);
            Assert.Equal("store", item.Id);
            Assert.True(item.SizeUnknown);
            Assert.Equal(0, item.Bytes);
            Assert.Equal(0, calls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ATrailingStarMatchesByPrefixOnDisk()
    {
        var listing = new[] { "$WINDOWS.~BT", "$Windows.~WS", "Windows", "$Recycle.Bin" };
        var found = SystemStorageCatalog.ExpandLive(@"C:\$WINDOWS.~*", p => p == @"C:" ? listing : null);
        Assert.Equal([@"C:\$WINDOWS.~BT", @"C:\$Windows.~WS"], found.ToArray());
        Assert.Empty(SystemStorageCatalog.ExpandLive(@"C:\nowhere\a*", _ => null));
        Assert.Equal([@"C:\Windows.old"], SystemStorageCatalog.ExpandLive(@"C:\Windows.old", _ => null).ToArray());
    }

    [Fact]
    public void TheWalkToleratesMissingAndOddPaths()
    {
        Assert.Null(SystemStorageCatalog.WalkSize(Path.Combine(Path.GetTempPath(), $"DiskMap-missing-{Guid.NewGuid():N}"), TimeSpan.FromSeconds(5)));
        Assert.Null(SystemStorageCatalog.WalkSize("", TimeSpan.FromSeconds(5)));
        Assert.Null(SystemStorageCatalog.WalkSize("a\0b", TimeSpan.FromSeconds(5)));
        // The bundled entries on whatever this machine is: never throws.
        var items = SystemStorageCatalog.Measure(SystemStorageCatalog.SystemEntries, null, null, null,
            sizer: p => SystemStorageCatalog.WalkSize(p, TimeSpan.FromSeconds(5)));
        Assert.All(items, i => Assert.True(i.Bytes >= 0));
    }

    [Fact]
    public void TheWalkSizesAFolderLikeAScan()
    {
        // Windows only: the walk is the scan engine's own directory query.
        if (!OperatingSystem.IsWindows()) return;
        string dir = Path.Combine(Path.GetTempPath(), $"DiskMap-walk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllBytes(Path.Combine(dir, "sub", "a.bin"), new byte[10_000]);
        try
        {
            var size = SystemStorageCatalog.WalkSize(dir, TimeSpan.FromSeconds(30));
            Assert.NotNull(size);
            Assert.True(size.Value.Measured);
            Assert.True(size.Value.Complete);
            Assert.True(size.Value.Bytes >= 10_000);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
