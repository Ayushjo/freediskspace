using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The rules that keep cleanup away from things that are not regenerable:
/// evidence for generic folder names, installed tools, git-tracked source,
/// protected user folders, duplicates' owners. Each case here is a folder a
/// real laptop lost or nearly lost (a Flutter SDK's bin/packages, MATLAB's
/// ServiceHost\bin, an agent's node_modules in AppData, all of AppData).
/// </summary>
public class SafetyRulesTests
{
    private static (FileTree Tree, int Root) Home()
    {
        var tree = new FileTree();
        return (tree, tree.AddNode("me", -1, true, 0, 0, 0));
    }

    private static int Dir(FileTree t, int parent, string name) => t.AddNode(name, parent, true, 0, 0, 0);
    private static void File(FileTree t, int parent, string name, long size = 1_000) => t.AddNode(name, parent, false, size, size, 10);

    /// <summary>A folder with one 20 MB file inside, so it has bytes to report.</summary>
    private static int Filled(FileTree t, int parent, string name)
    {
        int d = Dir(t, parent, name);
        File(t, d, "payload.bin", 20_000_000);
        return d;
    }

    private static List<DeveloperItem> Catalog(FileTree tree, string root = @"C:\Users\me") =>
        DeveloperCatalog.Build(tree, root, tree.RollUpSizes(SizeBasis.Allocated)).Items;

    [Fact]
    public void FlutterSdkBinAndPackagesAreNeverDeveloperOutput()
    {
        var (t, root) = Home();
        int docs = Dir(t, root, "Documents");
        int flutter = Dir(t, docs, "flutter");
        File(t, flutter, "pubspec.yaml");
        Dir(t, flutter, ".git");
        Filled(t, flutter, "bin");
        Filled(t, flutter, "packages");

        var items = Catalog(t);
        Assert.DoesNotContain(items, i => i.DisplayName is "bin" or "packages");
    }

    [Fact]
    public void BinCountsOnlyNextToADotnetProject()
    {
        var (t, root) = Home();
        int code = Dir(t, root, "code");
        int tool = Dir(t, code, "scripts");
        Filled(t, tool, "bin");
        int app = Dir(t, code, "app");
        File(t, app, "app.csproj");
        int appBin = Filled(t, app, "bin");

        var items = Catalog(t);
        var bin = Assert.Single(items, i => i.DisplayName == "bin");
        Assert.Equal(appBin, bin.NodeID);
        Assert.Equal(DeveloperEcosystem.Dotnet, bin.Ecosystem);
    }

    [Fact]
    public void PackagesNeedsASolutionBesideIt()
    {
        var (t, root) = Home();
        int mono = Dir(t, root, "monorepo");
        File(t, mono, "package.json");
        Filled(t, mono, "packages");
        int sln = Dir(t, root, "legacy");
        File(t, sln, "Legacy.sln");
        Filled(t, sln, "packages");

        var items = Catalog(t);
        var packages = Assert.Single(items, i => i.DisplayName == "packages");
        Assert.Contains(@"\legacy\", packages.AbsolutePath);
    }

    [Fact]
    public void TrackedBuildFoldersAreSourceNotOutput()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dm-tracked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        try
        {
            var tree = new FileTree();
            int repo = tree.AddNode("repo", -1, true, 0, 0, 0);
            Dir(tree, repo, ".git");
            tree.AddNode(".gitignore", repo, false, 20, 20, 1);
            File(tree, repo, "package.json");
            Filled(tree, repo, "dist");     // a GitHub Action commits dist/
            Filled(tree, repo, "build");    // ignored below
            System.IO.File.WriteAllText(Path.Combine(dir, ".gitignore"), "build/\n");

            var items = DeveloperCatalog.Build(tree, dir, tree.RollUpSizes(SizeBasis.Allocated)).Items;
            Assert.Contains(items, i => i.DisplayName == "build");
            Assert.DoesNotContain(items, i => i.DisplayName == "dist");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FoldersInsideInstalledToolsAreNotProjects()
    {
        var (t, root) = Home();
        int appData = Dir(t, root, "AppData");
        int local = Dir(t, appData, "Local");
        int agent = Dir(t, Dir(t, local, "hermes"), "hermes-agent");
        File(t, agent, "package.json");
        Filled(t, agent, "node_modules");
        Filled(t, agent, "dist");
        int host = Dir(t, Dir(t, Dir(t, local, "MathWorks"), "ServiceHost"), "v2026.4.0.6");
        File(t, host, "Service.csproj");   // even with evidence, a tool's own folder
        Filled(t, host, "bin");
        int dot = Dir(t, Dir(t, root, ".lmstudio"), "extensions");
        File(t, dot, "package.json");
        Filled(t, dot, "node_modules");
        int temp = Dir(t, Dir(t, local, "Temp"), "scratch");
        Filled(t, temp, "node_modules");    // scratch projects in Temp still count
        int web = Dir(t, Dir(t, Dir(t, root, "Documents"), "codes"), "web");
        File(t, web, "package.json");
        Filled(t, web, "node_modules");

        var items = Catalog(t);
        Assert.Equal(2, items.Count(i => i.DisplayName == "node_modules"));
        Assert.DoesNotContain(items, i => i.AbsolutePath.Contains(@"\hermes\") || i.AbsolutePath.Contains(@"\MathWorks\")
            || i.AbsolutePath.Contains(@"\.lmstudio\"));
    }

    [Fact]
    public void HardwareProjectsAreRecognisedWithTheirEvidence()
    {
        var (t, root) = Home();
        int hw = Dir(t, root, "hw");
        int viv = Dir(t, hw, "blinky");
        File(t, viv, "blinky.xpr");
        Filled(t, viv, "blinky.runs");
        Filled(t, viv, "blinky.cache");
        int q = Dir(t, hw, "uart");
        File(t, q, "uart.qpf");
        Filled(t, q, "db");
        int sim = Dir(t, hw, "tb");
        int work = Filled(t, sim, "work");
        File(t, work, "_info");
        int pcb = Dir(t, hw, "board");
        File(t, pcb, "board.kicad_pro");
        Filled(t, pcb, "board-backups");
        int fw = Dir(t, hw, "fw");
        File(t, fw, "platformio.ini");
        Filled(t, fw, ".pio");
        Filled(t, Dir(t, root, "notes"), "work");   // a plain folder called work

        var items = Catalog(t);
        Assert.Contains(items, i => i.DisplayName == "blinky.runs" && i.Ecosystem == DeveloperEcosystem.Fpga);
        Assert.Contains(items, i => i.DisplayName == "blinky.cache" && i.Reclaimability == DeveloperReclaimability.Reclaimable);
        Assert.Contains(items, i => i.DisplayName == "db" && i.Ecosystem == DeveloperEcosystem.Fpga);
        Assert.Contains(items, i => i.DisplayName == "board-backups" && i.Ecosystem == DeveloperEcosystem.Pcb);
        Assert.Contains(items, i => i.DisplayName == ".pio" && i.Ecosystem == DeveloperEcosystem.Embedded);
        var work1 = Assert.Single(items, i => i.DisplayName == "work");
        Assert.Contains(@"\tb\", work1.AbsolutePath);
    }

    [Fact]
    public void VirtualenvsNeedPyvenvCfgAndIdeStateIsKept()
    {
        var (t, root) = Home();
        int proj = Dir(t, root, "proj");
        int venv = Filled(t, proj, "venv");
        File(t, venv, "pyvenv.cfg");
        Filled(t, proj, "env");       // just a folder named env
        Filled(t, root, ".claude");
        int web = Dir(t, root, "web");
        File(t, web, "package.json");
        Filled(t, web, ".yarn");     // a Berry project's own yarn release
        Filled(t, root, ".yarn");    // the global cache

        var items = Catalog(t);
        Assert.Contains(items, i => i.DisplayName == "venv");
        Assert.DoesNotContain(items, i => i.DisplayName == "env");
        Assert.Equal(DeveloperReclaimability.Keep, Assert.Single(items, i => i.DisplayName == ".claude").Reclaimability);
        var yarn = Assert.Single(items, i => i.DisplayName == ".yarn");
        Assert.DoesNotContain(@"\web\", yarn.AbsolutePath);
    }

    [Fact]
    public void QuickWinsNeedTheSameEvidence()
    {
        var tree = new FileTree();
        int root = tree.AddNode("me", -1, true, 0, 0, 0);
        int app = Dir(tree, root, "app");
        File(tree, app, "package.json");
        int dist = Dir(tree, app, "dist");
        Dir(tree, Dir(tree, root, "photos"), "build");   // an album called build

        var hits = QuickWins.Find(tree, @"C:\Users\me", new QuickWins.Patterns(["dist", "build"], []));
        Assert.Equal([dist], hits.Select(h => h.Id).ToList());
    }

    [Theory]
    [InlineData(@"C:\Users\me", true)]
    [InlineData(@"C:\Users\me\AppData", true)]
    [InlineData(@"C:\Users\me\AppData\Local", true)]
    [InlineData(@"C:\Users\me\Documents", true)]
    [InlineData(@"C:\Users\me\Downloads", true)]
    [InlineData(@"C:\Users\me\OneDrive - Contoso\Documents", true)]
    [InlineData(@"C:\Program Files", true)]
    [InlineData(@"C:\Users\me\Documents\codes", false)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp", false)]
    [InlineData(@"C:\Users\me\Downloads\old", false)]
    public void UserFoldersThemselvesAreNeverStaged(string path, bool never)
    {
        Assert.Equal(never, StorageClassifier.Classify(path, isDirectory: true).Advice == CleanupAdvice.Never);
        Assert.Equal(never, CleanupQueue.IsExcludedPath(path));
        Assert.Equal(!never, new CleanupQueue().Stage(path, 1, "test"));
    }

    [Fact]
    public void DriveRootIsNeverStaged()
    {
        Assert.True(CleanupQueue.IsExcludedPath(@"D:\"));
        Assert.False(new CleanupQueue().Stage(@"D:\", 1, "test"));
    }

    [Fact]
    public void TheWorkingDrivesRootIsNeverStaged()
    {
        // A root trimmed to "C:" reads as the working folder on C: — the
        // exclusion once depended on where the process happened to start.
        string root = Path.GetPathRoot(Environment.CurrentDirectory)!;
        string bare = root.TrimEnd('\\');
        Assert.True(CleanupQueue.IsExcludedPath(root));
        Assert.True(CleanupQueue.IsExcludedPath(bare));
        Assert.False(new CleanupQueue().Stage(root, 1, "test"));
        Assert.False(new CleanupQueue().Stage(bare, 1, "test"));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\hermes\agent\node_modules", "appdata", CleanupAdvice.Warn)]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\x\node_modules", "caches", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\Documents\codes\web\node_modules", "deps", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\.ssh", "appdata", CleanupAdvice.Never)]
    // Under Program Files (x86) the installed-app claim wins and the queue's
    // excluded prefixes refuse it anyway; a library on another drive is a cache.
    [InlineData(@"D:\SteamLibrary\steamapps\shadercache", "caches", CleanupAdvice.Fine)]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\shadercache", "apps", CleanupAdvice.Warn)]
    [InlineData(@"C:\Users\me\AppData\Local\NVIDIA\DXCache", "caches", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\AppData\Local\MathWorks\ServiceHost\v1\bin", "apps", CleanupAdvice.Warn)]
    [InlineData(@"C:\Xilinx\Vivado\2024.1", "devtools", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\Documents\flutter", "devtools", CleanupAdvice.Review)]
    public void ClassifiesToolsCachesAndSecrets(string path, string classId, CleanupAdvice advice)
    {
        var v = StorageClassifier.Classify(path, isDirectory: true);
        Assert.Equal(classId, v.Class.Id);
        Assert.Equal(advice, v.Advice);
    }

    [Fact]
    public void SteamGamesStayAWarningAndWaveformsAreBuildOutput()
    {
        Assert.Equal(CleanupAdvice.Warn,
            StorageClassifier.Classify(@"D:\SteamLibrary\steamapps\common\Game\game.exe", false).Advice);
        var vcd = StorageClassifier.Classify(@"C:\Users\me\hw\tb\dump.vcd", false);
        Assert.Equal("deps", vcd.Class.Id);
        Assert.Equal(CleanupAdvice.Review, vcd.Advice);
    }

    [Fact]
    public void RollupCountsAToolsDependenciesAsAppData()
    {
        var tree = new FileTree();
        int root = tree.AddNode("me", -1, true, 0, 0, 0);
        int nm = Dir(tree, Dir(tree, Dir(tree, Dir(tree, root, "AppData"), "Local"), "agent"), "node_modules");
        File(tree, nm, "x.js", 9);
        var rollup = StorageClassifier.Rollup(tree, @"C:\Users\me", tree.RollUpSizes(SizeBasis.Logical));
        Assert.Equal(9, rollup.Single(r => r.Class.Id == "appdata").Bytes);
        Assert.DoesNotContain(rollup, r => r.Class.Id == "deps");
    }

    [Fact]
    public void DuplicatesNeverComeFromDependenciesAppsOrRepositories()
    {
        var tree = new FileTree();
        int root = tree.AddNode("me", -1, true, 0, 0, 0);
        int downloads = Dir(tree, root, "Downloads");
        tree.AddNode("a.zip", downloads, false, 50, 50, 1);
        int keep = tree.AddNode("b.zip", downloads, false, 50, 50, 1);
        int nm = Dir(tree, Dir(tree, root, "web"), "node_modules");
        tree.AddNode("index.js", nm, false, 50, 50, 1);
        int repo = Dir(tree, root, "repo");
        Dir(tree, repo, ".git");
        tree.AddNode("LICENSE", repo, false, 50, 50, 1);
        int appData = Dir(tree, root, "AppData");
        tree.AddNode("16492", Dir(tree, appData, "pg"), false, 50, 50, 1);
        tree.AddNode("x.bin", Dir(tree, root, ".cache"), false, 50, 50, 1);
        tree.AddNode(".cursorrules", root, false, 50, 50, 1);   // configs: each tool reads its own name
        int vivado = Dir(tree, Dir(tree, root, "Videos"), "adder");
        tree.AddNode("adder.xpr", vivado, false, 40, 40, 1);
        tree.AddNode("xvlog.pb", Dir(tree, vivado, "adder.sim"), false, 50, 50, 1);

        int scoop = Dir(tree, Dir(tree, Dir(tree, root, "scoop"), "apps"), "libreoffice");
        tree.AddNode("manifest.xml", scoop, false, 50, 50, 1);

        var found = DuplicateFinder.Candidates(tree, @"C:\Users\me").Select(c => c.Path).ToList();
        Assert.Equal(2, found.Count);
        Assert.All(found, p => Assert.Contains(@"\Downloads\", p));
        Assert.Contains(tree.PathOf(keep, @"C:\Users\me"), found);
    }

    [Fact]
    public void ProfileCatalogIsCompleteAndConsistent()
    {
        Assert.Equal(9, UserProfiles.Roles.Count);
        Assert.True(UserProfiles.Tools.Count >= 40);
        var roleIds = UserProfiles.Roles.Select(r => r.Id).ToHashSet();
        Assert.All(UserProfiles.Tools, tool =>
        {
            Assert.NotEmpty(tool.Roles);
            Assert.All(tool.Roles, r => Assert.Contains(r, roleIds));
            Assert.DoesNotContain(DeveloperEcosystem.Other, tool.Ecosystems);   // a typo decodes as Other
        });
        Assert.Equal(UserProfiles.Tools.Count, UserProfiles.Tools.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void ProfileDecidesWhichEcosystemsShow()
    {
        Assert.Null(UserProfiles.EcosystemsFor(null, null));
        Assert.Empty(UserProfiles.EcosystemsFor(["everyday"], [])!);
        var hardware = UserProfiles.EcosystemsFor(["hardware"], [])!;
        Assert.Contains(DeveloperEcosystem.Fpga, hardware);
        Assert.Contains(DeveloperEcosystem.Pcb, hardware);
        Assert.Contains(DeveloperEcosystem.Embedded, hardware);
        Assert.Equal([DeveloperEcosystem.Pcb], UserProfiles.EcosystemsFor(["hardware"], ["kicad"])!);
    }

    [Fact]
    public void ProfileNarrowsTheCatalogAndItsTotals()
    {
        var (t, root) = Home();
        int web = Dir(t, root, "web");
        File(t, web, "package.json");
        Filled(t, web, "node_modules");
        int ml = Dir(t, root, "ml");
        File(t, ml, "pyproject.toml");
        int venv = Filled(t, ml, ".venv");
        File(t, venv, "pyvenv.cfg");
        var full = DeveloperCatalog.Build(t, @"C:\Users\me", t.RollUpSizes(SizeBasis.Allocated));
        Assert.Equal(2, full.Projects.Count);

        var node = DeveloperCatalog.Only(full, new HashSet<DeveloperEcosystem> { DeveloperEcosystem.Node });

        Assert.Equal(["node_modules"], node.Items.Select(i => i.DisplayName));
        Assert.Equal(["web"], node.Projects.Select(p => p.Name));
        Assert.Equal(node.Items.Sum(i => i.Bytes), node.Summary.TotalBytes);
        Assert.True(node.Summary.TotalBytes < full.Summary.TotalBytes);
    }

    [Fact]
    public void DetectionOnlyAsksWhetherPathsExist()
    {
        string vivado = Environment.ExpandEnvironmentVariables(@"%SystemDrive%\Xilinx");
        var found = UserProfiles.Detected(p => p.Equals(vivado, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["vivado"], found);

        string dir = Path.Combine(Path.GetTempPath(), $"dm-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "intelFPGA_lite"));
        try
        {
            Assert.True(UserProfiles.PathExists(Path.Combine(dir, "intelFPGA*")));
            Assert.False(UserProfiles.PathExists(Path.Combine(dir, "altera*")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
