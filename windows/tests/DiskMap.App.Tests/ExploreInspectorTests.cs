using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>Explore's inspector: what can be freed (Mac: FreeableIndex), its layout rule and its wording.</summary>
public class ExploreInspectorTests
{
    // root
    // ├─ Projects
    // │  ├─ app
    // │  │  ├─ node_modules   (review-first target)  400
    // │  │  └─ main.c                                 50
    // │  └─ cache          (safe target)
    // │     └─ blob                                  100
    // ├─ Library
    // │  └─ Caches         (safe target)
    // │     ├─ a                                     300
    // │     └─ b                                     200
    // └─ notes.txt                                    30
    private static (FileTree Tree, long[] Totals, Dictionary<string, int> Ids) Fixture()
    {
        var tree = new FileTree();
        var ids = new Dictionary<string, int>();
        int add(string key, string name, int parent, bool dir, long size = 0) =>
            ids[key] = tree.AddNode(name, parent, dir, size, size, 0);
        int root = add("root", "Demo PC", -1, true);
        int projects = add("projects", "Projects", root, true);
        int app = add("app", "app", projects, true);
        int modules = add("modules", "node_modules", app, true);
        add("dep", "dep.js", modules, false, 400);
        add("main", "main.c", app, false, 50);
        int cache = add("cache", "cache", projects, true);
        add("blob", "blob", cache, false, 100);
        int library = add("library", "Library", root, true);
        int caches = add("caches", "Caches", library, true);
        add("a", "a", caches, false, 300);
        add("b", "b", caches, false, 200);
        add("notes", "notes.txt", root, false, 30);
        return (tree, tree.RollUpSizes(SizeBasis.Logical), ids);
    }

    private static ReviewableTarget Target(string name, SafetyLevel level, params int[] nodes) =>
        new(name, ReviewableCategory.Caches, name, "", 0, "", nodes.Select(n => $"node{n}").ToList(), nodes,
            new SafetyAssessment(level, "", name), "", "internaldrive", null);

    private static (FileTree, long[], Dictionary<string, int>, FreeableIndex, List<ReviewableTarget>) Indexed()
    {
        var (tree, totals, ids) = Fixture();
        var targets = new List<ReviewableTarget>
        {
            Target("Caches", SafetyLevel.Safe, ids["cache"], ids["caches"]),
            Target("Node modules", SafetyLevel.Review, ids["modules"]),
        };
        return (tree, totals, ids, new FreeableIndex(targets, totals), targets);
    }

    [Fact]
    public void OnlySafeTargetsAndWhatIsInsideThemCount()
    {
        var (tree, _, ids, index, _) = Indexed();
        Assert.True(index.Contains(ids["caches"], tree));
        Assert.True(index.Contains(ids["a"], tree));            // inside a target
        Assert.True(index.Contains(ids["blob"], tree));
        Assert.False(index.Contains(ids["library"], tree));     // above a target is not inside it
        Assert.False(index.Contains(ids["modules"], tree));     // review first is never "can be freed"
        Assert.False(index.Contains(ids["dep"], tree));
        Assert.False(index.Contains(-1, tree));
        Assert.False(index.Contains(tree.Count, tree));
    }

    [Fact]
    public void BytesAreTheTargetsBeneathOrAllOfItInsideOne()
    {
        var (tree, totals, ids, index, _) = Indexed();
        Assert.Equal(600, index.Bytes(ids["root"], tree, totals));      // 100 + 500
        Assert.Equal(100, index.Bytes(ids["projects"], tree, totals));
        Assert.Equal(0, index.Bytes(ids["app"], tree, totals));
        Assert.Equal(500, index.Bytes(ids["library"], tree, totals));
        Assert.Equal(300, index.Bytes(ids["a"], tree, totals));         // inside: the whole node
        Assert.Equal(0, index.Bytes(99, tree, totals));
        Assert.Equal(0, new FreeableIndex([], totals).Bytes(ids["root"], tree, totals));
    }

    [Fact]
    public void MostlyMeansInsideATargetOrAtLeastSixtyPercent()
    {
        var (tree, totals, ids, index, _) = Indexed();
        Assert.True(index.IsMostly(ids["library"], tree, totals));      // 500 of 500
        Assert.True(index.IsMostly(ids["b"], tree, totals));
        Assert.False(index.IsMostly(ids["projects"], tree, totals));    // 100 of 550
        Assert.False(index.IsMostly(ids["root"], tree, totals));        // 600 of 1080
        Assert.False(index.IsMostly(ids["notes"], tree, totals));
    }

    [Fact]
    public void AncestorIsStrict()
    {
        var (tree, _, ids, _, _) = Indexed();
        Assert.True(FreeableIndex.IsAncestor(ids["root"], ids["a"], tree));
        Assert.True(FreeableIndex.IsAncestor(ids["library"], ids["caches"], tree));
        Assert.False(FreeableIndex.IsAncestor(ids["caches"], ids["caches"], tree));
        Assert.False(FreeableIndex.IsAncestor(ids["projects"], ids["a"], tree));
        Assert.False(FreeableIndex.IsAncestor(ids["root"], -3, tree));
    }

    [Fact]
    public void TheBasketButtonStagesTheSafeTargetsInsideTheFolderOnceEach()
    {
        var (tree, _, ids, _, targets) = Indexed();
        targets.Add(Target("Duplicate", SafetyLevel.Safe, ids["caches"]));
        var fromRoot = ExploreInspector.FreeableRequests(targets, tree, ids["root"]);
        Assert.Equal([(ids["cache"], "Explore: Caches"), (ids["caches"], "Explore: Caches")], fromRoot);
        Assert.Equal([(ids["caches"], "Explore: Caches")], ExploreInspector.FreeableRequests(targets, tree, ids["caches"]));
        Assert.Empty(ExploreInspector.FreeableRequests(targets, tree, ids["app"]));   // review first stays out
    }

    [Theory]
    [InlineData(1100, 320)]      // drawer below 1200: 374, capped at 320
    [InlineData(1200, 290)]
    [InlineData(1449, 290)]
    [InlineData(1450, 320)]
    [InlineData(2000, 320)]
    [InlineData(800, 280)]       // drawer: 34% clamped to 280…320
    [InlineData(900, 306)]
    [InlineData(1199, 320)]
    public void ColumnWidthFollowsTheMacSplit(double window, double expected) =>
        Assert.Equal(expected, ExploreInspector.ColumnWidth(window), 3);

    [Fact]
    public void WordingMatchesTheMac()
    {
        Assert.Equal("—", ExploreInspector.LastUsed(0, 20_000));
        Assert.Equal("Today", ExploreInspector.LastUsed(20_000, 20_000));
        Assert.Equal("Yesterday", ExploreInspector.LastUsed(19_999, 20_000));
        Assert.Equal("2 days ago", ExploreInspector.LastUsed(19_998, 20_000));
        Assert.Equal("1 week ago", ExploreInspector.LastUsed(19_993, 20_000));
        Assert.Equal("8 weeks ago", ExploreInspector.LastUsed(19_941, 20_000));
        Assert.Equal("2 months ago", ExploreInspector.LastUsed(19_940, 20_000));
        Assert.Equal("2 years ago", ExploreInspector.LastUsed(19_270, 20_000));

        Assert.Equal("Unknown", ExploreInspector.RelativeAgeLong(0, 20_000));
        Assert.Equal("30 days ago", ExploreInspector.RelativeAgeLong(19_970, 20_000));
        Assert.Equal("1 month ago", ExploreInspector.RelativeAgeLong(19_969, 20_000));
        Assert.Equal("1 year ago", ExploreInspector.RelativeAgeLong(19_635, 20_000));

        Assert.Equal("<0.1%", ExploreInspector.Percent(0.0004));
        Assert.Equal("0.0%", ExploreInspector.Percent(0));
        Assert.Equal("12.5%", ExploreInspector.Percent(0.125));
        Assert.Equal("100.0%", ExploreInspector.Percent(3));

        Assert.Equal("Review first", ExploreInspector.SafetyTitle(SafetyLevel.Review));
        Assert.Equal("Safe", ExploreInspector.SafetyTitle(SafetyLevel.Safe));
    }

    [Fact]
    public void ParentDisplayFoldsTheProfileAndKeepsFortyEightCharacters()
    {
        Assert.Equal(@"~\Videos\", ExploreInspector.ParentDisplay(@"C:\Users\me\Videos\clip.mov", @"C:\Users\me"));
        Assert.Equal(@"D:\Media\", ExploreInspector.ParentDisplay(@"D:\Media\clip.mov", @"C:\Users\me"));
        string deep = ExploreInspector.ParentDisplay(@"D:\Archive\2019\Projects\Client work\Final renders\Exports\clip.mov", @"C:\Users\me");
        Assert.Equal(48, deep.Length);
        Assert.StartsWith(@"D:\Archive\2019\Pr…", deep);
        Assert.EndsWith(@"Final renders\Exports\", deep);
    }

    [Fact]
    public void WhatIsThisPrefersTheStorageClassThenTheComposition()
    {
        var cls = new StorageClass("caches", "Caches", "#000000", "Apps rebuild these.");
        var other = new StorageClass("other", "Other", "#000000", "Everything else.");
        Assert.Equal("This rebuilds by itself. Apps rebuild these.", ExploreInspector.WhatIsThisText(true, cls, "Mostly video."));
        Assert.Equal("This rebuilds by itself. Mostly video.",
            ExploreInspector.WhatIsThisText(true, cls with { Blurb = "" }, "Mostly video."));
        Assert.Equal("Apps rebuild these.", ExploreInspector.WhatIsThisText(false, cls, "Mostly video."));
        Assert.Equal("Mostly video.", ExploreInspector.WhatIsThisText(false, other, "Mostly video."));
    }
}
