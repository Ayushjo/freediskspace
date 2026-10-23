using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The demo PC (DISKMAP_HOME) rolls up like a real profile, Cargo's target folder is build
/// output, and setup .exe files are installers — the Mac demo's categories, on Windows.
/// </summary>
[Collection("ProcessEnvironment")]
public class ClassifierHomeTests
{
    private const long GB = 1_000_000_000;

    private static (FileTree Tree, long[] Totals) Build(params (string Path, long Size)[] files)
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

    private static Dictionary<string, long> Rollup((FileTree Tree, long[] Totals) built, string? home)
    {
        string? previous = Environment.GetEnvironmentVariable(UserFolders.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, home);
            return StorageClassifier.Rollup(built.Tree, @"C:\DemoPC", built.Totals)
                .ToDictionary(t => t.Class.Id, t => t.Bytes);
        }
        finally { Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, previous); }
    }

    private static readonly (string, long)[] Files =
    [
        (@"Videos\Wedding edit 4K.mov", 26 * GB),
        (@"Pictures\Camera Roll\IMG_0001.heic", 21 * GB),
        (@"Music\FL Studio\Album sessions.flp", 4 * GB),
        (@"Downloads\UnitySetup-2026.1.exe", 11 * GB),
        (@"Downloads\ubuntu-24.04-desktop.iso", 6 * GB),
        (@"Projects\rust-tool\Cargo.toml", 2_000),
        (@"Projects\rust-tool\target\debug\deps\rust_tool.rlib", 9 * GB),
        (@"Projects\other\target\notes.txt", 1 * GB),
    ];

    [Fact]
    public void TheDemoHomeRollsUpLikeAProfile()
    {
        var byClass = Rollup(Build(Files), @"C:\DemoPC");
        Assert.Equal(51 * GB, byClass["media"]);
        Assert.Equal(17 * GB, byClass["installers"]);   // the setup .exe and the .iso
        Assert.Equal(9 * GB, byClass["deps"]);          // only the target beside a Cargo.toml
        Assert.Equal(Build(Files).Totals[0], byClass.Values.Sum());
    }

    [Fact]
    public void WithoutTheOverrideTheFolderIsNotAProfile()
    {
        var byClass = Rollup(Build(Files), null);
        Assert.False(byClass.ContainsKey("media"));
        Assert.Equal(9 * GB, byClass["deps"]);
    }

    [Theory]
    [InlineData("UnitySetup-2026.1.exe", "installers")]
    [InlineData("python-3.13-installer.exe", "installers")]
    [InlineData("game.exe", "other")]
    public void SetupExecutablesAreInstallers(string name, string expected)
    {
        var verdict = StorageClassifier.Classify(@"D:\Stuff\" + name, false);
        Assert.Equal(expected, verdict.Class.Id);
    }
}
