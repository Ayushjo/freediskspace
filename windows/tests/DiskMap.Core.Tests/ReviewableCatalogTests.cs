using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The catalog on a copy of the demo PC (windows/scripts/make-demo-fixture.py): which items
/// Clean up ticks for you, which wait for a check, and how app caches group.
/// </summary>
[Collection("ProcessEnvironment")]
public class ReviewableCatalogTests
{
    private const long GB = 1_000_000_000;
    private const long MB = 1_000_000;
    private const string Root = @"C:\DemoPC";

    private static readonly (string Path, long Size)[] DemoFiles =
    [
        (@"Videos\Wedding edit 4K.mov", 26_100 * MB),
        (@"Downloads\Unity-2026.1-offline.zip", 11_200 * MB),
        (@"Projects\dashboard\node_modules\next\dist\compiled.js", 1_400 * MB),
        (@"Projects\dashboard\node_modules\@swc\core\binary.node", 900 * MB),
        (@"Projects\webapp\node_modules\react\index.js", 800 * MB),
        (@"Projects\webapp\node_modules\typescript\lib\tsc.js", 600 * MB),
        (@"Projects\rust-tool\target\debug\deps\rust_tool.rlib", 9_600 * MB),
        (@"Projects\rust-tool\Cargo.toml", 2_000),   // the evidence that makes target build output
        (@"Projects\ml-experiments\.venv\Lib\site-packages\torch\lib\torch_cpu.dll", 4_100 * MB),
        (@"Projects\ml-experiments\.venv\pyvenv.cfg", 200),   // what makes .venv a virtualenv
        (@".gradle\caches\transforms-4\objects.bin", 14_800 * MB),
        (@"AppData\Local\Android\Sdk\system-images\android-35\google_apis\x86_64\system.img", 4_100 * MB),
        (@"AppData\Local\Android\Sdk\system-images\android-33\google_apis\x86_64\system.img", 3_600 * MB),
        (@"AppData\Local\NuGet\v3-cache\packages.bin", 2_900 * MB),
        (@"AppData\Local\Spotify\Data\cache.bin", 1_900 * MB),
        (@"AppData\Local\Yarn\Cache\v6\packages.bin", 3_200 * MB),
        (@"AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\data_0", 2_000 * MB),
        (@"AppData\Local\Google\Chrome\User Data\Default\Code Cache\js\index", 400 * MB),
        (@"AppData\Local\pnpm\cache\metadata.bin", 640 * MB),
        (@"AppData\Roaming\Slack\Cache\Cache_Data\data.bin", 1_100 * MB),
        (@"AppData\Local\npm-cache\_cacache\content-v2\sha512\blobs.bin", 5_600 * MB),
        (@".cache\huggingface\hub\models--llama\blobs\model.safetensors", 12_000 * MB),
        (@"AppData\Local\pip\cache\http\cache.bin", 1_300 * MB),
    ];

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
                if (!dirs.TryGetValue(key, out int dir))
                    dirs[key] = dir = tree.AddNode(parts[i], parent, true, 0, 0, 0);
                parent = dir;
            }
            tree.AddNode(parts[^1], parent, false, size, size, 0);
        }
        return (tree, tree.RollUpSizes(SizeBasis.Logical));
    }

    private static List<ReviewableTarget> Catalog((string Path, long Size)[] files)
    {
        var (tree, totals) = Build(files);
        using var _ = new HomeOverride(Root);
        var hits = QuickWins.Find(tree, Root, QuickWins.BundledPatterns());
        return ReviewableCatalog.Build(tree, Root, totals, hits).Targets;
    }

    [Fact]
    public void DemoPcSplitsIntoRebuildsByItselfAndCheckFirst()
    {
        var targets = Catalog(DemoFiles);
        var safe = targets.Where(t => t.IsGenerallySafe).ToDictionary(t => t.DisplayName, t => t.Bytes);
        var review = targets.Where(t => t.IsReviewFirst).Select(t => (t.DisplayName, t.Bytes)).ToList();

        Assert.Equal(new Dictionary<string, long>
        {
            ["Gradle cache"] = 14_800 * MB,
            ["npm cache"] = 5_600 * MB,
            ["Yarn"] = 3_200 * MB,
            ["NuGet cache"] = 2_900 * MB,
            ["Chrome"] = 2_400 * MB,     // Cache + Code Cache, one row
            ["Spotify"] = 1_900 * MB,
            ["pip"] = 1_300 * MB,
            ["Slack"] = 1_100 * MB,
            ["pnpm"] = 640 * MB,
        }, safe);
        Assert.Equal(
        [
            ("Rust target", 9_600 * MB),
            ("Android system images", 7_700 * MB),
            (".venv", 4_100 * MB + 200),
            ("Node modules", 2_300 * MB),
            ("Node modules", 1_400 * MB),
        ], review);
        // Personal files and AI models are never offered here.
        Assert.DoesNotContain(targets, t => t.PrimaryPath.Contains("Videos") || t.PrimaryPath.Contains("huggingface"));
        Assert.DoesNotContain(targets, t => t.IsProtected);
    }

    [Fact]
    public void TargetsAreSortedLargestFirstAndNeverOverlap()
    {
        var targets = Catalog(DemoFiles);
        Assert.Equal(targets.OrderByDescending(t => t.Bytes).Select(t => t.Id), targets.Select(t => t.Id));
        var (tree, _) = Build(DemoFiles);
        var all = targets.SelectMany(t => t.NodeIds).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        foreach (int a in all)
            for (int p = tree.Parent[a]; p >= 0; p = tree.Parent[p])
                Assert.DoesNotContain(p, all);
    }

    [Fact]
    public void ChromeCachesGroupIntoOneRowWithEveryPath()
    {
        var chrome = Assert.Single(Catalog(DemoFiles), t => t.DisplayName == "Chrome");
        Assert.Equal(ReviewableCategory.Caches, chrome.Category);
        Assert.Equal(2, chrome.Paths.Count);
        Assert.All(chrome.Paths, p => Assert.StartsWith(@"C:\DemoPC\AppData\Local\Google\Chrome", p));
        Assert.Equal("Chrome can recreate this. Some sites may load more slowly next time.", chrome.Consequence);
        Assert.Equal("globe", chrome.Symbol);
    }

    [Fact]
    public void EveryPathSitsNextToItsOwnScanEntry()
    {
        // Paths are listed in name order; NodeIds must follow them, or the basket stages the
        // wrong folder for a path (Clean up pairs Paths[i] with NodeIds[i]).
        // Found out of name order (GPUCache before Cache), so sorting the paths alone would misalign them.
        (string, long)[] files =
        [
            .. DemoFiles,
            (@"AppData\Roaming\Slack\GPUCache\data_1", 100 * MB),
            (@"AppData\Roaming\Slack\Cache\data_0", 300 * MB),
            (@"AppData\Roaming\Slack\Code Cache\js\index", 200 * MB),
        ];
        var (tree, totals) = Build(files);
        using var _ = new HomeOverride(Root);
        var targets = ReviewableCatalog.Build(tree, Root, totals, QuickWins.Find(tree, Root, QuickWins.BundledPatterns())).Targets;
        Assert.Contains(targets, t => t.DisplayName == "Slack" && t.Paths.Count == 3);
        foreach (var target in targets)
        {
            Assert.Equal(target.Paths.Count, target.NodeIds.Count);
            for (int i = 0; i < target.Paths.Count; i++)
                Assert.Equal(target.Paths[i], tree.PathOf(target.NodeIds[i], Root), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ACacheInsideACacheCountsOnce()
    {
        var targets = Catalog(
        [
            (@"AppData\Roaming\discord\Cache\Cache_Data\f_0001", 300 * MB),
            (@"AppData\Roaming\discord\Cache\Code Cache\js\x", 200 * MB),
        ]);
        var discord = Assert.Single(targets);
        Assert.Equal("Discord", discord.DisplayName);
        Assert.Equal(500 * MB, discord.Bytes);
        Assert.Single(discord.NodeIds);
    }

    [Fact]
    public void CacheFoldersOutsideAppDataAndInTempAreNotAppCaches()
    {
        var targets = Catalog(
        [
            (@"Documents\Project\Cache\notes.txt", 50 * MB),
            (@"AppData\Local\Temp\setup\Cache\x.bin", 70 * MB),
        ]);
        Assert.DoesNotContain(targets, t => t.Category == ReviewableCategory.Caches);
        var temp = Assert.Single(targets);
        Assert.Equal("Temporary files", temp.DisplayName);
        Assert.True(temp.IsReviewFirst);
    }

    [Fact]
    public void UnknownAppCachesUseTheFolderUnderAppData()
    {
        var targets = Catalog(
        [
            (@"AppData\Local\Acme Tools\Widget\Cache\a.bin", 10 * MB),
            (@"AppData\Local\Microsoft\Edge\User Data\Default\Cache\b.bin", 20 * MB),
            (@"AppData\Local\JetBrains\Rider2026.1\Cache\c.bin", 30 * MB),
        ]);
        Assert.Equal(["Rider2026.1", "Edge", "Acme Tools"], targets.Select(t => t.DisplayName));
        Assert.All(targets, t => Assert.True(t.IsGenerallySafe));
    }

    [Fact]
    public void MismatchedTotalsGiveNothing()
    {
        var (tree, totals) = Build(DemoFiles);
        Assert.Empty(ReviewableCatalog.Build(tree, Root, totals[..^1], []).Targets);
        Assert.Same(ReviewableSummary.Empty, ReviewableCatalog.Build(new FileTree(), Root, [], []).Summary);
    }

    [Fact]
    public void SummaryAddsUpByCategory()
    {
        var targets = Catalog(DemoFiles);
        var summary = ReviewableCatalog.Summarize(targets);
        Assert.Equal(targets.Sum(t => t.Bytes), summary.TotalBytes);
        Assert.Equal(summary.CacheBytes + summary.BuildBytes + summary.PackageBytes + summary.OtherBytes, summary.TotalBytes);
        Assert.Equal(targets.Count, summary.TargetCount);
        Assert.Equal(targets.Count(t => t.Category == ReviewableCategory.Caches), summary.CacheAppCount);
    }

    private sealed class HomeOverride : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(UserFolders.OverrideVariable);
        public HomeOverride(string? value) => Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, value);
        public void Dispose() => Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, _previous);
    }
}

/// <summary>The safety table on real Windows paths.</summary>
public class SafetyClassifierTests
{
    [Theory]
    [InlineData(@"C:\Windows\WinSxS", "WinSxS", true, SafetyLevel.Protected)]
    [InlineData(@"D:\Windows", "Windows", true, SafetyLevel.Protected)]
    [InlineData(@"C:\System Volume Information", "System Volume Information", true, SafetyLevel.Protected)]
    [InlineData(@"C:\$Recycle.Bin", "$Recycle.Bin", true, SafetyLevel.Protected)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender", "Windows Defender", true, SafetyLevel.Protected)]
    [InlineData(@"C:\pagefile.sys", "pagefile.sys", false, SafetyLevel.Protected)]
    [InlineData(@"C:\Users\you\AppData\Roaming\Microsoft\Protect", "Protect", true, SafetyLevel.Protected)]
    [InlineData(@"C:\Users\you\AppData\Local\Microsoft\Credentials", "Credentials", true, SafetyLevel.Protected)]
    [InlineData(@"C:\Users\you\AppData", "AppData", true, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\AppData\Local\npm-cache", "npm-cache", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\AppData\Local\Yarn\Cache", "Cache", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\AppData\Local\pnpm\cache", "cache", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\AppData\Local\NuGet\v3-cache", "v3-cache", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\AppData\Local\Google\Chrome\User Data\Default\Cache", "Cache", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\AppData\Local\Microsoft\Windows\INetCache", "INetCache", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\AppData\Local\Spotify\Data", "Data", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\.gradle", ".gradle", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\.cargo\registry", "registry", true, SafetyLevel.Safe)]
    [InlineData(@"C:\Users\you\code\app\node_modules", "node_modules", true, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\code\tool\target", "target", true, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\code\ml\.venv", ".venv", true, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\AppData\Local\Android\Sdk\system-images", "system-images", true, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\AppData\Local\Temp", "Temp", true, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\Documents\Cache", "Cache", true, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\Downloads", "Downloads", true, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\Downloads\setup.exe", "setup.exe", false, SafetyLevel.Review)]
    [InlineData(@"C:\Users\you\Somewhere\else", "else", true, SafetyLevel.Review)]
    public void Levels(string path, string name, bool isDirectory, SafetyLevel expected) =>
        Assert.Equal(expected, SafetyClassifier.Assess(path, name, isDirectory).Level);

    [Fact]
    public void WindowsFolderOnlyAtTheDriveRoot() =>
        Assert.Equal(SafetyLevel.Review,
            SafetyClassifier.Assess(@"C:\Users\you\Projects\windows", "windows", true).Level);

    [Fact]
    public void SpecificConsequencesAreKept()
    {
        var npm = SafetyClassifier.Assess(@"C:\Users\you\AppData\Local\npm-cache", "npm-cache", true);
        Assert.True(npm.HasSpecificConsequences);
        Assert.Equal("Next npm install may re-download packages (slower once).", npm.Consequences);
        var unknown = SafetyClassifier.Assess(@"C:\x\y", "y", true);
        Assert.False(unknown.HasSpecificConsequences);
        Assert.Equal(SafetyAssessment.DefaultConsequences(SafetyLevel.Review), unknown.Consequences);
    }
}
