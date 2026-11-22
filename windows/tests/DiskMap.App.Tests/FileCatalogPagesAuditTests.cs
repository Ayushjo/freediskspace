using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>The Forgotten, Old downloads and Large media pages after the audit: bulk adds take only rows drawn, one rebuild per click.</summary>
public class FileCatalogPagesAuditTests
{
    private static ForgottenCandidate Candidate(int id, ForgottenConfidence confidence, SafetyLevel safety = SafetyLevel.Review) =>
        new(id, $"f{id}.zip", $@"C:\Users\x\Downloads\f{id}.zip", $@"~\Downloads\f{id}.zip", @"~\Downloads\", 1_000_000, 1, 800,
            FileKind.Archive, confidence, new SafetyAssessment(safety, "r", "f"), ["Not modified in 2 years"], 1);

    [Fact]
    public void ForgottensBulkAddsTakeOnlyTheRowsDrawn()
    {
        var shown = Enumerable.Range(1, CleanDetail.RowLimit)
            .Select(i => Candidate(i, i % 2 == 0 ? ForgottenConfidence.LikelyForgotten : ForgottenConfidence.WorthReviewing))
            .ToList();
        var (line, likely, add) = ForgottenExplorePage.FooterParts(shown, 450, _ => false);
        Assert.Equal("First 300 of 450 shown — narrow the filters for the rest", line);
        Assert.Equal(300, add.Count);   // the confirm sheet lists 400 at most, so it shows every one
        Assert.Equal(150, likely.Count);
        Assert.All(add, c => Assert.Contains(c, shown));
    }

    [Fact]
    public void ForgottensBulkAddsLeaveOutWhatsStagedProtectedOrLeftOut()
    {
        List<ForgottenCandidate> shown =
        [
            Candidate(1, ForgottenConfidence.LikelyForgotten), Candidate(2, ForgottenConfidence.LikelyForgotten),
            Candidate(3, ForgottenConfidence.WorthReviewing), Candidate(4, ForgottenConfidence.OldImportant),
            Candidate(5, ForgottenConfidence.WorthReviewing, SafetyLevel.Protected),
        ];
        var (line, likely, add) = ForgottenExplorePage.FooterParts(shown, 5, c => c.Id == 2);
        Assert.Equal($"5 shown · {ShellFormat.HeroBytes(5_000_000)}", line);
        Assert.Equal([1], likely.Select(c => c.Id));
        Assert.Equal([1, 3], add.Select(c => c.Id));
    }

    /// <summary>
    /// A click used to rebuild twice: the page's own Rebuild and the one StateChanged queues. The
    /// page now rebuilds itself only when the model raised nothing.
    /// </summary>
    [Fact]
    public void APageRebuildsItselfOnlyWhenTheModelDidNot()
    {
        var model = ScanModel.Shared;
        Assert.True(CleanDetailPage.ModelRedraws(() => model.ToggleMulti(987_654)));
        Assert.True(CleanDetailPage.ModelRedraws(model.ClearMulti));
        Assert.False(CleanDetailPage.ModelRedraws(model.ClearMulti));   // nothing left to clear
        Assert.False(CleanDetailPage.ModelRedraws(() => { }));
    }

    [Fact]
    public void RemixAudioIsNeverInLikelyDoneWith()
    {
        var tree = new FileTree();
        tree.AddNode("Users", -1, true, 0, 0, 0);
        int you = tree.AddNode("you", 0, true, 0, 0, 0);
        int music = tree.AddNode("Music", you, true, 0, 0, 0);
        tree.AddNode("Song (Remix).mp3", music, false, 600_000_000, 600_000_000, 1);
        var media = Assert.Single(MediaCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: 2_000).Candidates);
        // "Add N likely done with" takes only accented rows.
        Assert.False(CleanMediaPage.Row(media, @"C:\Users\you", 2_000).Accent);
    }
}
