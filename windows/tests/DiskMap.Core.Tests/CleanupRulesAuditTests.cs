using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The audit's classifier and Clean up rule findings: names that matched personal or tool data
/// as regenerable, cache folders Clean up ticked that were not caches, and Temp staged whole.
/// </summary>
public class ClassifierAuditTests
{
    [Theory]
    // electron* also took EA's app data.
    [InlineData(@"C:\Users\me\AppData\Local\Electronic Arts\EA Desktop\cache", true, "appdata", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\AppData\Local\electron\Cache", true, "pkgcache", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\AppData\Local\electron-builder\Cache", true, "pkgcache", CleanupAdvice.Fine)]
    // The Gradle and Maven homes keep credentials beside their caches: only the caches regenerate.
    [InlineData(@"C:\Users\me\.gradle", true, "devtools", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\.gradle\gradle.properties", false, "devtools", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\.gradle\caches", true, "pkgcache", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\.gradle\wrapper\dists", true, "pkgcache", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\.m2", true, "devtools", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\.m2\settings.xml", false, "devtools", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\.m2\repository", true, "pkgcache", CleanupAdvice.Fine)]
    // A project's own .gradle is its build state.
    [InlineData(@"C:\Users\me\Documents\codes\app\.gradle", true, "deps", CleanupAdvice.Fine)]
    // DerivedData is Xcode's name; on Windows it is whatever the user called it.
    [InlineData(@"D:\Thesis\DerivedData\results.parquet", false, "other", CleanupAdvice.Fine)]
    // .cache keeps its class, but some caches there hold uploads in progress.
    [InlineData(@"C:\Users\me\.cache\rclone\vfs\remote\notes.md", false, "pkgcache", CleanupAdvice.Review)]
    // Package caches count only in the profile; a project's .yarn is committed.
    [InlineData(@"C:\Users\me\Documents\codes\web\.yarn", true, "projects", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\Documents\codes\web\.yarn\releases\yarn-4.1.0.cjs", false, "projects", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\Documents\codes\lib\.nuget", true, "projects", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\.yarn\berry\cache", true, "pkgcache", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\.yarn\bin", true, "devtools", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\.nuget\packages", true, "pkgcache", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\.nuget\plugins", true, "devtools", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\.npm", true, "pkgcache", CleanupAdvice.Fine)]
    [InlineData(@"C:\Users\me\.pnpm-store", true, "pkgcache", CleanupAdvice.Fine)]
    [InlineData(@"D:\.pnpm-store", true, "pkgcache", CleanupAdvice.Fine)]
    // Synced folders: removing a file removes it from the cloud too. Review, never blocked.
    [InlineData(@"C:\Users\me\OneDrive\Thesis", true, "cloud", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\OneDrive - Contoso\Reports\q3.xlsx", false, "cloud", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\Google Drive\Photos", true, "cloud", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\Dropbox\Work", true, "cloud", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\iCloudDrive\Notes", true, "cloud", CleanupAdvice.Review)]
    [InlineData(@"C:\Users\me\Box\Shared", true, "cloud", CleanupAdvice.Review)]
    // Caches that hold the only copy: OneNote's unsynced notes, Box Drive's pending uploads.
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\OneNote\16.0\cache", true, "appdata", CleanupAdvice.Warn)]
    [InlineData(@"C:\Users\me\AppData\Local\Box\Box\cache", true, "cloud", CleanupAdvice.Warn)]
    public void Verdicts(string path, bool isDirectory, string classId, CleanupAdvice advice)
    {
        var verdict = StorageClassifier.Classify(path, isDirectory);
        Assert.Equal(classId, verdict.Class.Id);
        Assert.Equal(advice, verdict.Advice);
    }

    [Fact]
    public void TheNotesSayWhy()
    {
        Assert.Contains("credentials", StorageClassifier.Classify(@"C:\Users\me\.gradle\gradle.properties", false).Note);
        Assert.Contains("credentials", StorageClassifier.Classify(@"C:\Users\me\.m2\settings.xml", false).Note);
        var cloud = StorageClassifier.Classify(@"C:\Users\me\OneDrive\Thesis\draft.docx", false);
        Assert.Contains("other devices", cloud.Note);
        Assert.Contains("Free up space", cloud.Instead);
        Assert.NotNull(StorageClassifier.Classify(@"C:\Users\me\.cache\rclone", true).Note);
    }

    [Fact]
    public void RollupSplitsToolHomesFromTheirCaches()
    {
        var tree = new FileTree();
        int me = tree.AddNode("me", -1, true, 0, 0, 0);
        int gradle = tree.AddNode(".gradle", me, true, 0, 0, 0);
        tree.AddNode("gradle.properties", gradle, false, 1, 1, 0);
        tree.AddNode("x.bin", tree.AddNode("caches", gradle, true, 0, 0, 0), false, 10, 10, 0);
        int m2 = tree.AddNode(".m2", me, true, 0, 0, 0);
        tree.AddNode("settings.xml", m2, false, 2, 2, 0);
        tree.AddNode("y.jar", tree.AddNode("repository", m2, true, 0, 0, 0), false, 20, 20, 0);
        var rollup = StorageClassifier.Rollup(tree, @"C:\Users\me", tree.RollUpSizes(SizeBasis.Logical))
            .ToDictionary(r => r.Class.Id, r => r.Bytes);
        Assert.Equal(30, rollup["pkgcache"]);
        Assert.Equal(3, rollup["devtools"]);
    }
}

/// <summary>The Safe rules Clean up ticks from: only download caches, never a tool's whole home.</summary>
public class SafeRuleAuditTests
{
    [Theory]
    [InlineData(@"C:\Users\you\.gradle", ".gradle", SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\.gradle\caches", "caches", SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\.cargo", ".cargo", SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\.cargo\bin", "bin", SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\.cargo\registry", "registry", SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\.cargo\git", "git", SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\.cargo\github-notes", "github-notes", SafetyLevel.Review)]
    [InlineData(@"D:\Thesis\DerivedData", "DerivedData", SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\AppData\Local\Microsoft\Windows\INetCache", "INetCache", SafetyLevel.Review)]
    public void Levels(string path, string name, SafetyLevel expected) =>
        Assert.Equal(expected, SafetyClassifier.Assess(path, name, isDirectory: true).Level);
}

/// <summary>What Clean up offers, on a copy of a profile (DISKMAP_HOME).</summary>
[Collection("ProcessEnvironment")]
public class CleanupCatalogAuditTests
{
    private const long MB = 1_000_000;
    private const string Root = @"C:\DemoPC";

    private static (FileTree Tree, long[] Totals) Build((string Path, long Size)[] files)
    {
        var tree = new FileTree();
        int root = tree.AddNode("DemoPC", -1, true, 0, 0, 0);
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        foreach (var (path, size) in files)
        {
            string[] parts = path.Split('\\');
            string key = "";
            int parent = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                key = key.Length == 0 ? parts[i] : key + "\\" + parts[i];
                if (!dirs.TryGetValue(key, out int dir)) dirs[key] = dir = tree.AddNode(parts[i], parent, true, 0, 0, 0);
                parent = dir;
            }
            tree.AddNode(parts[^1], parent, false, size, size, 0);
        }
        return (tree, tree.RollUpSizes(SizeBasis.Logical));
    }

    private static (List<ReviewableTarget> Targets, FileTree Tree) Catalog(params (string Path, long Size)[] files)
    {
        var (tree, totals) = Build(files);
        using var _ = new HomeOverride(Root);
        var hits = QuickWins.Find(tree, Root, QuickWins.BundledPatterns());
        return (ReviewableCatalog.Build(tree, Root, totals, hits).Targets, tree);
    }

    [Fact]
    public void AFolderCalledCacheInsideAPackageIsItsSource()
    {
        var (targets, _) = Catalog(
            (@"AppData\Roaming\npm\node_modules\npm\node_modules\make-fetch-happen\lib\cache\entry.js", 10 * MB),
            (@"AppData\Roaming\Python\Python312\site-packages\fsspec\cache\core.py", 10 * MB),
            (@"AppData\Roaming\Tool\lib\python3\dist-packages\tool\cache\index.py", 10 * MB));
        Assert.Empty(targets);
    }

    [Fact]
    public void WhatTheQueueRefusesOrTheClassifierWarnsAboutIsNeverOffered()
    {
        var (targets, _) = Catalog(
            (@"AppData\Local\Microsoft\Windows\INetCache\IE\x.dat", 10 * MB),                // Windows' own: never staged
            (@"AppData\Local\Programs\cursor\resources\app\cache\x.bin", 10 * MB),           // an installed app
            (@"AppData\Local\Google\Chrome\User Data\Default\Storage\ext\a\def\GPUCache\d", 10 * MB),   // the browser profile
            (@"AppData\Local\Microsoft\OneNote\16.0\cache\notes.bin", 10 * MB),              // unsynced notes
            (@"AppData\Local\Box\Box\cache\upload.bin", 10 * MB));                           // files not uploaded yet
        Assert.Empty(targets);
    }

    [Fact]
    public void OnlyTheAppsItKnowsRebuildByThemselves()
    {
        var (targets, _) = Catalog(
            (@"AppData\Local\Acme Tools\Widget\Cache\a.bin", 10 * MB),
            (@"AppData\Local\Google\Chrome\User Data\Default\Cache\b.bin", 20 * MB));
        Assert.True(Assert.Single(targets, t => t.DisplayName == "Chrome").IsGenerallySafe);
        var acme = Assert.Single(targets, t => t.DisplayName == "Acme Tools");
        Assert.True(acme.IsReviewFirst);
        Assert.Contains("unsynced", acme.Consequence);
    }

    [Fact]
    public void TempIsOfferedAsItsEntriesNeverAsTheFolder()
    {
        var (targets, tree) = Catalog(
            (@"AppData\Local\Temp\setup-1234\installer.msi", 50 * MB),
            (@"AppData\Local\Temp\big.log", 30 * MB),
            (@"AppData\Local\Temp\tiny.tmp", 1 * MB));
        var temp = Assert.Single(targets);
        Assert.Equal("Temporary files", temp.DisplayName);
        Assert.True(temp.IsReviewFirst);
        Assert.Equal(81 * MB, temp.Bytes);
        Assert.Equal(
            [$@"{Root}\AppData\Local\Temp\setup-1234", $@"{Root}\AppData\Local\Temp\big.log", $@"{Root}\AppData\Local\Temp\tiny.tmp"],
            temp.Paths);
        Assert.Equal(temp.Paths[0], temp.PrimaryPath);
        Assert.Equal(temp.Paths, temp.NodeIds.Select(id => tree.PathOf(id, Root)));
        Assert.DoesNotContain(temp.Paths, p => p.EndsWith(@"\Temp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GradleOffersItsCachesNotItsHome()
    {
        var (targets, _) = Catalog(
            (@".gradle\caches\modules-2\files.bin", 100 * MB),
            (@".gradle\gradle.properties", 2_000));
        var gradle = Assert.Single(targets);
        Assert.Equal("Gradle cache", gradle.DisplayName);
        Assert.Equal($@"{Root}\.gradle\caches", gradle.PrimaryPath);
        Assert.Equal(100 * MB, gradle.Bytes);
        Assert.True(gradle.IsGenerallySafe);
    }

    [Fact]
    public void AThesisFolderCalledDerivedDataIsNotOffered()
    {
        var (targets, _) = Catalog((@"Documents\Thesis\DerivedData\results.parquet", 500 * MB));
        Assert.Empty(targets);
    }

    [Fact]
    public void ThePatternsHoldOnlyWhatCanMatchOnWindows()
    {
        var patterns = QuickWins.BundledPatterns();
        Assert.DoesNotContain(".gradle", patterns.DirectoryNames);
        Assert.DoesNotContain("DerivedData", patterns.DirectoryNames);
        Assert.DoesNotContain(patterns.PathSuffixes, s => s.StartsWith('~') || s.Contains("INetCache"));
        Assert.Contains(@"%USERPROFILE%\.gradle\caches", patterns.PathSuffixes);
        // Why the Mac suffixes went: "~/" expands with a forward slash, which no Windows path has.
        var (tree, _) = Build([(@"Library\Caches\x.bin", 1 * MB)]);
        using var _ = new HomeOverride(Root);
        Assert.Empty(QuickWins.Find(tree, Root, new QuickWins.Patterns([], ["~/Library/Caches"])));
    }

    private sealed class HomeOverride : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(UserFolders.OverrideVariable);
        public HomeOverride(string? value) => Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, value);
        public void Dispose() => Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, _previous);
    }
}
