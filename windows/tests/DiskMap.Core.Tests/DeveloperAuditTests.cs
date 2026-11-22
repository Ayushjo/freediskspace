using System.Diagnostics;
using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The developer-catalog audit: tool homes offer only their caches; backups and history never
/// rebuild and only what rebuilds is counted; outside git a generic folder is review-first; a
/// folder holding its own repository is not output; and the hand-written git reader never crashes,
/// hangs, follows a link, downloads a cloud file or calls local-only work "pushed".
/// </summary>
public class DeveloperAuditTests
{
    private const string Me = @"C:\Users\me";

    private static (FileTree Tree, int Root) Home()
    {
        var tree = new FileTree();
        return (tree, tree.AddNode("me", -1, true, 0, 0, 0));
    }

    private static int Dir(FileTree t, int parent, string name) => t.AddNode(name, parent, true, 0, 0, 0);
    private static int Put(FileTree t, int parent, string name, long size = 1_000) => t.AddNode(name, parent, false, size, size, 10);

    /// <summary>A folder with one 20 MB file inside, so it has bytes to report.</summary>
    private static int Filled(FileTree t, int parent, string name)
    {
        int d = Dir(t, parent, name);
        Put(t, d, "payload.bin", 20_000_000);
        return d;
    }

    private static DeveloperCatalogResult Build(FileTree tree, string root = Me) =>
        DeveloperCatalog.Build(tree, root, tree.RollUpSizes(SizeBasis.Allocated));

    private static string Sha(char c) => new(c, 40);

    private static string TempRepo()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dm-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    /// <summary>A repository with an origin remote and main at <paramref name="main"/> locally and on origin.</summary>
    private static void WriteRepo(string git, string head, string main, string originMain)
    {
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(git, "refs", "remotes", "origin"));
        File.WriteAllText(Path.Combine(git, "config"), "[remote \"origin\"]\n\turl = https://example/x.git\n");
        File.WriteAllText(Path.Combine(git, "HEAD"), head + "\n");
        File.WriteAllText(Path.Combine(git, "refs", "heads", "main"), main + "\n");
        File.WriteAllText(Path.Combine(git, "refs", "remotes", "origin", "main"), originMain + "\n");
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "fixture junction");
    }

    // ---- 1. Tool homes ----

    [Fact]
    public void ToolHomesOfferOnlyTheirCacheFolders()
    {
        var (t, root) = Home();
        int cargo = Dir(t, root, ".cargo");
        Put(t, Dir(t, cargo, "bin"), "cargo.exe", 20_000_000);    // cargo and rustup themselves
        Put(t, cargo, "credentials.toml");
        Filled(t, cargo, "registry");
        Filled(t, cargo, "git");
        int gradle = Dir(t, root, ".gradle");
        Put(t, gradle, "gradle.properties");                       // signing passwords, repository tokens
        Put(t, Dir(t, gradle, "init.d"), "repos.gradle");
        Filled(t, gradle, "caches");
        Filled(t, Dir(t, gradle, "wrapper"), "dists");
        Filled(t, gradle, "daemon");
        int m2 = Dir(t, root, ".m2");
        Put(t, m2, "settings.xml");
        Put(t, m2, "settings-security.xml");
        Filled(t, m2, "repository");
        int code = Dir(t, root, "code");
        int app = Dir(t, code, "app");
        Put(t, app, "build.gradle");
        int appGradle = Filled(t, app, ".gradle");                 // a project's own cache
        int ci = Dir(t, code, "ci");
        Put(t, ci, "build.gradle");
        int ciHome = Dir(t, ci, ".gradle");                        // a Gradle home kept in a project, as CI does
        Put(t, ciHome, "gradle.properties");
        Filled(t, ciHome, "caches");

        var items = Build(t).Items;

        string[] expected =
        [
            @"C:\Users\me\.cargo\git", @"C:\Users\me\.cargo\registry", @"C:\Users\me\.gradle\caches",
            @"C:\Users\me\.gradle\wrapper\dists", @"C:\Users\me\.m2\repository",
            @"C:\Users\me\code\app\.gradle", @"C:\Users\me\code\ci\.gradle\caches",
        ];
        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase),
            items.Select(i => i.AbsolutePath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(items, i => Assert.Equal(DeveloperReclaimability.Reclaimable, i.Reclaimability));
        var byPath = items.ToDictionary(i => i.AbsolutePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Cargo git checkouts", byPath[@"C:\Users\me\.cargo\git"].DisplayName);
        Assert.Equal("Gradle wrapper downloads", byPath[@"C:\Users\me\.gradle\wrapper\dists"].DisplayName);
        Assert.Equal("Maven repository", byPath[@"C:\Users\me\.m2\repository"].DisplayName);

        // The Quick Wins gate agrees: of the three .gradle folders only the project's own is output.
        var hits = QuickWins.Find(t, Me, new QuickWins.Patterns([".gradle"], []));
        Assert.Equal([appGradle], hits.Select(h => h.Id));
    }

    // ---- 2. Only what rebuilds by itself ----

    [Fact]
    public void BackupsAndHistoryNeverRebuildAndOnlyWhatRebuildsIsCounted()
    {
        var (t, root) = Home();
        int web = Dir(t, root, "web");
        Put(t, web, "package.json");
        Put(t, web, "package-lock.json");
        Filled(t, web, "node_modules");                            // review first: it re-downloads
        int pcb = Dir(t, root, "board");
        Put(t, pcb, "board.kicad_pro");
        Filled(t, pcb, "board-backups");
        Filled(t, pcb, ".history");
        int altium = Dir(t, root, "altium");
        Put(t, altium, "main.PrjPcb");
        Filled(t, altium, "History");
        Put(t, Dir(t, Dir(t, root, "revit"), "Tower_backup"), "Tower.0001.rvt", 20_000_000);
        Filled(t, root, ".npm");                                   // the one thing here that rebuilds by itself
        Filled(t, root, "Windows.old");                            // Windows Settings removes it, not us

        var result = Build(t);

        Assert.DoesNotContain(result.Items, i => i.DisplayName == "Windows.old");
        foreach (var name in new[] { "board-backups", ".history", "History", "Tower_backup" })
        {
            var item = Assert.Single(result.Items, i => i.DisplayName == name);
            Assert.Equal(DeveloperReclaimability.ReviewFirst, item.Reclaimability);
            Assert.Equal(RebuildCost.Never, item.RebuildCost);
        }
        var npm = Assert.Single(result.Items, i => i.DisplayName == "npm cache");
        Assert.Equal(npm.Bytes, result.Summary.ReclaimableBytes);
        Assert.All(result.Projects, p => Assert.Equal(0L, p.ReclaimableBytes));
        Assert.Equal(("Check before removing.", "Nothing here rebuilds by itself.", false),
            DeveloperBoard.ReinstallLine(result.Items.Where(i => i.RebuildCost == RebuildCost.Never).ToList()));
        Assert.Equal("Doesn’t rebuild", RebuildCost.Never.Title());
        Assert.Equal("Not rebuilt", DeveloperBoard.RebuildShort(RebuildCost.Never));
    }

    // ---- 3. Outside git ----

    [Fact]
    public void OutsideGitAGenericFolderBesideAManifestIsReviewFirst()
    {
        var (t, root) = Home();
        int unreal = Dir(t, root, "MyGame");
        Put(t, unreal, "MyGame.sln");
        Put(t, unreal, "MyGame.uproject");
        Filled(t, unreal, "Build");                                // Unreal's Build\ is source: icons, configs
        int unity = Dir(t, root, "Game");
        Put(t, unity, "Game.sln");
        Dir(t, unity, "ProjectSettings");
        Put(t, Dir(t, unity, "Packages"), "manifest.json", 20_000_000);
        int lib = Dir(t, root, "Lib");
        Put(t, lib, "Lib.csproj");
        Put(t, Dir(t, lib, "bin"), "Vendor.dll", 20_000_000);      // maybe a checked-in dependency
        Filled(t, Dir(t, Dir(t, root, ".yarn"), "berry"), "cache"); // Yarn's global cache is never in a repository

        var items = Build(t).Items;

        Assert.DoesNotContain(items, i => i.DisplayName is "Build" or "Packages");
        var bin = Assert.Single(items, i => i.DisplayName == "bin");
        Assert.Equal(DeveloperReclaimability.ReviewFirst, bin.Reclaimability);
        Assert.EndsWith(DeveloperCatalog.NotInGit, bin.WhyLarge);
        Assert.Equal(DeveloperReclaimability.Reclaimable, Assert.Single(items, i => i.DisplayName == "Yarn cache").Reclaimability);
    }

    [Fact]
    public void InsideGitAnIgnoredBuildFolderStillRebuildsByItself()
    {
        string dir = TempRepo();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\n");
            var tree = new FileTree();
            int repo = tree.AddNode("repo", -1, true, 0, 0, 0);
            Dir(tree, repo, ".git");
            Put(tree, repo, ".gitignore", 10);
            Put(tree, repo, "Lib.csproj");
            Filled(tree, repo, "bin");

            var bin = Assert.Single(Build(tree, dir).Items, i => i.DisplayName == "bin");
            Assert.Equal(DeveloperReclaimability.Reclaimable, bin.Reclaimability);
            Assert.DoesNotContain(DeveloperCatalog.NotInGit, bin.WhyLarge);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 4. Lightroom ----

    [Fact]
    public void LightroomSmartPreviewsAreNeverOffered()
    {
        var (t, root) = Home();
        int lr = Dir(t, Dir(t, root, "Pictures"), "Lightroom");
        Filled(t, lr, "Lightroom Catalog Previews.lrdata");
        Filled(t, lr, "Lightroom Catalog Smart Previews.lrdata");  // stand-ins for originals that may be offline

        Assert.Equal(["Lightroom Catalog Previews.lrdata"], Build(t).Items.Select(i => i.DisplayName));
    }

    // ---- 5. A folder with its own repository ----

    [Fact]
    public void AFolderHoldingItsOwnRepositoryIsNeverOutput()
    {
        string dir = TempRepo();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "dist/\n");
            var tree = new FileTree();
            int repo = tree.AddNode("repo", -1, true, 0, 0, 0);
            Dir(tree, repo, ".git");
            Put(tree, repo, ".gitignore", 10);
            Put(tree, repo, "package.json");
            int deploy = Filled(tree, repo, "dist");
            Dir(tree, deploy, ".git");                             // a gh-pages clone the outer repo ignores
            int site = Dir(tree, repo, "site");
            Put(tree, site, "package.json");
            int dist = Filled(tree, site, "dist");                 // the same, without a repository of its own

            Assert.Equal([dist], Build(tree, dir).Items.Where(i => i.DisplayName == "dist").Select(i => i.NodeID));
            Assert.Equal([dist], QuickWins.Find(tree, dir, new QuickWins.Patterns(["dist"], [])).Select(h => h.Id));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 6. Smaller rules ----

    [Fact]
    public void LookalikesOfKiCadOpenLaneAndBuildOutputAreLeftAlone()
    {
        var (t, root) = Home();
        int qt = Dir(t, root, "qtapp");
        Put(t, qt, "app.pro");                                     // a Qt project file, not KiCad's
        Filled(t, qt, "db-backups");
        int ml = Dir(t, root, "ml");
        Put(t, ml, "config.yaml");
        Put(t, ml, "train.py");
        Filled(t, Dir(t, ml, "runs"), "run_001");                  // training runs: best.pt never comes back
        int asic = Dir(t, root, "asic");
        Put(t, asic, "config.json");
        Filled(t, Dir(t, asic, "runs"), "RUN_2026.01.01");         // an OpenLane run
        int thesis = Dir(t, root, "thesis");
        Put(t, thesis, "Makefile");
        Filled(t, thesis, "out");
        int sim = Dir(t, root, "sim");
        Put(t, sim, "pyproject.toml");
        Filled(t, sim, "out");
        int extension = Dir(t, root, "extension");
        Put(t, extension, "package.json");
        Filled(t, extension, "out");                               // tsc's outDir
        int native = Dir(t, root, "native");
        Put(t, native, "App.sln");
        Filled(t, native, ".vs");                                  // also holds launch.vs.json and tasks.vs.json
        Filled(t, Dir(t, root, "infra"), ".terragrunt-cache");

        var items = Build(t).Items;

        Assert.DoesNotContain(items, i => i.DisplayName == "db-backups");
        Assert.Contains(@"\asic\", Assert.Single(items, i => i.DisplayName == "runs").AbsolutePath);
        Assert.Contains(@"\extension\", Assert.Single(items, i => i.DisplayName == "out").AbsolutePath);
        Assert.Equal(DeveloperReclaimability.ReviewFirst,
            Assert.Single(items, i => i.DisplayName == "Visual Studio cache").Reclaimability);
        Assert.Equal(DeveloperReclaimability.ReviewFirst,
            Assert.Single(items, i => i.DisplayName == ".terragrunt-cache").Reclaimability);
    }

    // ---- 7. Git inspection ----

    [Fact]
    public void AnEmptyRemoteNameIsSkippedAndTheCatalogSurvivesIt()
    {
        // `[remote "]` once threw out of the catalog build and the page showed nothing at all.
        Assert.Equal(["origin"], GitInspector.RemoteNames("[remote \"]\n[remote \"\"]\n[Remote \"origin\"]\n\turl = x\n"));
        string dir = TempRepo();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".git", "config"), "[remote \"]\n");
            var tree = new FileTree();
            int repo = tree.AddNode("repo", -1, true, 0, 0, 0);
            Dir(tree, repo, ".git");
            Put(tree, repo, "package.json");
            Filled(tree, repo, "node_modules");

            var result = Build(tree, dir);
            Assert.Single(result.Items);
            Assert.IsType<GitState.NoRemote>(Assert.Single(result.Projects).Git);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AHostileIgnorePatternIsMatchedInLinearTime()
    {
        string dir = TempRepo();
        try
        {
            // Ten **/ in a row: the backtracking engine took 1.7 s per deep path with these.
            File.WriteAllText(Path.Combine(dir, ".gitignore"), "a/" + string.Concat(Enumerable.Repeat("**/", 10)) + "b/c\n");
            var tree = new FileTree();
            int repo = tree.AddNode("repo", -1, true, 0, 0, 0);
            Dir(tree, repo, ".git");
            Put(tree, repo, ".gitignore", 10);
            int a = Dir(tree, repo, "a");
            // a/xb/cx/d/…/d/c holds the literal "b/c" and ends in "c": only the regex can say no.
            int node = Dir(tree, Dir(tree, a, "xb"), "cx");
            for (int i = 0; i < 20; i++) node = Dir(tree, node, "d");
            int deep = Dir(tree, node, "c");
            int ignored = Dir(tree, Dir(tree, Dir(tree, a, "q"), "b"), "c");

            var cache = new Dictionary<string, GitIgnoreRules?>(StringComparer.OrdinalIgnoreCase);
            var clock = Stopwatch.StartNew();
            Assert.False(GitIgnoreRules.IsIgnored(tree, repo, dir, deep, cache));
            clock.Stop();
            Assert.True(clock.ElapsedMilliseconds < 500, $"one deep path took {clock.ElapsedMilliseconds} ms");
            Assert.True(GitIgnoreRules.IsIgnored(tree, repo, dir, ignored, cache));
            // Each ignore file is read and parsed once, then shared: info/exclude (absent) and the root .gitignore.
            Assert.Equal(2, cache.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OnlineOnlyFilesAreNeverReadAndLinkedRefsAreNeverFollowed()
    {
        string dir = TempRepo();
        string git = Path.Combine(dir, ".git");
        try
        {
            WriteRepo(git, head: "ref: refs/heads/main", main: Sha('a'), originMain: Sha('a'));
            Assert.IsType<GitState.InSync>(GitInspector.Inspect(dir));

            // Reading an online-only .gitignore would download it: it counts as unreadable.
            string ignore = Path.Combine(dir, ".gitignore");
            File.WriteAllText(ignore, "bin/\n");
            File.SetAttributes(ignore, FileAttributes.Offline);
            Assert.Null(GitInspector.Read(ignore));

            // An online-only branch file is not read, and the repository is never "pushed" without it.
            string wip = Path.Combine(git, "refs", "heads", "wip");
            File.WriteAllText(wip, Sha('b') + "\n");
            File.SetAttributes(wip, FileAttributes.Offline);
            Assert.IsType<GitState.Unknown>(GitInspector.Inspect(dir));
            File.SetAttributes(wip, FileAttributes.Normal);
            File.Delete(wip);

            // A junction among the branches is not followed.
            string outside = Path.Combine(dir, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "x"), Sha('c') + "\n");
            string linked = Path.Combine(git, "refs", "heads", "linked");
            Junction(linked, outside);
            Assert.IsType<GitState.Unknown>(GitInspector.Inspect(dir));
            Directory.Delete(linked);

            // An unreadable packed-refs is unknown, never clean.
            string packed = Path.Combine(git, "packed-refs");
            File.WriteAllText(packed, "# pack-refs with: peeled\n");
            File.SetAttributes(packed, FileAttributes.Offline);
            Assert.IsType<GitState.Unknown>(GitInspector.Inspect(dir));
            File.SetAttributes(packed, FileAttributes.Normal);
            Assert.IsType<GitState.InSync>(GitInspector.Inspect(dir));
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void StashesAndDetachedCommitsAreNeverCalledPushed()
    {
        string dir = TempRepo();
        string git = Path.Combine(dir, ".git");
        try
        {
            WriteRepo(git, head: "ref: refs/heads/main", main: Sha('a'), originMain: Sha('a'));
            Assert.Equal("Pushed", DeveloperBoard.GitShort(GitInspector.Inspect(dir)));

            File.WriteAllText(Path.Combine(git, "refs", "stash"), Sha('5') + "\n");
            var stashed = Assert.IsType<GitState.Differs>(GitInspector.Inspect(dir));
            Assert.True(stashed.HasStash);
            Assert.Empty(stashed.Branches);
            Assert.Equal("Stashed changes aren’t on the remote", stashed.Title);
            Assert.Equal("Unpushed", DeveloperBoard.GitShort(stashed));
            File.Delete(Path.Combine(git, "refs", "stash"));

            File.WriteAllText(Path.Combine(git, "HEAD"), Sha('d') + "\n");    // commits made on a detached HEAD
            var detached = Assert.IsType<GitState.Differs>(GitInspector.Inspect(dir));
            Assert.True(detached.DetachedHead);
            Assert.Contains("detached", detached.Detail);

            File.WriteAllText(Path.Combine(git, "HEAD"), Sha('a') + "\n");    // detached at the remote's tip
            Assert.IsType<GitState.InSync>(GitInspector.Inspect(dir));

            File.WriteAllText(Path.Combine(git, "packed-refs"), $"{Sha('6')} refs/stash\n");
            Assert.True(Assert.IsType<GitState.Differs>(GitInspector.Inspect(dir)).HasStash);
        }
        finally { Directory.Delete(dir, true); }
    }
}
