using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// WIN-108: what the Developer page needs beyond the catalog — "Where it lives" (folder
/// rollup and where it opens), the tool strip, the page's words, the tool-storage tab
/// (developer-storage.json) and the By tool sections. Paths are compared without caring
/// about the separator, so the suite runs on macOS too.
/// </summary>
public class DeveloperBoardTests
{
    private static string Slash(string path) => path.Replace('\\', '/');

    /// <summary>
    /// scan/
    /// ├── Projects/
    /// │   ├── web/        package.json + package-lock.json, node_modules 60 MB, dist 20 MB
    /// │   └── tool/       Cargo.toml, target 30 MB
    /// └── .npm/           10 MB
    /// </summary>
    private static (FileTree Tree, long[] Totals, DeveloperCatalogResult Result) Board()
    {
        var tree = new FileTree();
        int root = tree.AddNode("scan", -1, true, 0, 0, 0);
        int projects = tree.AddNode("Projects", root, true, 0, 0, 0);
        int web = tree.AddNode("web", projects, true, 0, 0, 0);
        tree.AddNode("package.json", web, false, 400, 400, 10);
        tree.AddNode("package-lock.json", web, false, 1_000, 1_000, 10);
        int nm = tree.AddNode("node_modules", web, true, 0, 0, 0);
        tree.AddNode("dep.js", nm, false, 60_000_000, 60_000_000, 10);
        int dist = tree.AddNode("dist", web, true, 0, 0, 0);
        tree.AddNode("bundle.js", dist, false, 20_000_000, 20_000_000, 10);
        int tool = tree.AddNode("tool", projects, true, 0, 0, 0);
        tree.AddNode("Cargo.toml", tool, false, 200, 200, 10);
        int target = tree.AddNode("target", tool, true, 0, 0, 0);
        tree.AddNode("a.rlib", target, false, 30_000_000, 30_000_000, 10);
        int npm = tree.AddNode(".npm", root, true, 0, 0, 0);
        tree.AddNode("blob", npm, false, 10_000_000, 10_000_000, 10);
        var totals = tree.RollUpSizes(SizeBasis.Allocated);
        return (tree, totals, DeveloperCatalog.Build(tree, @"C:\scan", totals, today: 100));
    }

    [Fact]
    public void ItemsCarryAnIdADisplayPathAndASafetyVerdict()
    {
        var (_, _, result) = Board();
        Assert.Equal(4, result.Items.Count);
        foreach (var item in result.Items)
        {
            Assert.Equal($"dev-{item.NodeID}", item.Id);
            Assert.False(string.IsNullOrEmpty(item.DisplayPath));
            Assert.False(string.IsNullOrEmpty(item.Safety.Reason));
        }
        var npm = result.Items.Single(i => i.DisplayName == "npm cache");
        Assert.Equal(SafetyLevel.Safe, npm.Safety.Level);
        // The excluded-paths list resolves Windows folders, so this half only holds there.
        if (OperatingSystem.IsWindows()) Assert.False(npm.IsProtected);
        var web = result.Projects.Single(p => p.Name == "web");
        Assert.Equal(web.Key, web.Id);
    }

    [Fact]
    public void FolderRollupCreditsEveryFolderUpToTheRoot()
    {
        var (tree, _, result) = Board();
        var folders = DeveloperCatalog.FolderRollup(result, tree,
            i => i.Reclaimability != DeveloperReclaimability.Keep && !i.IsProtected);

        var root = folders.Folders[0];
        Assert.Equal(120_000_000, root.Bytes);
        Assert.Equal(4, root.ItemCount);
        Assert.Null(root.ItemId);
        // Projects (110 MB) before .npm (10 MB).
        Assert.Equal(2, root.Children.Count);
        Assert.Equal("Projects", tree.NameOf(root.Children[0]));
        Assert.Equal(".npm", tree.NameOf(root.Children[1]));

        var projects = folders.Folders[root.Children[0]];
        Assert.Equal(110_000_000, projects.Bytes);
        Assert.Equal(["web", "tool"], projects.Children.Select(tree.NameOf).ToArray());

        var web = folders.Folders[projects.Children[0]];
        Assert.Equal(80_000_000, web.Bytes);
        Assert.Equal(2, web.ItemIds.Count);
        Assert.Equal(web.RemovableBytes, web.RemovableItemIds
            .Sum(id => result.Items.Single(i => i.Id == id).Bytes));

        // An item's own folder knows it is that item.
        var npmItem = result.Items.Single(i => i.DisplayName == "npm cache");
        Assert.Equal(npmItem.Id, folders.Folders[npmItem.NodeID].ItemId);
    }

    [Fact]
    public void FolderRollupLeavesOutWhatCannotGo()
    {
        var (tree, _, result) = Board();
        var folders = DeveloperCatalog.FolderRollup(result, tree, i => i.DisplayName == "target");
        Assert.Equal(30_000_000, folders.Folders[0].RemovableBytes);
        Assert.Single(folders.Folders[0].RemovableItemIds);
        Assert.Equal(120_000_000, folders.Folders[0].Bytes);
    }

    [Fact]
    public void AutoStartStepsInWhileOneFolderHoldsMostOfIt()
    {
        var (tree, _, result) = Board();
        var folders = DeveloperCatalog.FolderRollup(result, tree, _ => true);
        // Projects holds 110 of 120 MB (≥ 80%): open there. web holds 80 of 110 (< 80%): stop.
        int start = folders.AutoStart();
        Assert.Equal("Projects", tree.NameOf(start));
        Assert.Equal(0, DeveloperFolders.Empty.AutoStart());
    }

    [Fact]
    public void AutoStartNeverOpensAnItem()
    {
        var tree = new FileTree();
        int root = tree.AddNode("scan", -1, true, 0, 0, 0);
        int npm = tree.AddNode(".npm", root, true, 0, 0, 0);
        tree.AddNode("blob", npm, false, 10_000_000, 10_000_000, 10);
        var totals = tree.RollUpSizes(SizeBasis.Allocated);
        var result = DeveloperCatalog.Build(tree, @"C:\scan", totals);
        var folders = DeveloperCatalog.FolderRollup(result, tree, _ => true);
        Assert.Equal(0, folders.AutoStart());
    }

    private static DeveloperItem Item(DeveloperEcosystem eco, long bytes,
        DeveloperReclaimability reclaim = DeveloperReclaimability.Reclaimable,
        RebuildCost cost = RebuildCost.Cheap, string? lockfile = null, CleanupRecipe? recipe = null, int node = 1) =>
        new(node, "item", @"C:\scan\item", bytes, DeveloperCategory.Caches, eco, false, reclaim, "", null, null,
            0, true, cost, lockfile, null, null, recipe);

    [Fact]
    public void TilesAreBiggestFirstAndFoldPastTheLimit()
    {
        var items = new[]
        {
            Item(DeveloperEcosystem.Node, 50),
            Item(DeveloperEcosystem.Node, 30, DeveloperReclaimability.ReviewFirst),
            Item(DeveloperEcosystem.Rust, 60),
            Item(DeveloperEcosystem.Python, 10),
            Item(DeveloperEcosystem.Docker, 5, DeveloperReclaimability.Keep),
            Item(DeveloperEcosystem.Dotnet, 0),
        };
        var all = DeveloperBoard.Tiles(items, 9);
        Assert.Equal(["Node.js", "Rust", "Python", "Docker"], all.Select(t => t.Title).ToArray());
        Assert.Equal(80, all[0].Bytes);
        Assert.Equal(50, all[0].RebuildBytes);   // only Reclaimable counts as rebuilding
        Assert.Equal(0, all[3].RebuildBytes);

        var folded = DeveloperBoard.Tiles(items, 3);
        Assert.Equal(3, folded.Count);
        Assert.Equal("2 more", folded[2].Title);
        Assert.Null(folded[2].Ecosystem);
        Assert.Equal("more", folded[2].Id);
        Assert.Equal(15, folded[2].Bytes);
        Assert.Equal(10, folded[2].RebuildBytes);
    }

    [Fact]
    public void ReinstallLineIsSafeOnlyWhenEverythingIsPinned()
    {
        var caches = DeveloperBoard.ReinstallLine([Item(DeveloperEcosystem.Rust, 1)]);
        Assert.True(caches.Ok);
        Assert.Equal("Reinstall is safe.", caches.Title);
        Assert.Equal("These are build output and caches; the next build makes them again.", caches.Detail);

        var pinned = DeveloperBoard.ReinstallLine(
        [
            Item(DeveloperEcosystem.Node, 1, cost: RebuildCost.Networked, lockfile: "package-lock.json"),
            Item(DeveloperEcosystem.Python, 1, cost: RebuildCost.Networked, lockfile: "poetry.lock"),
        ]);
        Assert.Equal("package-lock.json and poetry.lock are here, so the same versions come back.", pinned.Detail);

        var unpinned = DeveloperBoard.ReinstallLine(
        [
            Item(DeveloperEcosystem.Node, 1, cost: RebuildCost.NetworkedUnpinned),
            Item(DeveloperEcosystem.Rust, 1),
        ]);
        Assert.False(unpinned.Ok);
        Assert.Equal("Check before removing.", unpinned.Title);
        Assert.Equal("1 folder has no lockfile, so a reinstall may pull newer versions.", unpinned.Detail);
    }

    [Fact]
    public void ItemNoteSteersToTheToolWhenTheRecycleBinIsWrong()
    {
        var recipe = new CleanupRecipe
        {
            Id = "docker", Title = "Docker", Command = "`docker system prune -a`", Why = "", TrashIsUnsafe = true,
        };
        Assert.Equal("Use docker system prune -a instead.", DeveloperBoard.ItemNote(Item(DeveloperEcosystem.Docker, 1, recipe: recipe)));
        Assert.Equal("Node.js · Re-download", DeveloperBoard.ItemNote(Item(DeveloperEcosystem.Node, 1, cost: RebuildCost.Networked)));
        Assert.Equal("Rust · Offline build", DeveloperBoard.ItemNote(Item(DeveloperEcosystem.Rust, 1)));
    }

    [Fact]
    public void WordsMatchTheMac()
    {
        Assert.Equal("4.2", DeveloperBoard.ChipShort(4_200_000_000));
        Assert.Equal("350M", DeveloperBoard.ChipShort(350_400_000));
        Assert.Equal("12K", DeveloperBoard.ChipShort(12_000));
        Assert.Equal("1K", DeveloperBoard.ChipShort(20));

        Assert.Equal("Unknown", DeveloperBoard.LastChange(0, 100));
        Assert.Equal("Today", DeveloperBoard.LastChange(100, 100));
        Assert.Equal("Yesterday", DeveloperBoard.LastChange(99, 100));
        Assert.Equal("2d ago", DeveloperBoard.LastChange(98, 100));
        Assert.Equal("3 months ago", DeveloperBoard.AgeString(95));
        Assert.Equal("59d ago", DeveloperBoard.AgeString(59));
        Assert.Equal("2 months ago", DeveloperBoard.AgeString(60));
        Assert.Equal("1 year ago", DeveloperBoard.AgeString(400));
        Assert.Equal("2 years ago", DeveloperBoard.AgeString(800));
        Assert.Equal("Very old", DeveloperBoard.AgeString(16 * 365));

        Assert.Equal("Pushed", DeveloperBoard.GitShort(new GitState.InSync()));
        Assert.Equal("No remote", DeveloperBoard.GitShort(new GitState.NoRemote()));
        Assert.Equal("Unpushed", DeveloperBoard.GitShort(new GitState.Differs(["main"])));
        Assert.Equal("No git", DeveloperBoard.GitShort(new GitState.NotARepository()));
        Assert.Equal("Unknown", DeveloperBoard.GitShort(new GitState.Unknown("x")));

        Assert.Equal("Free", DeveloperBoard.RebuildShort(RebuildCost.Free));
        Assert.Equal("No lockfile", DeveloperBoard.RebuildShort(RebuildCost.NetworkedUnpinned));
        Assert.Equal("1 place", DeveloperBoard.CountLabel(1, "place"));
        Assert.Equal("3 places", DeveloperBoard.CountLabel(3, "place"));

        Assert.True(DeveloperBoard.IsStale(10, 200));
        Assert.False(DeveloperBoard.IsStale(20, 200));
        Assert.False(DeveloperBoard.IsStale(0, 900));
        Assert.Equal("Untouched for over six months", DeveloperBoard.FolderNote(stale: true).Title);
        Assert.Equal("You worked on this recently", DeveloperBoard.FolderNote(stale: false).Title);
    }

    // ---- The tool-storage tab (developer-storage.json) ----

    [Fact]
    public void BundledDeveloperStorageEntriesLoad()
    {
        var entries = SystemStorageCatalog.DeveloperEntries;
        Assert.True(entries.Count >= 6, $"expected the full developer-storage.json, got {entries.Count}");
        Assert.Contains(entries, e => e.Id == "wsl-distros" && e.Action == SystemStorageCatalog.Action.Guide && e.Command is not null);
        Assert.Contains(entries, e => e.Id == "nuget-http-cache" && e.Action == SystemStorageCatalog.Action.Trash);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Why)));
        // Only the Recycle Bin path is ever "trash": WSL, Docker and emulators are never offered.
        Assert.All(entries.Where(e => e.Id is "wsl-distros" or "docker-disk" or "android-emulators"),
            e => Assert.NotEqual(SystemStorageCatalog.Action.Trash, e.Action));
    }

    [Fact]
    public void MalformedOrUnknownEntriesAreDropped()
    {
        Assert.Empty(SystemStorageCatalog.LoadEntries("not json"u8.ToArray()));
        var entries = SystemStorageCatalog.LoadEntries("""
            { "entries": [
              { "id": "a", "title": "A", "paths": ["x"], "action": "trash", "why": "w", "steps": [] },
              { "id": "b", "title": "B", "paths": ["x"], "action": "explode", "why": "w", "steps": [] },
              { "id": "c", "title": "C", "paths": [], "action": "guide", "why": "w", "steps": [] }
            ] }
            """u8.ToArray());
        Assert.Equal("a", Assert.Single(entries).Id);
    }

    /// <summary>
    /// C:\Users\me (the scan root)
    /// ├── AppData\Local\Android\Sdk\system-images\android-33 (3 MB), android-35 (4 MB), .temp (1 MB)
    /// ├── AppData\Local\NuGet\v3-cache (2 MB)
    /// └── AppData\Local\Packages\CanonicalGroupLimited.Ubuntu_x\LocalState (5 MB)
    /// </summary>
    private static (FileTree Tree, long[] Totals) ToolTree()
    {
        var tree = new FileTree();
        int root = tree.AddNode("me", -1, true, 0, 0, 0);
        int local = tree.AddNode("Local", tree.AddNode("AppData", root, true, 0, 0, 0), true, 0, 0, 0);
        int images = tree.AddNode("system-images",
            tree.AddNode("Sdk", tree.AddNode("Android", local, true, 0, 0, 0), true, 0, 0, 0), true, 0, 0, 0);
        tree.AddNode("system.img", tree.AddNode("android-33", images, true, 0, 0, 0), false, 3_000_000, 3_000_000, 1);
        tree.AddNode("system.img", tree.AddNode("android-35", images, true, 0, 0, 0), false, 4_000_000, 4_000_000, 1);
        tree.AddNode("partial", tree.AddNode(".temp", images, true, 0, 0, 0), false, 1_000_000, 1_000_000, 1);
        int nuget = tree.AddNode("v3-cache", tree.AddNode("NuGet", local, true, 0, 0, 0), true, 0, 0, 0);
        tree.AddNode("pkg.bin", nuget, false, 2_000_000, 2_000_000, 1);
        int ubuntu = tree.AddNode("CanonicalGroupLimited.Ubuntu_x",
            tree.AddNode("Packages", local, true, 0, 0, 0), true, 0, 0, 0);
        tree.AddNode("ext4.vhdx", tree.AddNode("LocalState", ubuntu, true, 0, 0, 0), false, 5_000_000, 5_000_000, 1);
        return (tree, tree.RollUpSizes(SizeBasis.Allocated));
    }

    private static string Expand(string pattern) => pattern
        .Replace("%LOCALAPPDATA%", @"C:\Users\me\AppData\Local")
        .Replace("%USERPROFILE%", @"C:\Users\me")
        .Replace("%ProgramFiles%", @"C:\Program Files");

    [Fact]
    public void ToolStorageIsMeasuredFromTheScan()
    {
        var (tree, totals) = ToolTree();
        var items = SystemStorageCatalog.MeasureTree(SystemStorageCatalog.DeveloperEntries, tree, @"C:\Users\me", totals, Expand);

        // Biggest first; entries with nothing in the scan (Docker, emulators, .NET SDKs) left out.
        Assert.Equal(["android-system-images", "wsl-distros", "nuget-http-cache"], items.Select(i => i.Id).ToArray());

        var images = items[0];
        Assert.Equal(2, images.FoundPaths.Count);          // one row per version; ".temp" skipped
        Assert.Equal(7_000_000, images.Bytes);
        var keep = Assert.Single(images.Keep);
        Assert.EndsWith("android-35", Slash(keep));        // the newest API level stays
        Assert.Equal(3_000_000, images.SuggestedBytes);

        var wsl = items[1];
        var distro = Assert.Single(wsl.FoundPaths);         // matched by prefix
        Assert.EndsWith("Packages/CanonicalGroupLimited.Ubuntu_x", Slash(distro.Path));
        Assert.Equal(5_000_000, distro.Bytes);

        var nuget = items[2];
        Assert.Equal(SystemStorageCatalog.Action.Trash, nuget.Entry.Action);
        Assert.Equal(2_000_000, nuget.SuggestedBytes);
        Assert.Equal("v3-cache", tree.NameOf(Assert.Single(nuget.FoundPaths).NodeId));
    }

    [Fact]
    public void PathsOutsideTheScanAreLeftOut()
    {
        var (tree, totals) = ToolTree();
        var items = SystemStorageCatalog.MeasureTree(SystemStorageCatalog.DeveloperEntries, tree, @"D:\Elsewhere", totals, Expand);
        Assert.Empty(items);
    }

    [Fact]
    public void VersionsReadFromFolderNames()
    {
        Assert.Equal([8, 0, 401], SystemStorageCatalog.Version("8.0.401")!);
        Assert.Equal([35], SystemStorageCatalog.Version("android-35")!);
        Assert.Equal([17, 4], SystemStorageCatalog.Version("iPhone15,2 17.4 (21E219)")!);
        Assert.Null(SystemStorageCatalog.Version("google_apis"));
        Assert.EndsWith("9.0.100", SystemStorageCatalog.NewestVersion([@"sdk\8.0.401", @"sdk\9.0.100", @"sdk\notes"]));
        Assert.Null(SystemStorageCatalog.NewestVersion([@"x\alpha"]));
    }

    // ---- By tool ----

    [Fact]
    public void ByToolSectionsFollowTheFileOrderWithNotes()
    {
        var patterns = QuickWins.BundledPatterns();
        Assert.NotNull(patterns.CategoryNotes);
        Assert.All(patterns.CategoryNotes!, n => Assert.False(string.IsNullOrWhiteSpace(n.Note)));
        // Every category a pattern names has a note.
        Assert.All(patterns.Categories!.Values.Distinct(),
            c => Assert.Contains(patterns.CategoryNotes!, n => n.Title == c));

        var hits = new List<QuickWins.Hit>
        {
            new(1, "Temp", "System caches"),
            new(2, "node_modules", "Development dependencies"),
            new(3, "odd", QuickWins.DefaultCategory),
        };
        var sections = QuickWins.Sections(patterns, hits);
        Assert.Equal(["Development dependencies", "System caches", QuickWins.DefaultCategory],
            sections.Select(s => s.Title).ToArray());
        Assert.Equal("", sections[2].Note);
    }
}
