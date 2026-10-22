using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// These tests change a process-wide environment variable, so they run on
/// their own, after the parallel tests.
/// </summary>
[CollectionDefinition("ProcessEnvironment", DisableParallelization = true)]
public class ProcessEnvironmentCollection { }

/// <summary>
/// The render harness points the catalogs at a demo profile with DISKMAP_HOME,
/// so a synthetic folder classifies like a real profile. Without it, nothing
/// changes; and the CleanupQueue safety list never follows it.
/// </summary>
[Collection("ProcessEnvironment")]
public class UserFoldersTests
{
    [Fact]
    public void WithoutOverrideTheRealKnownFoldersAreUsed()
    {
        using var _ = new HomeOverride(null);
        Assert.Null(UserFolders.Override);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), UserFolders.Home);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), UserFolders.LocalAppData);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), UserFolders.RoamingAppData);
    }

    [Fact]
    public void OverridePointsAllThreeFoldersAtTheDemoProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), "DemoPC-folders");
        using var _ = new HomeOverride(root + "\\");
        Assert.Equal(root, UserFolders.Home);
        Assert.Equal(Path.Combine(root, "AppData", "Local"), UserFolders.LocalAppData);
        Assert.Equal(Path.Combine(root, "AppData", "Roaming"), UserFolders.RoamingAppData);
        Assert.Equal(Path.Combine(root, "AppData", "Local", "npm-cache"), UserFolders.Expand(@"%LOCALAPPDATA%\npm-cache"));
        Assert.Equal(Path.Combine(root, ".gradle", "caches"), UserFolders.Expand(@"%USERPROFILE%\.gradle\caches"));
        Assert.Equal(Path.Combine(root, "Library/Caches"), UserFolders.Expand("~/Library/Caches"));
    }

    [Fact]
    public void QuickWinsFindTheDemoProfilesCaches()
    {
        // A demo profile at C:\DemoPC with %LOCALAPPDATA%\npm-cache inside it.
        var tree = new FileTree();
        int root = tree.AddNode("DemoPC", -1, true, 0, 0, 0);
        int appData = tree.AddNode("AppData", root, true, 0, 0, 0);
        int local = tree.AddNode("Local", appData, true, 0, 0, 0);
        int npm = tree.AddNode("npm-cache", local, true, 0, 0, 0);
        var patterns = new QuickWins.Patterns([], [@"%LOCALAPPDATA%\npm-cache"]);

        using (new HomeOverride(@"C:\DemoPC"))
            Assert.Equal([npm], QuickWins.Find(tree, @"C:\DemoPC", patterns).Select(h => h.Id).ToList());
        using (new HomeOverride(null))
            Assert.Empty(QuickWins.Find(tree, @"C:\DemoPC", patterns));
    }

    [Fact]
    public void TheCleanupSafetyListIgnoresTheOverride()
    {
        string real = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string root = Path.Combine(Path.GetTempPath(), "DemoPC-safety");
        using var _ = new HomeOverride(root);
        string source = File.ReadAllText(FindRepoFile(@"windows\src\DiskMap.Core\CleanupQueue.cs"));
        Assert.DoesNotContain("UserFolders", source);
        Assert.NotEqual(real, UserFolders.LocalAppData);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relative))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private sealed class HomeOverride : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(UserFolders.OverrideVariable);
        public HomeOverride(string? value) => Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, value);
        public void Dispose() => Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, _previous);
    }
}
