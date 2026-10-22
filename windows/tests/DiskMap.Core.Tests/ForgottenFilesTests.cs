using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// Forgotten files (Mac: ForgottenFilesTests.swift) on Windows paths, plus the Windows
/// counterparts of the Mac's locations. Separator-agnostic: paths built by FileTree use the
/// host's separator under a Windows-style root.
/// </summary>
public class ForgottenFilesTests
{
    private const long MB = 1_000_000;
    private const long GB = 1_000_000_000;
    private const int Today = 20_000;

    private static string Win(string path) => path.Replace('/', '\\');

    private static (FileTree Tree, int User) UsersTree()
    {
        var tree = new FileTree();
        tree.AddNode("Users", -1, true, 0, 0, 0);
        int user = tree.AddNode("alex", 0, true, 0, 0, 0);
        return (tree, user);
    }

    private static List<ForgottenCandidate> Run(FileTree tree, string root = @"C:\Users") =>
        ForgottenFiles.Candidates(tree, root, tree.RollUpBoth().Allocated, today: Today, limit: 50, home: @"C:\Users\alex");

    // ---- Ported from the Mac suite ----

    [Fact]
    public void DownloadsOldVideoIsLikelyForgotten()
    {
        var (tree, user) = UsersTree();
        int downloads = tree.AddNode("Downloads", user, true, 0, 0, 0);
        int file = tree.AddNode("old-movie.mkv", downloads, false, 8 * GB, 8 * GB, Today - 800);
        var hit = Assert.Single(Run(tree), c => c.Id == file);
        Assert.Equal(ForgottenConfidence.LikelyForgotten, hit.Confidence);
        Assert.True(hit.IsReviewable);
        Assert.Equal(800, hit.AgeDays);
        Assert.Equal(@"~\Downloads\old-movie.mkv", Win(hit.DisplayPath));
        Assert.Equal(@"~\Downloads\", Win(hit.ParentDisplay));
        Assert.Equal(["Not modified in 2 years", "Very large file", "Located in Downloads", "Personal media file"], hit.Reasons);
    }

    /// <summary>Mac: ~/Library/Caches/Homebrew/downloads is a tool's cache. Windows: AppData.</summary>
    [Fact]
    public void ADownloadsFolderInsideAppDataIsNotPersonal()
    {
        var (tree, user) = UsersTree();
        int appData = tree.AddNode("AppData", user, true, 0, 0, 0);
        int local = tree.AddNode("Local", appData, true, 0, 0, 0);
        int tool = tree.AddNode("SomeTool", local, true, 0, 0, 0);
        int downloads = tree.AddNode("downloads", tool, true, 0, 0, 0);
        tree.AddNode("x.tar.gz", downloads, false, 2 * MB, 2 * MB, Today - 500);
        Assert.DoesNotContain(Run(tree), c => c.IsReviewable);
    }

    /// <summary>Mac: a framework inside an .app bundle. Windows: a file in Program Files.</summary>
    [Fact]
    public void ProgramFilesIsOldImportant()
    {
        var tree = new FileTree();
        tree.AddNode("Program Files", -1, true, 0, 0, 0);
        int app = tree.AddNode("FreeCAD", 0, true, 0, 0, 0);
        int file = tree.AddNode("model.weights", app, false, 140 * MB, 140 * MB, Today - 900);
        var candidates = Run(tree, @"C:\Program Files");
        var hit = Assert.Single(candidates, c => c.Id == file);
        Assert.Equal(ForgottenConfidence.OldImportant, hit.Confidence);
        Assert.False(hit.IsReviewable);
        Assert.Equal(0, ForgottenFiles.Summary(candidates).ReviewableBytes);
    }

    [Fact]
    public void SystemFileIsProtectedAndExcluded()
    {
        var tree = new FileTree();
        tree.AddNode("C:", -1, true, 0, 0, 0);
        int windows = tree.AddNode("Windows", 0, true, 0, 0, 0);
        int system = tree.AddNode("System32", windows, true, 0, 0, 0);
        int file = tree.AddNode("kernel.bin", system, false, 5 * GB, 5 * GB, Today - 1200);
        var hit = Assert.Single(Run(tree, "C:"), c => c.Id == file);
        Assert.Equal(ForgottenConfidence.OldImportant, hit.Confidence);
        Assert.Equal(SafetyLevel.Protected, hit.Safety.Level);
    }

    [Fact]
    public void RecentLargeFileIsNotForgotten()
    {
        var (tree, user) = UsersTree();
        int downloads = tree.AddNode("Downloads", user, true, 0, 0, 0);
        tree.AddNode("fresh.mkv", downloads, false, 20 * GB, 20 * GB, Today - 60);
        Assert.Empty(Run(tree));
    }

    [Fact]
    public void TinyOldFileIsBelowTheFloor()
    {
        var (tree, user) = UsersTree();
        int downloads = tree.AddNode("Downloads", user, true, 0, 0, 0);
        tree.AddNode("note.txt", downloads, false, 2000, 2000, Today - 3000);
        Assert.Empty(Run(tree));
    }

    [Fact]
    public void AgeDistributionOnlyUsesReviewableCandidates()
    {
        var (tree, user) = UsersTree();
        int downloads = tree.AddNode("Downloads", user, true, 0, 0, 0);
        tree.AddNode("a.mkv", downloads, false, 2 * GB, 2 * GB, Today - 400);
        tree.AddNode("b.mkv", downloads, false, 3 * GB, 3 * GB, Today - 40);
        var summary = ForgottenFiles.Summary(Run(tree));
        Assert.Equal(summary.ReviewableBytes, summary.AgeDistribution.Values.Sum());
        Assert.Equal(2 * GB, summary.ReviewableBytes);
        Assert.Equal(2 * GB, summary.AgeDistribution[ForgottenAgeBucket.OneToTwoYears]);
    }

    [Fact]
    public void CargoRegistryIsOldImportant()
    {
        var confidence = ForgottenFiles.ClassifyConfidence(
            @"C:\Users\alex\.cargo\registry\src\index.crates.io-xyz\windows-0.52.0\src\libwindows.0.52.0.a",
            "libwindows.0.52.0.a", FileKind.Other, new SafetyAssessment(SafetyLevel.Safe, "cache", "cache"), 12_700_000);
        Assert.Equal(ForgottenConfidence.OldImportant, confidence);
    }

    [Fact]
    public void DockerDiskIsOldImportant()
    {
        const string path = @"C:\Users\alex\AppData\Local\Docker\wsl\disk\docker_data.vhdx";
        var confidence = ForgottenFiles.ClassifyConfidence(path, "docker_data.vhdx", FileKind.VirtualDisk,
            SafetyClassifier.Assess(path, "docker_data.vhdx", false), 10 * GB);
        Assert.Equal(ForgottenConfidence.OldImportant, confidence);
    }

    // ---- Windows specifics ----

    [Fact]
    public void ARecycleBinFileIsNeverACandidate()
    {
        var tree = new FileTree();
        tree.AddNode("C:", -1, true, 0, 0, 0);
        int bin = tree.AddNode("$Recycle.Bin", 0, true, 0, 0, 0);
        int sid = tree.AddNode("S-1-5-21", bin, true, 0, 0, 0);
        tree.AddNode("$R0AB12.mkv", sid, false, 4 * GB, 4 * GB, Today - 800);
        Assert.Empty(Run(tree, "C:"));
    }

    [Fact]
    public void VideosAndLooseProfileFilesArePersonal()
    {
        var (tree, user) = UsersTree();
        int videos = tree.AddNode("Videos", user, true, 0, 0, 0);
        int clip = tree.AddNode("trip.mp4", videos, false, 600 * MB, 600 * MB, Today - 700);
        int loose = tree.AddNode("dump.bin", user, false, 60 * MB, 60 * MB, Today - 700);
        int worth = tree.AddNode("notes.bin", user, false, 30 * MB, 30 * MB, Today - 700);
        var candidates = Run(tree);
        var trip = Assert.Single(candidates, c => c.Id == clip);
        Assert.Equal(ForgottenConfidence.LikelyForgotten, trip.Confidence);
        Assert.Contains("Located in Videos", trip.Reasons);
        Assert.Equal(ForgottenConfidence.LikelyForgotten, Assert.Single(candidates, c => c.Id == loose).Confidence);
        Assert.Equal(ForgottenConfidence.WorthReviewing, Assert.Single(candidates, c => c.Id == worth).Confidence);
    }

    [Fact]
    public void BestFirstAndCapped()
    {
        var (tree, user) = UsersTree();
        int downloads = tree.AddNode("Downloads", user, true, 0, 0, 0);
        int small = tree.AddNode("small.zip", downloads, false, 5 * MB, 5 * MB, Today - 500);
        int big = tree.AddNode("big.zip", downloads, false, 5 * GB, 5 * GB, Today - 500);
        var candidates = ForgottenFiles.Candidates(tree, @"C:\Users", tree.RollUpBoth().Allocated, Today, limit: 1);
        Assert.Equal(big, Assert.Single(candidates).Id);
        Assert.DoesNotContain(candidates, c => c.Id == small);
    }

    [Theory]
    [InlineData(365, null)]
    [InlineData(366, ForgottenAgeBucket.OneToTwoYears)]
    [InlineData(730, ForgottenAgeBucket.TwoToThreeYears)]
    [InlineData(1095, ForgottenAgeBucket.ThreeToFiveYears)]
    [InlineData(1825, ForgottenAgeBucket.OverFiveYears)]
    public void AgeBuckets(int days, ForgottenAgeBucket? expected) =>
        Assert.Equal(expected, ForgottenText.Bucket(days));

    [Fact]
    public void ParentDisplayKeepsHomeAsTildeAndTruncates()
    {
        Assert.Equal(@"~\Downloads\", CanonicalPath.ParentDisplay(@"C:\Users\alex\Downloads\a.mkv", @"C:\Users\alex"));
        Assert.Equal(@"D:\Media\", CanonicalPath.ParentDisplay(@"D:\Media\a.mkv", @"C:\Users\alex"));
        string deep = CanonicalPath.ParentDisplay(@"D:\one\two\three\four\five\six\seven\eight\nine\ten\file.bin", @"C:\Users\alex");
        Assert.Equal(48, deep.Length);
        Assert.StartsWith(@"D:\one\two\three\f…", deep);
        Assert.EndsWith(@"\ten\", deep);
    }

    [Fact]
    public void CountedSearchSaysHowManyMatchedBeyondTheLimit()
    {
        var tree = new FileTree();
        tree.AddNode("root", -1, true, 0, 0, 0);
        for (int i = 0; i < 5; i++) tree.AddNode($"report-{i}.pdf", 0, false, (i + 1) * MB, (i + 1) * MB, 1);
        tree.AddNode("other.txt", 0, false, MB, MB, 1);
        var (ids, total) = FileSearch.SearchCounted(tree, tree.RollUpBoth().Allocated, "REPORT", limit: 2);
        Assert.Equal(5, total);
        Assert.Equal(2, ids.Count);
        Assert.Equal("report-4.pdf", tree.NameOf(ids[0]));
    }
}
