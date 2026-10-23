using DiskMap.App.Pages;
using DiskMap.Core;
using static DiskMap.App.Pages.CleanUpPage;

namespace DiskMap.App.Tests;

/// <summary>Clean up's rows, ticks and wording (Mac: CleanUpView.buildItems / defaultTicks / age / place).</summary>
public class CleanUpTests
{
    private static ReviewableTarget Target(string id, string name, long bytes, SafetyLevel level, params string[] paths) =>
        new(id, ReviewableCategory.Caches, name, "Application cache", bytes, paths[0], paths,
            paths.Select((_, i) => 100 + i).ToList(), new SafetyAssessment(level, "", name), "", "internaldrive", null);

    private static CleanItem File(string id, CleanGroup group, int node, long bytes) =>
        new(id, group, id, "why", bytes, [(node, $@"C:\Users\you\Downloads\{id}")], $@"C:\Users\you\Downloads\{id}");

    [Fact]
    public void RowsAreGroupedBySafetyAndSortedBiggestFirst()
    {
        var items = BuildItems(
        [
            Target("a", "Chrome", 2_400, SafetyLevel.Safe, @"C:\x\Cache", @"C:\x\Code Cache"),
            Target("b", "Node modules", 9_000, SafetyLevel.Review, @"C:\p\node_modules"),
            Target("c", "Windows", 99_000, SafetyLevel.Protected, @"C:\Windows"),
        ], [], [File("setup.exe", CleanGroup.Downloads, 7, 5_000)], []);
        Assert.Equal(["r-b", "setup.exe", "r-a"], items.Select(i => i.Id));
        Assert.Equal(CleanGroup.ReviewFirst, items[0].Group);
        Assert.Equal(CleanGroup.Rebuilds, items[2].Group);
        Assert.DoesNotContain(items, i => i.Title == "Windows");          // protected never listed
        Assert.StartsWith("2 places · ", items[2].Why);                   // grouped caches say how many
        Assert.Equal(2, items[2].Targets.Count);
    }

    [Fact]
    public void AFileThatIsBothAnOldDownloadAndMediaIsListedOnce()
    {
        var items = BuildItems([], [],
            [File("clip.mov", CleanGroup.Downloads, 7, 5_000)],
            [File("clip.mov", CleanGroup.Media, 7, 5_000), File("film.mov", CleanGroup.Media, 8, 6_000)]);
        Assert.Equal(2, items.Count);
        Assert.Single(items, i => i.Group == CleanGroup.Downloads);
        Assert.Single(items, i => i.Group == CleanGroup.Media && i.Title == "film.mov");
    }

    [Fact]
    public void EmptyRowsAreDropped() =>
        Assert.Empty(BuildItems([Target("z", "Empty", 0, SafetyLevel.Safe, @"C:\e")], [], [], []));

    [Fact]
    public void OnlyRebuildsAreTickedAndNothingAlreadyInTheBasket()
    {
        var items = BuildItems(
        [
            Target("a", "Chrome", 2_400, SafetyLevel.Safe, @"C:\x\Cache"),
            Target("s", "Spotify", 1_900, SafetyLevel.Safe, @"C:\s\Data"),
            Target("b", "Node modules", 9_000, SafetyLevel.Review, @"C:\p\node_modules"),
        ], [], [File("setup.exe", CleanGroup.Downloads, 7, 5_000)], []);
        var ticks = DefaultTicks(items, path => path == @"C:\s\Data", tickRebuilds: true);
        Assert.Equal(["r-a"], ticks);
        Assert.Empty(DefaultTicks(items, _ => false, tickRebuilds: false));
    }

    [Theory]
    [InlineData(0, "today")]
    [InlineData(1, "1 days old")]
    [InlineData(13, "13 days old")]
    [InlineData(14, "2 weeks old")]
    [InlineData(59, "8 weeks old")]
    [InlineData(60, "2 months old")]
    [InlineData(729, "24 months old")]
    [InlineData(730, "2 years old")]
    public void AgeMatchesTheMac(int days, string expected) => Assert.Equal(expected, Age(days));

    [Theory]
    [InlineData(@"C:\Users\you\Downloads\a.zip", @"C:\Users\you", "Downloads")]
    [InlineData(@"C:\Users\you\Downloads\misc\a.zip", @"C:\Users\you", @"Downloads\misc")]
    [InlineData(@"C:\Users\you\a.zip", @"C:\Users\you", "you")]
    public void PlaceIsRelativeToTheScan(string path, string root, string expected) =>
        Assert.Equal(expected, Place(path, root));

    [Fact]
    public void GroupWordsMatchTheBoard()
    {
        Assert.Equal("Rebuilds by itself", GroupTitle(CleanGroup.Rebuilds));
        Assert.Equal("Comes back, but check first", GroupTitle(CleanGroup.ReviewFirst));
        Assert.Equal("Old downloads", GroupTitle(CleanGroup.Downloads));
        Assert.Equal("Your files", GroupTitle(CleanGroup.Media));
        Assert.Equal("Never ticked for you. Look before you add them", GroupSubtitle(CleanGroup.Media));
    }
}
