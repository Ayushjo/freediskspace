using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The audit's file-catalog fixes: what Forgotten files, Old downloads and Large media may call
/// likely forgotten or done with — the verdicts behind their one-click bulk adds — and what they
/// must never list.
/// </summary>
public class FileCatalogAuditTests
{
    private const long MB = 1_000_000;
    private const long GB = 1_000_000_000;
    private const int Today = 20_000;
    private static readonly (string Folder, string Path)[] NoKnownFolders = [];
    private static readonly SafetyAssessment Review = new(SafetyLevel.Review, "r", "file");

    /// <summary>C:\Users\alex with these folders in it.</summary>
    private static (FileTree Tree, Dictionary<string, int> Folders) Profile(params string[] folders)
    {
        var tree = new FileTree();
        tree.AddNode("Users", -1, true, 0, 0, 0);
        int alex = tree.AddNode("alex", 0, true, 0, 0, 0);
        return (tree, folders.ToDictionary(f => f, f => tree.AddNode(f, alex, true, 0, 0, 0)));
    }

    private static List<ForgottenCandidate> Forgotten(FileTree tree, string root,
        IReadOnlyList<(string Folder, string Path)>? known = null, long[]? totals = null) =>
        ForgottenFiles.Candidates(tree, root, totals ?? tree.RollUpBoth().Allocated, today: Today, limit: 50,
            home: @"C:\Users\alex", knownFolders: known ?? NoKnownFolders);

    private static ForgottenConfidence Confidence(string path, FileKind kind, long bytes,
        IReadOnlyList<(string Folder, string Path)>? known = null) =>
        ForgottenFiles.ClassifyConfidence(path, Path.GetFileName(path), kind, Review, bytes, known ?? NoKnownFolders);

    // ---- 1a: a video keeps the date it was made ----

    [Theory]
    [InlineData(FileKind.Video, @"C:\Users\alex\Videos\trip.mp4")]
    [InlineData(FileKind.Video, @"C:\Users\alex\Downloads\movie.mkv")]
    [InlineData(FileKind.DeviceBackup, @"C:\Users\alex\Documents\iPhone backup\phone.backup")]
    public void OldVideosAndDeviceBackupsAreWorthALookAtMost(FileKind kind, string path)
    {
        Assert.Equal(ForgottenConfidence.WorthReviewing, Confidence(path, kind, 4 * GB));
        // Small ones stay out, like any other medium-confidence file under 25 MB.
        Assert.Equal(ForgottenConfidence.OldImportant, Confidence(path, kind, 5 * MB));
    }

    [Fact]
    public void OldArchivesAndInstallersInAProfileFolderAreStillLikelyForgotten()
    {
        Assert.Equal(ForgottenConfidence.LikelyForgotten, Confidence(@"C:\Users\alex\Downloads\old.zip", FileKind.Archive, 5 * MB));
        Assert.Equal(ForgottenConfidence.LikelyForgotten, Confidence(@"C:\Users\alex\Desktop\setup.iso", FileKind.DiskImage, 5 * MB));
        Assert.Equal(ForgottenConfidence.LikelyForgotten, Confidence(@"C:\Users\alex\Documents\dump.bin", FileKind.Other, 60 * MB));
    }

    [Fact]
    public void TheLikelyForgottenBulkAddNeverTakesAVideo()
    {
        var (tree, f) = Profile("Videos", "Downloads");
        tree.AddNode("wedding.mp4", f["Videos"], false, 9 * GB, 9 * GB, Today - 2000);
        int zip = tree.AddNode("old.zip", f["Downloads"], false, 2 * GB, 2 * GB, Today - 900);
        var likely = Forgotten(tree, @"C:\Users").Where(c => c.Confidence == ForgottenConfidence.LikelyForgotten);
        Assert.Equal([zip], likely.Select(c => c.Id));
    }

    // ---- 1b: a folder name counts only right under a profile ----

    [Theory]
    [InlineData(@"D:\Games\X\Data\Videos\intro.zip")]
    [InlineData(@"D:\site\public\dump.zip")]
    [InlineData(@"D:\Backup\Downloads\old.zip")]
    [InlineData(@"E:\Music\old.zip")]
    [InlineData(@"C:\Users\alex\Projects\Videos\old.zip")]
    [InlineData(@"C:\Users\alex\AppData\Local\Tool\Downloads\old.zip")]
    public void APersonalFolderNameAnywhereElseIsNotPersonal(string path) =>
        Assert.Equal(ForgottenConfidence.OldImportant, Confidence(path, FileKind.Archive, 60 * MB));

    [Theory]
    [InlineData(@"C:\Users\alex\Downloads\old.zip")]
    [InlineData(@"C:\Users\alex\Music\Rips\old.zip")]
    [InlineData(@"C:\Users\Public\Videos\old.zip")]
    [InlineData(@"D:\Users\alex\Documents\old.zip")]
    [InlineData(@"C:\Users\alex\old.zip")]   // a loose file in the profile itself
    [InlineData("/Users/alex/Movies/old.zip")]
    public void AFolderRightUnderAProfileIsPersonal(string path) =>
        Assert.Equal(ForgottenConfidence.LikelyForgotten, Confidence(path, FileKind.Archive, 60 * MB));

    [Fact]
    public void TheProfilesKnownFoldersArePersonalWhereverTheyAre()
    {
        (string, string)[] known = [("documents", @"D:\Docs"), ("downloads", @"C:\Users\alex\OneDrive\DL\")];
        Assert.Equal(ForgottenConfidence.LikelyForgotten, Confidence(@"D:\Docs\Taxes\old.zip", FileKind.Archive, 60 * MB, known));
        Assert.Equal(ForgottenConfidence.LikelyForgotten, Confidence(@"c:\users\ALEX\onedrive\dl\old.zip", FileKind.Archive, 60 * MB, known));
        Assert.Equal(ForgottenConfidence.OldImportant, Confidence(@"D:\Docs2\old.zip", FileKind.Archive, 60 * MB, known));
        Assert.Equal(ForgottenConfidence.OldImportant, Confidence(@"D:\Docs\Taxes\old.zip", FileKind.Archive, 60 * MB));

        var tree = new FileTree();
        tree.AddNode("D:", -1, true, 0, 0, 0);
        int docs = tree.AddNode("Docs", 0, true, 0, 0, 0);
        int file = tree.AddNode("old.zip", docs, false, 60 * MB, 60 * MB, Today - 900);
        var hit = Assert.Single(Forgotten(tree, "D:", known), c => c.Id == file);
        Assert.Equal(ForgottenConfidence.LikelyForgotten, hit.Confidence);
        Assert.Contains("Located in Documents", hit.Reasons);
    }

    [Fact]
    public void TheLocationReasonNamesTheProfileFolderNotADeeperOne()
    {
        var (tree, f) = Profile("Documents");
        int inner = tree.AddNode("Downloads", f["Documents"], true, 0, 0, 0);
        int file = tree.AddNode("old.zip", inner, false, 2 * GB, 2 * GB, Today - 900);
        var hit = Assert.Single(Forgotten(tree, @"C:\Users"), c => c.Id == file);
        Assert.Contains("Located in Documents", hit.Reasons);
        Assert.DoesNotContain("Located in Downloads", hit.Reasons);
    }

    /// <summary>Verify, don't assume: the shell's real Downloads folder comes back through the P/Invoke.</summary>
    [Fact]
    public void KnownFoldersComeFromTheShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        var known = ForgottenFiles.KnownFolders();
        var downloads = Assert.Single(known, k => k.Folder == "downloads").Path;
        Assert.True(Path.IsPathFullyQualified(downloads), downloads);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Assert.Single(known, k => k.Folder == "documents").Path);
    }

    // ---- 1c: a copy keeps an old modified date ----

    [Fact]
    public void AgeCountsFromTheLaterOfModifiedAndCreated()
    {
        var (tree, f) = Profile("Downloads");
        // Copied or extracted yesterday, still carrying 2019's modified date.
        int copy = tree.AddNode("photos-2019.zip", f["Downloads"], false, 2 * GB, 2 * GB, Today - 2000,
            createdDaysSinceEpoch: Today - 1);
        int old = tree.AddNode("old.zip", f["Downloads"], false, 2 * GB, 2 * GB, Today - 2000,
            createdDaysSinceEpoch: Today - 1900);
        int undated = tree.AddNode("undated.zip", f["Downloads"], false, 2 * GB, 2 * GB, Today - 800);

        var forgotten = Forgotten(tree, @"C:\Users");
        Assert.DoesNotContain(forgotten, c => c.Id == copy);
        Assert.Equal(1900, Assert.Single(forgotten, c => c.Id == old).AgeDays);
        Assert.Equal(800, Assert.Single(forgotten, c => c.Id == undated).AgeDays);   // created unknown: modified

        var downloads = OldDownloadsCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: Today).Candidates;
        var fresh = Assert.Single(downloads, c => c.NodeID == copy);
        Assert.Equal(1, fresh.AgeDays);
        Assert.Equal(Today - 1, fresh.ModifiedDay);
        Assert.Equal(1900, Assert.Single(downloads, c => c.NodeID == old).AgeDays);
        Assert.DoesNotContain(OldDownloadsCatalog.Filter(downloads, OldDownloadsAgeFilter.Days30, OldDownloadsSizeFilter.Any,
            OldDownloadsTypeFilter.All, ""), c => c.NodeID == copy);
    }

    // ---- 2: a name is no sign audio is scratch ----

    [Theory]
    [InlineData("Song (Remix).mp3")]
    [InlineData("Mixtape 2.mp3")]
    [InlineData("final bounce.wav")]
    [InlineData("render v3.flac")]
    [InlineData("scratch take.m4a")]
    public void AudioIsAlwaysReviewFirst(string name)
    {
        Assert.Equal(MediaStatus.ReviewFirst, MediaCatalog.ClassifyStatus(MediaKind.Audio, 900, name));
        var (tree, f) = Profile("Music");
        tree.AddNode(name, f["Music"], false, 600 * MB, 600 * MB, Today - 900);
        var hit = Assert.Single(MediaCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: Today).Candidates);
        Assert.Equal(MediaStatus.ReviewFirst, hit.Status);
    }

    // ---- 3: Downloads is a folder name, not a prefix ----

    [Theory]
    [InlineData("Downloads", true)]
    [InlineData("downloads", true)]
    [InlineData("Downloads (2)", true)]
    [InlineData("Downloads-old", true)]
    [InlineData("Downloads_2019", true)]
    [InlineData("Downloads backup.zip", true)]
    [InlineData("DownloadService", false)]
    [InlineData("downloadstats.csv", false)]
    [InlineData("Downloader", false)]
    [InlineData("MyDownloads", false)]
    public void IsDownloadsName(string name, bool expected) =>
        Assert.Equal(expected, OldDownloadsCatalog.IsDownloadsName(name));

    [Theory]
    [InlineData(@"C:\src\DownloadService\bin", false)]
    [InlineData(@"C:\Users\a\Downloadsfolder", false)]
    [InlineData(@"D:\Downloads (2)", true)]
    [InlineData(@"D:\Downloads-old\x.zip", true)]
    public void IsUnderDownloadsReadsWholeComponents(string path, bool expected) =>
        Assert.Equal(expected, OldDownloadsCatalog.IsUnderDownloads(path));

    [Fact]
    public void OnlyDownloadsFoldersFeedOldDownloads()
    {
        var tree = new FileTree();
        tree.AddNode("C:", -1, true, 0, 0, 0);
        int src = tree.AddNode("src", 0, true, 0, 0, 0);
        int service = tree.AddNode("DownloadService", src, true, 0, 0, 0);
        tree.AddNode("build.zip", service, false, 50 * MB, 50 * MB, 100);
        tree.AddNode("downloadstats.csv", src, false, 50 * MB, 50 * MB, 100);
        int second = tree.AddNode("Downloads (2)", 0, true, 0, 0, 0);
        int kept = tree.AddNode("movie.mp4", second, false, 50 * MB, 50 * MB, 100);
        var built = OldDownloadsCatalog.Build(tree, "C:", tree.RollUpBoth().Allocated, today: 1000);
        Assert.Equal([kept], built.Candidates.Select(c => c.NodeID));
    }

    // ---- 4: online-only cloud files ----

    /// <summary>
    /// With Size set to Logical an online-only OneDrive file counts at its cloud size; recycling it
    /// frees nothing here and removes it from OneDrive everywhere. None of the three lists it.
    /// </summary>
    [Fact]
    public void OnlineOnlyFilesAreNeverListed()
    {
        var (tree, f) = Profile("Downloads", "Videos");
        tree.AddNode("cloud.zip", f["Downloads"], false, 2 * GB, 0, Today - 900, flags: NodeFlags.NotDownloaded);
        tree.AddNode("cloud.mp4", f["Videos"], false, 2 * GB, 0, Today - 900, flags: NodeFlags.NotDownloaded);
        int zip = tree.AddNode("local.zip", f["Downloads"], false, 2 * GB, 2 * GB, Today - 900);
        int video = tree.AddNode("local.mp4", f["Videos"], false, 2 * GB, 2 * GB, Today - 900);
        var logical = tree.RollUpSizes(SizeBasis.Logical);

        Assert.Equal([zip, video], Forgotten(tree, @"C:\Users", totals: logical).Select(c => c.Id).Order());
        Assert.Equal([zip], OldDownloadsCatalog.Build(tree, @"C:\Users", logical, today: Today).Candidates.Select(c => c.NodeID));
        Assert.Equal([video], MediaCatalog.Build(tree, @"C:\Users", logical, today: Today).Candidates.Select(c => c.NodeID));
    }

    // ---- 5: the detail pages' footer counts only the rows drawn ----

    [Fact]
    public void PastTheRowLimitTheFooterAndItsAddCountOnlyTheRowsDrawn()
    {
        var rows = Enumerable.Range(0, 2_000).Select(i => new CleanDetailRow($"{i}", i + 1, $"f{i}.zip", $@"C:\d\f{i}.zip",
            "d", "ZIP", "Look first", MB, "1 year ago", "", false, [new CleanDetailRequest(i + 1, "r")], [$@"C:\d\f{i}.zip"]));
        var shown = rows.Take(CleanDetail.RowLimit).ToList();
        Assert.Equal("Largest 300 of 2,000 shown — narrow the filters for the rest", CleanDetail.FooterLine(shown, 2_000));
        var add = CleanDetail.Bulk(shown, _ => true);
        Assert.Equal("Add 300 shown · 300 MB", CleanDetail.ShownBulkTitle(add.Count, add.Sum(r => r.Bytes)));
    }
}

/// <summary>The demo PC (DISKMAP_HOME) reads as a profile, and nothing comes from this PC's own folders.</summary>
[Collection("ProcessEnvironment")]
public class FileCatalogDemoProfileTests
{
    [Fact]
    public void TheDemoHomeIsAProfile()
    {
        string? previous = Environment.GetEnvironmentVariable(UserFolders.OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, @"C:\DemoPC");
            Assert.Empty(ForgottenFiles.KnownFolders());
            var tree = new FileTree();
            tree.AddNode("DemoPC", -1, true, 0, 0, 0);
            int downloads = tree.AddNode("Downloads", 0, true, 0, 0, 0);
            tree.AddNode("old.zip", downloads, false, 2_000_000_000, 2_000_000_000, 20_000 - 900);
            var hit = Assert.Single(ForgottenFiles.Candidates(tree, @"C:\DemoPC", tree.RollUpBoth().Allocated, today: 20_000));
            Assert.Equal(ForgottenConfidence.LikelyForgotten, hit.Confidence);
            Assert.Contains("Located in Downloads", hit.Reasons);
        }
        finally { Environment.SetEnvironmentVariable(UserFolders.OverrideVariable, previous); }
    }
}
