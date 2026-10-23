using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// Large Media (Mac: MediaCatalogTests.swift) on Windows paths. Paths are compared with '/'
/// folded to '\' so the suite passes where Path.Join uses either separator.
/// </summary>
public class MediaCatalogTests
{
    private const long MB = 1_000_000;
    private const long GB = 1_000_000_000;

    private static string Win(string path) => path.Replace('/', '\\');

    private static MediaCandidate Candidate(
        int id, string name, string absolutePath, long bytes, int ageDays, MediaKind kind,
        MediaLocationBucket location, double score = 1) =>
        new(id, name, absolutePath, CanonicalPath.DisplayPath(absolutePath, @"C:\Users\x"), bytes, 1, ageDays,
            kind, location, MediaStatus.ReviewFirst, new SafetyAssessment(SafetyLevel.Review, "r", name),
            "w", "r", false, score);

    // ---- Ported from the Mac suite ----

    [Fact]
    public void ClassifiesMediaAndExcludesDiskImages()
    {
        Assert.Equal(MediaKind.Video, MediaCatalog.Classify("Dune.mkv"));
        Assert.Equal(MediaKind.Video, MediaCatalog.Classify("clip.mp4"));
        Assert.Equal(MediaKind.Image, MediaCatalog.Classify("IMG_01.HEIC"));
        Assert.Equal(MediaKind.Audio, MediaCatalog.Classify("song.mp3"));
        Assert.Equal(MediaKind.Project, MediaCatalog.Classify("Library.photoslibrary", isDirectory: true));
        Assert.Null(MediaCatalog.Classify("os.dmg"));
        Assert.Null(MediaCatalog.Classify("os.clone.dmg"));
        Assert.Null(MediaCatalog.Classify("Setup.pkg"));
        Assert.Null(MediaCatalog.Classify("archive.zip"));
        Assert.Null(MediaCatalog.Classify("Notes.pdf"));
        Assert.Null(MediaCatalog.Classify("Docker.raw"));
        Assert.Equal(MediaKind.Image, MediaCatalog.Classify("IMG_0042.dng"));
    }

    [Fact]
    public void BuildExcludesDmgAndKeepsVideo()
    {
        var tree = new FileTree();
        int root = tree.AddNode("Users", -1, true, 0, 0, 1000);
        int user = tree.AddNode("alex", root, true, 0, 0, 1000);
        int dl = tree.AddNode("Downloads", user, true, 0, 0, 1000);
        int video = tree.AddNode("Dune.mkv", dl, false, 5 * GB, 5 * GB, 100);
        tree.AddNode("os.dmg", dl, false, 7 * GB, 7 * GB, 200);
        tree.AddNode("os.clone.dmg", dl, false, 7 * GB, 7 * GB, 200);
        int photo = tree.AddNode("IMG_1.heic", dl, false, 12 * MB, 12 * MB, 900);
        var built = MediaCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: 1000);

        Assert.Contains(built.Candidates, c => c.NodeID == video);
        Assert.Contains(built.Candidates, c => c.NodeID == photo);
        Assert.DoesNotContain(built.Candidates, c => c.Name == "os.dmg");
        Assert.DoesNotContain(built.Candidates, c => c.Name == "os.clone.dmg");
        Assert.True(built.Summary.VideoBytes >= 5 * GB);
        Assert.True(built.Summary.ImageCount >= 1);
        var dune = Assert.Single(built.Candidates, c => c.NodeID == video);
        Assert.Equal(MediaStatus.ReviewFirst, dune.Status);
        Assert.Equal(MediaLocationBucket.Downloads, dune.Location);
        Assert.Equal(@"C:\Users\alex\Downloads\Dune.mkv", Win(dune.AbsolutePath));
    }

    [Fact]
    public void FiltersByTypeLocationAndQuery()
    {
        var items = new[]
        {
            Candidate(1, "a.mkv", @"C:\Users\x\Downloads\a.mkv", 2 * GB, 400, MediaKind.Video, MediaLocationBucket.Downloads),
            Candidate(2, "b.heic", @"C:\Users\x\Desktop\b.heic", 8 * MB, 40, MediaKind.Image, MediaLocationBucket.Desktop),
        };
        Assert.Single(MediaCatalog.Filter(items, MediaTypeFilter.Video, MediaSizeFilter.Any, MediaAgeFilter.All,
            MediaLocationFilter.Any, ""));
        Assert.Single(MediaCatalog.Filter(items, MediaTypeFilter.All, MediaSizeFilter.Any, MediaAgeFilter.All,
            MediaLocationFilter.Downloads, ""));
        var q = MediaCatalog.Filter(items, MediaTypeFilter.All, MediaSizeFilter.Any, MediaAgeFilter.All,
            MediaLocationFilter.Any, "desktop");
        Assert.Equal("b.heic", Assert.Single(q).Name);
    }

    [Fact]
    public void PersonalVideoNeverLikelyDisposable()
    {
        Assert.Equal(MediaStatus.ReviewFirst, MediaCatalog.ClassifyStatus(MediaKind.Video, 900, "Dune.mkv"));
        Assert.Equal(MediaStatus.ReviewFirst, MediaCatalog.ClassifyStatus(MediaKind.Image, 900, "IMG.heic"));
        Assert.Equal(MediaStatus.ReviewFirst, MediaCatalog.ClassifyStatus(MediaKind.Project, 900, "Lib.photoslibrary"));
    }

    // ---- Windows cases and the rest of the Mac behaviour ----

    [Theory]
    [InlineData("VSCodeSetup.exe", false)]
    [InlineData("node.msi", false)]
    [InlineData("App.msixbundle", false)]
    [InlineData("ext4.vhdx", false)]
    [InlineData("install.wim", false)]
    [InlineData(".mp4", false)]          // NSString.pathExtension: no extension
    [InlineData("clip.mp4 ", false)]     // …nor one with a space
    [InlineData("IMG_0001", true)]       // camera name, no extension → image
    [InlineData("Wedding.veg", true)]    // Vegas Pro project
    [InlineData("Edit.prproj", true)]
    [InlineData("Song.flp", true)]
    [InlineData("track.WMA", true)]
    [InlineData("scan.TIF", true)]
    public void WindowsClassification(string name, bool isMedia) =>
        Assert.Equal(isMedia, MediaCatalog.Classify(name) is not null);

    [Fact]
    public void FoldersAreOnlyMediaWhenTheyAreProjects()
    {
        Assert.Null(MediaCatalog.Classify("Holiday.mp4", isDirectory: true));
        Assert.Equal(MediaKind.Project, MediaCatalog.Classify("Trip.fcpbundle", isDirectory: true));
        Assert.Equal(MediaKind.Project, MediaCatalog.Classify("Backups", @"D:\Final Cut Backups\Backups", isDirectory: true));
        Assert.Null(MediaCatalog.Classify("Backups", @"D:\Final Cut Backups\Backups", isDirectory: false));
    }

    [Theory]
    [InlineData(@"C:\Users\a\Downloads\x.mp4", MediaLocationBucket.Downloads)]
    [InlineData(@"C:\Users\a\Desktop\x.mp4", MediaLocationBucket.Desktop)]
    [InlineData(@"C:\Users\a\Videos\Captures\x.mp4", MediaLocationBucket.Videos)]
    [InlineData(@"C:\Users\a\Movies\x.mp4", MediaLocationBucket.Videos)]
    [InlineData(@"C:\Users\a\OneDrive\Pictures\x.jpg", MediaLocationBucket.Pictures)]
    [InlineData(@"C:\Users\a\Music\x.mp3", MediaLocationBucket.Music)]
    [InlineData(@"C:\Users\a\Documents\x.mp4", MediaLocationBucket.Documents)]
    [InlineData(@"D:\Footage\x.mp4", MediaLocationBucket.Other)]
    [InlineData("/Users/a/Movies/x.mov", MediaLocationBucket.Videos)]
    public void LocationBuckets(string path, MediaLocationBucket expected) =>
        Assert.Equal(expected, MediaCatalog.LocationBucket(path));

    [Theory]
    [InlineData(@"C:\Windows\Web\Wallpaper\Windows\img0.jpg", true)]
    [InlineData(@"D:\Windows\Media\Alarm01.wav", true)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\SystemData\lock.jpg", true)]
    [InlineData(@"C:\Recovery\x.wim", true)]
    [InlineData(@"C:\$WINDOWS.~BT\Sources\x.mp4", true)]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"C:\Users\a\Windows\x.mp4", false)]
    [InlineData(@"C:\Users\a\Videos\x.mp4", false)]
    [InlineData(@"C:\WindowsApps\x.mp4", false)]
    public void ExcludedSystemPaths(string path, bool expected) =>
        Assert.Equal(expected, MediaCatalog.IsExcludedSystemPath(path));

    /// <summary>
    /// A whole-drive scan: Windows' wallpapers and the Recycle Bin are never personal media;
    /// TypeScript sources aren't video; folders only count as projects.
    /// </summary>
    [Fact]
    public void WholeDriveScanKeepsOnlyPersonalMedia()
    {
        // Root "C:" rather than "C:\": the same paths on Windows, and on a non-Windows test
        // host Path.Join doesn't produce "C:\/Windows".
        var tree = new FileTree();
        int root = tree.AddNode("C:", -1, true, 0, 0, 1000);
        int windows = tree.AddNode("Windows", root, true, 0, 0, 1000);
        int web = tree.AddNode("Web", windows, true, 0, 0, 1000);
        tree.AddNode("img0.jpg", web, false, 20 * MB, 20 * MB, 500);
        int bin = tree.AddNode("$Recycle.Bin", root, true, 0, 0, 1000);
        tree.AddNode("$R0AB12.mp4", bin, false, 3 * GB, 3 * GB, 500);
        int users = tree.AddNode("Users", root, true, 0, 0, 1000);
        int alex = tree.AddNode("alex", users, true, 0, 0, 1000);
        int videos = tree.AddNode("Videos", alex, true, 0, 0, 1000);
        int wedding = tree.AddNode("Wedding.mov", videos, false, 26 * GB, 26 * GB, 300);
        int stream = tree.AddNode("capture.ts", videos, false, 40 * MB, 40 * MB, 900);
        int code = tree.AddNode("code", alex, true, 0, 0, 1000);
        tree.AddNode("index.ts", code, false, 2 * MB, 2 * MB, 990);
        int music = tree.AddNode("Music", alex, true, 0, 0, 1000);
        int mix = tree.AddNode("final mix.wav", music, false, 600 * MB, 600 * MB, 100);
        tree.AddNode("tiny.png", music, false, 100_000, 100_000, 100);
        int album = tree.AddNode("Album.mp3", music, true, 0, 0, 1000);   // a folder with a media name
        tree.AddNode("01.flac", album, false, 30 * MB, 30 * MB, 100);

        var built = MediaCatalog.Build(tree, "C:", tree.RollUpBoth().Allocated, today: 1000,
            home: @"C:\Users\alex");
        var ids = built.Candidates.Select(c => c.NodeID).ToList();
        Assert.Equal([wedding, mix, stream, ids[3]], ids);
        Assert.Equal("01.flac", built.Candidates[3].Name);
        Assert.DoesNotContain(album, ids);

        var weddingRow = built.Candidates[0];
        Assert.Equal(MediaLocationBucket.Videos, weddingRow.Location);
        Assert.Equal(@"~\Videos\Wedding.mov", Win(weddingRow.DisplayPath));
        Assert.Equal(700, weddingRow.AgeDays);
        Assert.Equal("Large video (26 GB) in Videos that hasn’t been modified in 1y 11m.", weddingRow.WhyHere);
        Assert.Equal("Personal media. freedisk.space can’t determine whether you still need it — review first.",
            weddingRow.Recommendation);

        var mixRow = built.Candidates[1];
        Assert.Equal(MediaStatus.LikelyDisposable, mixRow.Status);
        Assert.Equal("Audio file (600 MB) in Music, last modified 2y 5m ago.", mixRow.WhyHere);
        Assert.Equal("Looks like scratch/export media that may be safe to review, but confirm before removing.",
            mixRow.Recommendation);
        Assert.False(built.IsTruncated);
        // At least one item over 1 GB: opportunities are only those.
        Assert.Equal([wedding], built.Opportunities.Select(c => c.NodeID));
    }

    [Fact]
    public void LimitKeepsTheLargestAndSaysSo()
    {
        var tree = new FileTree();
        int root = tree.AddNode("Users", -1, true, 0, 0, 1000);
        int pics = tree.AddNode("Pictures", root, true, 0, 0, 1000);
        int a = tree.AddNode("a.jpg", pics, false, 5 * MB, 5 * MB, 990);
        int b = tree.AddNode("b.jpg", pics, false, 9 * MB, 9 * MB, 990);
        int c = tree.AddNode("c.jpg", pics, false, 9 * MB, 9 * MB, 990);
        tree.AddNode("d.jpg", pics, false, 1 * MB, 1 * MB, 990);

        var built = MediaCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: 1000, limit: 3);
        Assert.Equal([b, c, a], built.Candidates.Select(x => x.NodeID));   // size, then node order
        Assert.True(built.IsTruncated);
        Assert.Equal(3, built.Summary.TotalCount);
        // Nothing over 1 GB: the five largest stand in.
        Assert.Equal([b, c, a], built.Opportunities.Select(x => x.NodeID));

        var all = MediaCatalog.Build(tree, @"C:\Users", tree.RollUpBoth().Allocated, today: 1000, limit: 4);
        Assert.False(all.IsTruncated);
    }

    [Fact]
    public void SummaryBucketsAndInsights()
    {
        var items = new List<MediaCandidate>
        {
            Candidate(1, "a.mkv", @"D:\Footage\a.mkv", 6 * GB, 800, MediaKind.Video, MediaLocationBucket.Other),
            Candidate(2, "b.mp4", @"C:\Users\x\Downloads\b.mp4", 2 * GB, 10, MediaKind.Video, MediaLocationBucket.Downloads),
            Candidate(3, "c.jpg", @"C:\Users\x\Pictures\c.jpg", 1 * GB, 400, MediaKind.Image, MediaLocationBucket.Pictures),
            Candidate(4, "d.veg", @"C:\Users\x\Videos\d.veg", 1 * GB, 60, MediaKind.Project, MediaLocationBucket.Videos),
        };
        var s = MediaCatalog.Summarize(items);
        Assert.Equal(10 * GB, s.TotalBytes);
        Assert.Equal(4, s.TotalCount);
        Assert.Equal((8 * GB, 2), (s.VideoBytes, s.VideoCount));
        Assert.Equal((1 * GB, 1), (s.ImageBytes, s.ImageCount));
        Assert.Equal((0L, 0), (s.AudioBytes, s.AudioCount));
        Assert.Equal((1 * GB, 1), (s.ProjectBytes, s.ProjectCount));
        Assert.Equal(4, s.Over1GBCount);
        Assert.Equal(["video", "image", "project"], s.TypeBuckets.Select(b => b.Id));
        Assert.Equal(0.8, s.TypeBuckets[0].Fraction, 9);
        // Fixed folder order, not size order.
        Assert.Equal(["downloads", "videos", "pictures", "other"], s.LocationBuckets.Select(b => b.Id));
        Assert.Equal(["Downloads", "Videos", "Pictures", "Other"], s.LocationBuckets.Select(b => b.Title));
        Assert.Equal(
            [("a30", "< 30 days"), ("a90", "30–90 days"), ("a730", "1–2 years"), ("a2p", "2+ years")],
            s.AgeBuckets.Select(b => (b.Id, b.Title)));
        Assert.Equal(
        [
            "2 GB of media is in Downloads.",
            "6 GB of media hasn’t been modified in over 2 years.",
            "Video accounts for 80% of media storage.",
        ], s.InsightLines);

        // Over a year but not two: the one-year line instead.
        var younger = MediaCatalog.Summarize([items[2]]);
        Assert.Equal(["1 GB of media is in Pictures.", "1 GB of media hasn’t been modified in over a year."],
            younger.InsightLines);
        Assert.Empty(MediaCatalog.Summarize([]).InsightLines);
    }

    [Fact]
    public void VideoShareRoundsHalvesUp()
    {
        var items = new List<MediaCandidate>
        {
            Candidate(1, "a.mp4", @"D:\a.mp4", 125 * MB, 10, MediaKind.Video, MediaLocationBucket.Other),
            Candidate(2, "b.jpg", @"D:\b.jpg", 875 * MB, 10, MediaKind.Image, MediaLocationBucket.Other),
        };
        // 12.5% → 13%, like Swift's rounded().
        Assert.Contains("Video accounts for 13% of media storage.", MediaCatalog.Summarize(items).InsightLines);
    }

    [Fact]
    public void QueryShortcutsAndSorts()
    {
        var items = new[]
        {
            Candidate(1, "Clip10.mp4", @"C:\Users\x\Downloads\Clip10.mp4", 3 * GB, 5, MediaKind.Video, MediaLocationBucket.Downloads, score: 2),
            Candidate(2, "clip9.mp3", @"C:\Users\x\Music\clip9.mp3", 1 * GB, 50, MediaKind.Audio, MediaLocationBucket.Music, score: 9),
            Candidate(3, "beach.jpg", @"C:\Users\x\Pictures\beach.jpg", 2 * GB, 20, MediaKind.Image, MediaLocationBucket.Pictures, score: 5),
        };
        List<int> Q(string q) => MediaCatalog.Filter(items, MediaTypeFilter.All, MediaSizeFilter.Any,
            MediaAgeFilter.All, MediaLocationFilter.Any, q).Select(c => c.NodeID).ToList();
        Assert.Equal([1], Q("video"));
        Assert.Equal([3], Q(" Photos "));
        Assert.Equal([2], Q("music"));
        Assert.Equal([1], Q("downloads"));
        Assert.Equal([3], Q("pictures"));           // location title
        Assert.Equal([1, 2], Q("clip"));            // name
        Assert.Equal([2], Q(@"x\music"));           // absolute path

        Assert.Equal([1, 3, 2], MediaCatalog.Sorted(items, MediaSort.Largest).Select(c => c.NodeID));
        Assert.Equal([2, 3, 1], MediaCatalog.Sorted(items, MediaSort.Oldest).Select(c => c.NodeID));
        Assert.Equal([1, 3, 2], MediaCatalog.Sorted(items, MediaSort.Newest).Select(c => c.NodeID));
        // localizedCaseInsensitiveCompare: not numeric, so "Clip10" sorts before "clip9".
        Assert.Equal([3, 1, 2], MediaCatalog.Sorted(items, MediaSort.Name).Select(c => c.NodeID));
        Assert.Equal([2, 3, 1], MediaCatalog.Sorted(items, MediaSort.Opportunities).Select(c => c.NodeID));
    }

    [Fact]
    public void OpportunityScoreFavoursLooseOldVideo()
    {
        Assert.Equal(1000 * 2 * 1.25 * 1.1,
            MediaCatalog.OpportunityScore(GB, 60, MediaKind.Video, MediaLocationBucket.Downloads, MediaStatus.ReviewFirst), 6);
        Assert.Equal(1000 * 1.3,
            MediaCatalog.OpportunityScore(GB, 0, MediaKind.Audio, MediaLocationBucket.Music, MediaStatus.LikelyDisposable), 6);
    }

    [Theory]
    [InlineData(0, "0d")]
    [InlineData(29, "29d")]
    [InlineData(30, "1m")]
    [InlineData(364, "12m")]
    [InlineData(365, "1y")]
    [InlineData(700, "1y 11m")]
    public void AgeLabels(int days, string expected) => Assert.Equal(expected, MediaCatalog.AgeLabel(days));

    [Fact]
    public void FilterTitlesAndThresholdsMatchTheMac()
    {
        Assert.Equal(["All", "Video", "Images", "Audio", "Projects", "Other"], Enum.GetValues<MediaTypeFilter>().Select(f => f.Title()));
        Assert.Equal(["Any", ">100 MB", ">500 MB", ">1 GB", ">5 GB", ">10 GB"], Enum.GetValues<MediaSizeFilter>().Select(f => f.Title()));
        Assert.Equal([0L, 100 * MB, 500 * MB, GB, 5 * GB, 10 * GB], Enum.GetValues<MediaSizeFilter>().Select(f => f.MinBytes()));
        Assert.Equal(["Any", "30d+", "90d+", "6mo+", "1y+"], Enum.GetValues<MediaAgeFilter>().Select(f => f.Title()));
        Assert.Equal(["Any", "Downloads", "Desktop", "Videos", "Pictures", "Music", "Documents", "Other"],
            Enum.GetValues<MediaLocationFilter>().Select(f => f.Title()));
        Assert.Equal(["Largest first", "Oldest first", "Newest first", "Name", "Best cleanup opportunities"],
            Enum.GetValues<MediaSort>().Select(f => f.Title()));
        Assert.Equal(["Video", "Images", "Audio", "Media Projects", "Other"], Enum.GetValues<MediaKind>().Select(k => k.Title()));
    }

    [Fact]
    public void MismatchedTotalsReturnEmpty()
    {
        var tree = new FileTree();
        tree.AddNode("Users", -1, true, 0, 0, 0);
        Assert.Same(MediaCatalogResult.Empty, MediaCatalog.Build(tree, @"C:\Users", [1, 2]));
    }
}
