using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// Old Downloads (Mac: OldDownloadsCatalogTests.swift) on Windows paths. Paths are compared
/// with '/' folded to '\' so the suite passes where Path.Join uses either separator.
/// </summary>
public class OldDownloadsCatalogTests
{
    private const long MB = 1_000_000;
    private const long GB = 1_000_000_000;

    private static string Win(string path) => path.Replace('/', '\\');

    private static (FileTree Tree, int Downloads) UsersTree()
    {
        var tree = new FileTree();
        int root = tree.AddNode("Users", -1, true, 0, 0, 1000);
        int user = tree.AddNode("alex", root, true, 0, 0, 1000);
        int dl = tree.AddNode("Downloads", user, true, 0, 0, 1000);
        return (tree, dl);
    }

    private static OldDownloadsCandidate Candidate(
        int id, string name, long bytes, int ageDays, FileKind kind, double score = 1) =>
        new(id, name, $@"C:\Users\x\Downloads\{name}", $@"~\Downloads\{name}", bytes, 1, ageDays,
            kind, OldDownloadsStatus.ReviewFirst, new SafetyAssessment(SafetyLevel.Review, "r", name),
            "w", "r", score);

    // ---- Ported from the Mac suite ----

    [Fact]
    public void FindsDownloadsFilesAndAges()
    {
        var (tree, dl) = UsersTree();
        int video = tree.AddNode("Dune.mkv", dl, false, 5 * GB, 5 * GB, 100);
        int dmg = tree.AddNode("App.dmg", dl, false, 800 * MB, 800 * MB, 50);
        tree.AddNode("notes.txt", dl, false, 1000, 1000, 900);
        var built = OldDownloadsCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: 1000);

        Assert.Contains(built.Candidates, c => c.NodeID == video);
        Assert.Contains(built.Candidates, c => c.NodeID == dmg);
        // tiny notes below floor
        Assert.DoesNotContain(built.Candidates, c => c.Name == "notes.txt");
        var dune = Assert.Single(built.Candidates, c => c.NodeID == video);
        Assert.Equal(FileKind.Video, dune.Kind);
        Assert.Equal(OldDownloadsStatus.ReviewFirst, dune.Status);
        Assert.Equal(900, dune.AgeDays);
        Assert.Equal(@"C:\Users\alex\Downloads\Dune.mkv", Win(dune.AbsolutePath));
        var installer = Assert.Single(built.Candidates, c => c.NodeID == dmg);
        Assert.Equal(OldDownloadsStatus.LikelyDisposable, installer.Status);
        Assert.True(built.Summary.TotalBytes >= 5_800 * MB);
        Assert.True(built.Summary.Bytes365 > 0);
    }

    /// <summary>
    /// Mac: Homebrew's own downloads cache (~/Library/Caches/Homebrew/downloads) listed as the
    /// user's Downloads, and the header counting the row cap as files. Windows: a tool's
    /// "downloads" folder under AppData or ProgramData.
    /// </summary>
    [Fact]
    public void AppDataDownloadsAreNotUserDownloadsAndTotalsIgnoreTheCap()
    {
        var tree = new FileTree();
        int root = tree.AddNode("Users", -1, true, 0, 0, 1000);
        int user = tree.AddNode("alex", root, true, 0, 0, 1000);
        int dl = tree.AddNode("Downloads", user, true, 0, 0, 1000);
        for (int k = 0; k < 5; k++)
            tree.AddNode($"f{k}.zip", dl, false, 2 * MB, 2 * MB, 900);
        int appData = tree.AddNode("AppData", user, true, 0, 0, 1000);
        int local = tree.AddNode("Local", appData, true, 0, 0, 1000);
        int tool = tree.AddNode("SomeTool", local, true, 0, 0, 1000);
        int toolDownloads = tree.AddNode("downloads", tool, true, 0, 0, 1000);
        tree.AddNode("bottle.tar.gz", toolDownloads, false, 9 * MB, 9 * MB, 900);

        var built = OldDownloadsCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated,
            today: 1000, limit: 3);
        Assert.DoesNotContain(built.Candidates, c => c.Name == "bottle.tar.gz");
        Assert.Equal(3, built.Candidates.Count);
        Assert.Equal(5, built.Summary.TotalCount);
        Assert.Equal(10 * MB, built.Summary.TotalBytes);
    }

    [Fact]
    public void FiltersByAgeAndType()
    {
        var items = new[]
        {
            Candidate(1, "a.mkv", 2 * GB, 400, FileKind.Video),
            Candidate(2, "b.dmg", 500 * MB, 40, FileKind.DiskImage),
        };
        var year = OldDownloadsCatalog.Filter(items, OldDownloadsAgeFilter.Year1, OldDownloadsSizeFilter.Any,
            OldDownloadsTypeFilter.All, "");
        Assert.Equal("a.mkv", Assert.Single(year).Name);
        var videos = OldDownloadsCatalog.Filter(items, OldDownloadsAgeFilter.All, OldDownloadsSizeFilter.Any,
            OldDownloadsTypeFilter.Video, "");
        Assert.Single(videos);
    }

    [Theory]
    [InlineData("/Users/a/Downloads/x.mkv", true)]
    [InlineData("/Users/a/Documents/x.mkv", false)]
    [InlineData(@"C:\Users\a\Downloads\x.mkv", true)]
    [InlineData(@"C:\Users\a\Downloads", true)]
    [InlineData(@"D:\downloads-old\x.zip", true)]   // Mac rule: any component starting "downloads"
    [InlineData(@"C:\Users\a\Documents\x.mkv", false)]
    public void IsUnderDownloads(string path, bool expected) =>
        Assert.Equal(expected, OldDownloadsCatalog.IsUnderDownloads(path));

    // ---- Windows cases ----

    [Fact]
    public void WindowsInstallersAreTheInstallerTypeAndGoStaleAfter90Days()
    {
        var (tree, dl) = UsersTree();
        int oldExe = tree.AddNode("VSCodeUserSetup-x64.exe", dl, false, 100 * MB, 100 * MB, 800);
        int oldMsi = tree.AddNode("node-v20.msi", dl, false, 30 * MB, 30 * MB, 850);
        int newMsix = tree.AddNode("App.msixbundle", dl, false, 60 * MB, 60 * MB, 990);
        int iso = tree.AddNode("Win11_24H2.iso", dl, false, 6 * GB, 6 * GB, 500);
        int zipSetup = tree.AddNode("driver-setup.zip", dl, false, 20 * MB, 20 * MB, 700);
        int zipYoung = tree.AddNode("driver-setup2.zip", dl, false, 20 * MB, 20 * MB, 900);
        int doc = tree.AddNode("Tax 2024.pdf", dl, false, 3 * MB, 3 * MB, 100);
        var built = OldDownloadsCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: 1000);
        var byId = built.Candidates.ToDictionary(c => c.NodeID);

        foreach (int id in new[] { oldExe, oldMsi, newMsix, iso })
            Assert.Equal(FileKind.DiskImage, byId[id].Kind);
        Assert.Equal(OldDownloadsStatus.LikelyDisposable, byId[oldExe].Status);
        Assert.Equal(OldDownloadsStatus.LikelyDisposable, byId[oldMsi].Status);
        Assert.Equal(OldDownloadsStatus.ReviewFirst, byId[newMsix].Status);   // 10 days old
        Assert.Equal(OldDownloadsStatus.LikelyDisposable, byId[iso].Status);
        Assert.Equal(OldDownloadsStatus.LikelyDisposable, byId[zipSetup].Status);  // 300 days, "setup"
        Assert.Equal(OldDownloadsStatus.ReviewFirst, byId[zipYoung].Status);       // 100 days
        Assert.Equal(OldDownloadsStatus.ReviewFirst, byId[doc].Status);
        Assert.Equal(SafetyLevel.Review, byId[oldExe].Safety.Level);

        var installers = OldDownloadsCatalog.Filter(built.Candidates, OldDownloadsAgeFilter.All,
            OldDownloadsSizeFilter.Any, OldDownloadsTypeFilter.Installer, "");
        Assert.Equal(new[] { iso, oldExe, newMsix, oldMsi }, installers.Select(c => c.NodeID));

        Assert.Equal(
            "If the software is already installed and you don’t need the installer again, add it to Cleanup and review it before removing.",
            byId[oldExe].Recommendation);
        Assert.Equal("Installer or disk image (100 MB) in Downloads, last modified 6mo ago.", byId[oldExe].WhyHere);
        Assert.Equal(
            "This looks like a personal file. freedisk.space can’t determine whether you still need it. Review before removing.",
            byId[doc].Recommendation);
        Assert.Equal("Document (3 MB) in Downloads, last modified 2y 5m ago. Review carefully — it may still matter.",
            byId[doc].WhyHere);
    }

    [Fact]
    public void SkipsTheRecycleBinAndWindowsItself()
    {
        // Root "C:" rather than "C:\": the same paths on Windows, and on a non-Windows test
        // host Path.Join doesn't produce "C:\/Windows".
        var tree = new FileTree();
        int root = tree.AddNode("C:", -1, true, 0, 0, 1000);
        int bin = tree.AddNode("$Recycle.Bin", root, true, 0, 0, 1000);
        int sid = tree.AddNode("S-1-5-21-1000", bin, true, 0, 0, 1000);
        int binnedDownloads = tree.AddNode("Downloads", sid, true, 0, 0, 1000);
        tree.AddNode("binned.zip", binnedDownloads, false, 50 * MB, 50 * MB, 100);
        int windows = tree.AddNode("Windows", root, true, 0, 0, 1000);
        int winDownloads = tree.AddNode("Downloads", windows, true, 0, 0, 1000);
        tree.AddNode("system.cab", winDownloads, false, 50 * MB, 50 * MB, 100);
        int programData = tree.AddNode("ProgramData", root, true, 0, 0, 1000);
        int choco = tree.AddNode("chocolatey", programData, true, 0, 0, 1000);
        int chocoDownloads = tree.AddNode("downloads", choco, true, 0, 0, 1000);
        tree.AddNode("pkg.nupkg", chocoDownloads, false, 50 * MB, 50 * MB, 100);
        int users = tree.AddNode("Users", root, true, 0, 0, 1000);
        int alex = tree.AddNode("alex", users, true, 0, 0, 1000);
        int dl = tree.AddNode("Downloads", alex, true, 0, 0, 1000);
        int kept = tree.AddNode("movie.mp4", dl, false, 50 * MB, 50 * MB, 100);

        var built = OldDownloadsCatalog.Build(tree, "C:", tree.RollUpBoth().Allocated, today: 1000);
        Assert.Equal(kept, Assert.Single(built.Candidates).NodeID);
    }

    [Fact]
    public void DisplayPathShowsTheProfileAsTilde()
    {
        var (tree, dl) = UsersTree();
        tree.AddNode("Dune.mkv", dl, false, 5 * GB, 5 * GB, 100);
        var built = OldDownloadsCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated,
            today: 1000, home: @"C:\Users\alex");
        var dune = Assert.Single(built.Candidates);
        Assert.Equal(@"~\Downloads\Dune.mkv", Win(dune.DisplayPath));
        // The query also searches the display path.
        Assert.Single(OldDownloadsCatalog.Filter(built.Candidates, OldDownloadsAgeFilter.All,
            OldDownloadsSizeFilter.Any, OldDownloadsTypeFilter.All, "  DOWNLOADS "));
        // Another profile is left as is.
        Assert.Equal(@"C:\Users\alexandra\x", CanonicalPath.DisplayPath(@"C:\Users\alexandra\x", @"C:\Users\alex"));
        Assert.Equal("~", CanonicalPath.DisplayPath(@"c:\users\ALEX", @"C:\Users\alex\"));
    }

    [Fact]
    public void LooseFilesNamedDownloadsCountEvenOutsideTheFolder()
    {
        var tree = new FileTree();
        int root = tree.AddNode("Users", -1, true, 0, 0, 1000);
        int user = tree.AddNode("alex", root, true, 0, 0, 1000);
        int desk = tree.AddNode("Desktop", user, true, 0, 0, 1000);
        int loose = tree.AddNode("Downloads backup.zip", desk, false, 5 * MB, 5 * MB, 900);
        tree.AddNode("other.zip", desk, false, 5 * MB, 5 * MB, 900);
        var built = OldDownloadsCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: 1000);
        Assert.Equal(loose, Assert.Single(built.Candidates).NodeID);
    }

    [Fact]
    public void TodayDefaultsToTheCurrentDate()
    {
        var (tree, dl) = UsersTree();
        int today = AgeMap.Today();
        tree.AddNode("a.zip", dl, false, 5 * MB, 5 * MB, today - 40);
        tree.AddNode("undated.zip", dl, false, 5 * MB, 5 * MB, 0);
        var built = OldDownloadsCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated);
        Assert.InRange(built.Candidates.Single(c => c.Name == "a.zip").AgeDays, 40, 41);
        Assert.Equal(0, built.Candidates.Single(c => c.Name == "undated.zip").AgeDays);
    }

    [Fact]
    public void SummaryBucketsAndInsights()
    {
        var items = new List<OldDownloadsCandidate>
        {
            Candidate(1, "a.mkv", 4 * GB, 800, FileKind.Video),
            Candidate(2, "b.mp4", 1 * GB, 10, FileKind.Video),
            Candidate(3, "c.zip", 500 * MB, 400, FileKind.Archive),
            Candidate(4, "d.iso", 2 * GB, 100, FileKind.DiskImage),
        };
        var s = OldDownloadsCatalog.Summarize(items);
        Assert.Equal(7_500 * MB, s.TotalBytes);
        Assert.Equal(4, s.TotalCount);
        Assert.Equal(4, s.ReviewCount);
        Assert.Equal((6_500 * MB, 3), (s.Bytes30, s.Count30));
        Assert.Equal((6_500 * MB, 3), (s.Bytes90, s.Count90));
        Assert.Equal((4_500 * MB, 2), (s.Bytes365, s.Count365));
        Assert.Equal(
            [("<30 days", 1 * GB, 1), ("90d–1 year", 2 * GB, 1), ("1–2 years", 500 * MB, 1), ("2+ years", 4 * GB, 1)],
            s.AgeBuckets.Select(b => (b.Title, b.Bytes, b.Count)));
        Assert.Equal([FileKind.Video, FileKind.DiskImage, FileKind.Archive], s.TypeBuckets.Select(b => b.Kind));
        Assert.Equal("diskImage", s.TypeBuckets[1].Id);
        Assert.Equal(
        [
            "Downloads has 7.5 GB of files matching this view.",
            "5 GB comes from 2 large video files.",
            "4.5 GB hasn’t been modified in over a year.",
            "Nothing here is automatically considered safe to delete.",
        ], s.InsightLines);
        Assert.Equal(["Nothing here is automatically considered safe to delete."],
            OldDownloadsCatalog.Summarize([]).InsightLines);
    }

    [Fact]
    public void SortsLikeTheMac()
    {
        var items = new[]
        {
            Candidate(1, "file10.zip", 3 * MB, 5, FileKind.Archive, score: 2),
            Candidate(2, "File9.zip", 1 * MB, 50, FileKind.Archive, score: 9),
            Candidate(3, "alpha.zip", 2 * MB, 20, FileKind.Archive, score: 5),
        };
        Assert.Equal([1, 3, 2], OldDownloadsCatalog.Sorted(items, OldDownloadsSort.Largest).Select(c => c.NodeID));
        Assert.Equal([2, 3, 1], OldDownloadsCatalog.Sorted(items, OldDownloadsSort.Oldest).Select(c => c.NodeID));
        Assert.Equal([1, 3, 2], OldDownloadsCatalog.Sorted(items, OldDownloadsSort.Newest).Select(c => c.NodeID));
        Assert.Equal([3, 2, 1], OldDownloadsCatalog.Sorted(items, OldDownloadsSort.Name).Select(c => c.NodeID));
        Assert.Equal([2, 3, 1], OldDownloadsCatalog.Sorted(items, OldDownloadsSort.Smart).Select(c => c.NodeID));
    }

    [Fact]
    public void ScoreWeighsSizeAgeAndDisposability()
    {
        var (tree, dl) = UsersTree();
        tree.AddNode("a.mkv", dl, false, 300 * MB, 300 * MB, 910);   // 90 days, review first
        tree.AddNode("b.iso", dl, false, 300 * MB, 300 * MB, 910);   // 90 days, likely disposable
        var built = OldDownloadsCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: 1000);
        Assert.Equal(1200, built.Candidates.Single(c => c.Name == "a.mkv").Score, 6);
        Assert.Equal(1680, built.Candidates.Single(c => c.Name == "b.iso").Score, 6);
    }

    [Theory]
    [InlineData(0, "unknown age")]
    [InlineData(29, "29d ago")]
    [InlineData(30, "1mo ago")]
    [InlineData(364, "12mo ago")]
    [InlineData(365, "1y ago")]
    [InlineData(400, "1y 1m ago")]
    [InlineData(800, "2y 2m ago")]
    public void AgeLabels(int days, string expected) => Assert.Equal(expected, OldDownloadsCatalog.AgeLabel(days));

    [Fact]
    public void FilterTitlesAndThresholdsMatchTheMac()
    {
        Assert.Equal(["All", "30d+", "90d+", "6mo+", "1y+"], Enum.GetValues<OldDownloadsAgeFilter>().Select(f => f.Title()));
        Assert.Equal([0, 30, 90, 180, 365], Enum.GetValues<OldDownloadsAgeFilter>().Select(f => f.MinAgeDays()));
        Assert.Equal(["Any", ">100 MB", ">500 MB", ">1 GB", ">5 GB"], Enum.GetValues<OldDownloadsSizeFilter>().Select(f => f.Title()));
        Assert.Equal(["All", "Videos", "Archives", "Installers", "Documents", "Other"],
            Enum.GetValues<OldDownloadsTypeFilter>().Select(f => f.Title()));
        Assert.Equal(["Largest first", "Oldest first", "Newest first", "Name", "Smart (space × age)"],
            Enum.GetValues<OldDownloadsSort>().Select(f => f.Title()));
        Assert.True(OldDownloadsTypeFilter.Other.Matches(FileKind.VirtualDisk));
        Assert.False(OldDownloadsTypeFilter.Other.Matches(FileKind.DiskImage));
    }

    [Fact]
    public void MismatchedTotalsReturnEmpty()
    {
        var (tree, _) = UsersTree();
        Assert.Same(OldDownloadsCatalogResult.Empty, OldDownloadsCatalog.Build(tree, @"C:\Users", [1, 2]));
    }
}

/// <summary>The Windows FileKind rules (Mac: FileKind.classify).</summary>
public class FileKindTests
{
    [Theory]
    [InlineData("Dune.MKV", "", FileKind.Video)]
    [InlineData("App.dmg", "", FileKind.DiskImage)]
    [InlineData("setup.exe", @"C:\Users\a\Downloads\setup.exe", FileKind.DiskImage)]
    [InlineData("code.exe", @"C:\Program Files\Microsoft VS Code\code.exe", FileKind.Application)]
    [InlineData("game.exe", @"C:\Program Files (x86)\Game\game.exe", FileKind.Application)]
    [InlineData("app.exe", @"C:\Users\a\AppData\Local\Programs\App\app.exe", FileKind.Application)]
    [InlineData("resources.pak", @"C:\Program Files\App\resources.pak", FileKind.Application)]
    [InlineData("node.msi", "", FileKind.DiskImage)]
    [InlineData("App.msix", "", FileKind.DiskImage)]
    [InlineData("ext4.vhdx", "", FileKind.VirtualDisk)]
    [InlineData("Docker.raw", "", FileKind.VirtualDisk)]
    [InlineData("src.7z", "", FileKind.Archive)]
    [InlineData("data.sqlite", "", FileKind.Database)]
    [InlineData("Manifest.db", @"C:\Users\a\Apple\MobileSync\Backup\0000-1111\Manifest.db", FileKind.DeviceBackup)]
    [InlineData("3d4f", @"C:\Users\a\AppData\Roaming\Apple Computer\MobileSync\Backup\abc\3d\3d4f", FileKind.DeviceBackup)]
    [InlineData("Report.docx", "", FileKind.Document)]
    [InlineData("thing.bin", "", FileKind.Other)]
    public void Classifies(string name, string path, FileKind expected) =>
        Assert.Equal(expected, FileKinds.Classify(name, path));
}
