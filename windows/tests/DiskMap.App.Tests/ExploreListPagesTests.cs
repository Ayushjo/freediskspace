using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>Folders, Forgotten and Find's words and orders (Mac: BiggestFoldersView, ForgottenFilesView, FindView).</summary>
public class ExploreListPagesTests
{
    // ---- Folders ----

    [Fact]
    public void FolderContentsAndFooterCounts()
    {
        Assert.Equal("1,204 files · 31 folders", BiggestFoldersExplorePage.Contents(1204, 31));
        Assert.Equal("1 file", BiggestFoldersExplorePage.Contents(1, 0));
        Assert.Equal("8 folders", BiggestFoldersExplorePage.FooterCount(8, 0));
        Assert.Equal("3 folders, 1 file", BiggestFoldersExplorePage.FooterCount(3, 1));
        Assert.Equal("0 files", BiggestFoldersExplorePage.FooterCount(0, 0));
    }

    [Fact]
    public void FoldersSortByKindPutsFoldersFirstThenExtension()
    {
        var tree = new FileTree();
        tree.AddNode("root", -1, true, 0, 0, 0);
        int b = tree.AddNode("b.zip", 0, false, 5, 5, 10);
        int a = tree.AddNode("a.mkv", 0, false, 9, 9, 20);
        int dir = tree.AddNode("Folder", 0, true, 0, 0, 5);
        var totals = tree.RollUpBoth().Allocated;
        var rows = tree.ChildrenOf(0, totals);
        Assert.Equal([dir, a, b], BiggestFoldersExplorePage.Sorted(rows, BiggestFoldersExplorePage.SortMode.Kind, tree).Select(r => r.Id));
        Assert.Equal([a, b, dir], BiggestFoldersExplorePage.Sorted(rows, BiggestFoldersExplorePage.SortMode.Size, tree).Select(r => r.Id));
        Assert.Equal([a, b, dir], BiggestFoldersExplorePage.Sorted(rows, BiggestFoldersExplorePage.SortMode.Changed, tree).Select(r => r.Id));
    }

    [Fact]
    public void NameOrderReadsNumbersByValue()
    {
        var tree = new FileTree();
        tree.AddNode("root", -1, true, 0, 0, 0);
        int ten = tree.AddNode("Clip 10", 0, true, 0, 0, 0);
        int two = tree.AddNode("clip 2", 0, true, 0, 0, 0);
        var rows = tree.ChildrenOf(0, tree.RollUpBoth().Allocated);
        Assert.Equal([two, ten], BiggestFoldersExplorePage.Sorted(rows, BiggestFoldersExplorePage.SortMode.Name, tree).Select(r => r.Id));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("D:", true)]
    [InlineData(@"C:\Users", false)]
    public void VolumeRoot(string root, bool expected) => Assert.Equal(expected, BiggestFoldersExplorePage.IsVolumeRoot(root));

    // ---- Forgotten ----

    private static ForgottenCandidate Candidate(int id, string name, long bytes, int age, ForgottenConfidence confidence,
        double score, string path) =>
        new(id, name, path, path, @"~\Downloads\", bytes, 1, age, FileKinds.Classify(name, path), confidence,
            new SafetyAssessment(SafetyLevel.Review, "r", name), ["Not modified in 2 years", "Very large file"], score);

    [Fact]
    public void ForgottenWords()
    {
        Assert.Equal("Very large file", ForgottenExplorePage.Why(["Not modified in 2 years", "Very large file"]));
        Assert.Equal("Worth a look", ForgottenExplorePage.ConfidenceLabel(ForgottenConfidence.WorthReviewing));
        Assert.Equal("45d ago", ForgottenExplorePage.AgeText(45));
        Assert.Equal("2 months ago", ForgottenExplorePage.AgeText(60));   // Mac: max(1, 60 / 30) months
        Assert.Equal("1 year ago", ForgottenExplorePage.AgeText(400));
        Assert.Equal("Very old", ForgottenExplorePage.AgeText(20 * 365));
    }

    [Fact]
    public void ForgottenFiltersAndSorts()
    {
        var all = new[]
        {
            Candidate(1, "a.mkv", 3_000_000_000, 800, ForgottenConfidence.LikelyForgotten, 50, @"C:\Users\x\Downloads\a.mkv"),
            Candidate(2, "b.bin", 40_000_000, 2000, ForgottenConfidence.WorthReviewing, 30, @"C:\Users\x\b.bin"),
            Candidate(3, "c.dll", 90_000_000, 900, ForgottenConfidence.OldImportant, -10, @"C:\Program Files\c.dll"),
        };
        var F = ForgottenExplorePage.Filter;
        Assert.Equal([1, 2], F(all, ForgottenExplorePage.FilterTab.All, null, false, false, false, "", ForgottenExplorePage.SortMode.ReviewValue).Select(c => c.Id));
        Assert.Equal([3], F(all, ForgottenExplorePage.FilterTab.Excluded, null, false, false, false, "", ForgottenExplorePage.SortMode.ReviewValue).Select(c => c.Id));
        Assert.Equal([2, 1], F(all, ForgottenExplorePage.FilterTab.All, null, false, false, false, "", ForgottenExplorePage.SortMode.Oldest).Select(c => c.Id));
        Assert.Equal([1], F(all, ForgottenExplorePage.FilterTab.All, null, true, false, false, "", ForgottenExplorePage.SortMode.Largest).Select(c => c.Id));
        Assert.Equal([1], F(all, ForgottenExplorePage.FilterTab.All, null, false, true, false, "", ForgottenExplorePage.SortMode.Largest).Select(c => c.Id));
        Assert.Equal([2], F(all, ForgottenExplorePage.FilterTab.All, ForgottenAgeBucket.OverFiveYears, false, false, false, "", ForgottenExplorePage.SortMode.Largest).Select(c => c.Id));
        Assert.Equal([2], F(all, ForgottenExplorePage.FilterTab.All, null, false, false, false, "B.B", ForgottenExplorePage.SortMode.Largest).Select(c => c.Id));
    }

    // ---- Find ----

    [Fact]
    public void FindTokensAndSummary()
    {
        Assert.Equal(["ext:mp4", "size>500MB"], FindExplorePage.Tokens("holiday ext:mp4  size>500MB"));
        Assert.Equal(["Searching…"], FindExplorePage.SummaryParts(true, 0, 0, 0));
        Assert.Equal(["1 match"], FindExplorePage.SummaryParts(false, 1, 1, null));
        Assert.Equal("2 matches", FindExplorePage.SummaryParts(false, 2, 2, 5_000_000_000)[0]);
        Assert.Equal("Showing the first 500 of 2,140 — narrow the query to see the rest.", FindExplorePage.FooterLine(500, 2140, 1));
        Assert.Equal("3 shown", FindExplorePage.FooterLine(3, 3, null));
    }

    [Fact]
    public void OneBareWordRunsThroughTheNameSearch()
    {
        FileQuery Q(string text) => FileQuery.Parse(text, @"C:\Users\x", @"C:\").Query;
        Assert.Equal(("node_modules", (FileQuery.NodeType?)FileQuery.NodeType.Folders),
            FindExplorePage.IndexNeedle(Q("type:folder node_modules"), FindExplorePage.FindSort.Largest));
        Assert.Null(FindExplorePage.IndexNeedle(Q("holiday"), FindExplorePage.FindSort.Oldest));
        Assert.Null(FindExplorePage.IndexNeedle(Q("holiday photos"), FindExplorePage.FindSort.Largest));
        Assert.Null(FindExplorePage.IndexNeedle(Q("holiday size>1GB"), FindExplorePage.FindSort.Largest));
    }
}
