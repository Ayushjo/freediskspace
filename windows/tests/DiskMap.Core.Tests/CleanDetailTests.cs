using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The Clean up detail pages' rules (Mac: CleanDetailPage, CachesReviewView, OldDownloadsView,
/// LargeMediaView): what Caches lists, the filters, the footer, the bulk add and its wording.
/// </summary>
public class CleanDetailTests
{
    private const long MB = 1_000_000;
    private const long GB = 1_000_000_000;

    private static ReviewableTarget Target(string id, string name, long bytes, SafetyLevel level,
        ReviewableCategory category = ReviewableCategory.Caches, int[]? nodes = null, params string[] paths) =>
        new(id, category, name, "Application cache", bytes, paths.Length > 0 ? paths[0] : $@"C:\Users\you\AppData\Local\{name}\Cache",
            paths.Length > 0 ? paths : [$@"C:\Users\you\AppData\Local\{name}\Cache"],
            nodes ?? [7], new SafetyAssessment(level, "", name), $"{name} can recreate this.", "internaldrive", null);

    private static CleanDetailRow Row(string id, long bytes, bool accent, bool canAdd = true, params string[] paths) =>
        new(id, 1, id, paths.Length > 0 ? paths[0] : $@"C:\x\{id}", "x", "APP", "Rebuilds", bytes, "1 place", "",
            accent, canAdd ? [new CleanDetailRequest(1, "Clean up: " + id)] : [],
            paths.Length > 0 ? paths : [$@"C:\x\{id}"]);

    private static OldDownloadsCandidate Download(int id, string name, long bytes, int ageDays, FileKind kind,
        SafetyLevel level = SafetyLevel.Review, OldDownloadsStatus status = OldDownloadsStatus.ReviewFirst) =>
        new(id, name, $@"C:\Users\you\Downloads\{name}", $@"Downloads\{name}", bytes, 1, ageDays, kind, status,
            new SafetyAssessment(level, "r", name), "why", "rec", 1);

    private static MediaCandidate Media(int id, string name, long bytes, int ageDays, MediaKind kind,
        SafetyLevel level = SafetyLevel.Review, string recommendation = "rec") =>
        new(id, name, $@"C:\Users\you\Videos\{name}", $@"Videos\{name}", bytes, 1, ageDays, kind, MediaLocationBucket.Videos,
            MediaStatus.ReviewFirst, new SafetyAssessment(level, "r", name), "why", recommendation, false, 1);

    // ---- Caches ----

    [Fact]
    public void CachesListsOnlyAppCachesThatCanGoBiggestFirst()
    {
        var caches = CleanDetail.Caches(
        [
            Target("cache-Spotify", "Spotify", 1_900 * MB, SafetyLevel.Safe),
            Target("cache-Chrome", "Chrome", 2_400 * MB, SafetyLevel.Safe),
            Target("qw-1", "npm cache", 5_600 * MB, SafetyLevel.Safe, ReviewableCategory.PackageCaches),
            Target("cache-Locked", "Locked", 9_000 * MB, SafetyLevel.Protected),
            Target("cache-Empty", "Empty", 0, SafetyLevel.Safe),
        ]);
        Assert.Equal(["cache-Chrome", "cache-Spotify"], caches.Select(c => c.Id));
    }

    [Fact]
    public void ACacheGroupAddsEachOfItsFoldersUnderItsAppName()
    {
        var chrome = Target("cache-Chrome", "Chrome", 2_400 * MB, SafetyLevel.Safe, nodes: [12, 30, 12, -1],
            paths: [@"C:\a\Cache", @"C:\a\Code Cache", @"C:\a\GPUCache"]);
        var requests = CleanDetail.CacheRequests(chrome);
        Assert.Equal([12, 30], requests.Select(r => r.NodeId));
        Assert.All(requests, r => Assert.Equal("Clean up: Chrome", r.Reason));
    }

    [Fact]
    public void CacheWords()
    {
        Assert.Equal("1 place", CleanDetail.Places(1));
        Assert.Equal("3 places", CleanDetail.Places(3));
        Assert.Equal("Rebuilds", CleanDetail.CacheSuggestion(true));
        Assert.Equal("Look first", CleanDetail.CacheSuggestion(false));
    }

    [Fact]
    public void CacheSearchMatchesNameOrPathAndTheChipKeepsWhatRebuilds()
    {
        CleanDetailRow[] rows =
        [
            Row("Chrome", 2_400 * MB, true, paths: @"C:\Users\you\AppData\Local\Google\Chrome\User Data\Default\Cache"),
            Row("Teams", 900 * MB, false, paths: @"C:\Users\you\AppData\Roaming\Microsoft\Teams\Cache"),
        ];
        Assert.Equal(2, CleanDetail.FilterCaches(rows, "", false).Count);
        Assert.Equal(["Chrome"], CleanDetail.FilterCaches(rows, "  CHROME ", false).Select(r => r.Id));
        Assert.Equal(["Teams"], CleanDetail.FilterCaches(rows, "roaming", false).Select(r => r.Id));
        Assert.Equal(["Chrome"], CleanDetail.FilterCaches(rows, "", true).Select(r => r.Id));
        Assert.Empty(CleanDetail.FilterCaches(rows, "teams", true));
    }

    // ---- Footer and bulk add ----

    [Fact]
    public void HeaderAndFooterSayHowManyAndHowBig()
    {
        CleanDetailRow[] rows =
        [
            Row("Xcode", 14_800 * MB, true), Row("Homebrew", 3_200 * MB, true), Row("Chrome", 2_400 * MB, true),
            Row("Spotify", 1_900 * MB, true), Row("Swiftpm", 600 * MB, true),
        ];
        Assert.Equal("5 · 22.9 GB", CleanDetail.HeaderFigure(5, rows.Sum(r => r.Bytes)));
        Assert.Equal("5 shown · 22.9 GB", CleanDetail.FooterLine(rows));
        Assert.Equal("Largest 3 of 5 shown — narrow the filters for the rest", CleanDetail.FooterLine(rows, limit: 3));
        Assert.Equal(300, CleanDetail.RowLimit);
    }

    [Fact]
    public void BulkAddTakesOnlyWhatCanGoAndPassesThePageFilter()
    {
        CleanDetailRow[] rows =
        [
            Row("a", 3 * GB, true), Row("b", 2 * GB, false), Row("kept", 9 * GB, true, canAdd: false),
        ];
        var rebuild = CleanDetail.Bulk(rows, r => r.Accent);
        Assert.Equal(["a"], rebuild.Select(r => r.Id));
        var shown = CleanDetail.Bulk(rows, _ => true);
        Assert.Equal(["a", "b"], shown.Select(r => r.Id));
        Assert.Equal("Add 5 that rebuild · 22.9 GB", CleanDetail.CachesBulkTitle(5, 22_900 * MB));
        Assert.Equal("Add 4 shown · 21.5 GB", CleanDetail.DownloadsBulkTitle(4, 21_500 * MB));
        Assert.Equal("Add 2 likely done with · 4.10 GB", CleanDetail.MediaBulkTitle(2, 4_100 * MB));
    }

    [Fact]
    public void ARowIsInTheBasketOnlyWhenEveryPathIs()
    {
        var two = Row("Chrome", GB, true, paths: [@"C:\a\Cache", @"C:\a\Code Cache"]);
        Assert.False(CleanDetail.InBasket(two, p => p == @"C:\a\Cache"));
        Assert.True(CleanDetail.InBasket(two, _ => true));
        Assert.False(CleanDetail.InBasket(Row("kept", GB, false, canAdd: false), _ => true));
    }

    // ---- Old downloads ----

    [Fact]
    public void DownloadsWordsAndRequests()
    {
        Assert.Equal("Likely done with", CleanDetail.DownloadSuggestion(OldDownloadsStatus.LikelyDisposable));
        Assert.Equal("Look first", CleanDetail.DownloadSuggestion(OldDownloadsStatus.ReviewFirst));
        var setup = CleanDetail.DownloadRequests(Download(4, "setup.msi", GB, 200, FileKind.DiskImage));
        Assert.Equal([(4, "Old Downloads: setup.msi")], setup.Select(r => (r.NodeId, r.Reason)));
        Assert.Empty(CleanDetail.DownloadRequests(Download(5, "desktop.ini", MB, 200, FileKind.Other, SafetyLevel.Protected)));
    }

    [Fact]
    public void DownloadTypeChipsCountUnderTheAgeAndSizeFiltersAndHideEmptyTypes()
    {
        OldDownloadsCandidate[] all =
        [
            Download(1, "Xcode_26.xip", 11_200 * MB, 150, FileKind.Archive),
            Download(2, "ubuntu.iso", 6_100 * MB, 240, FileKind.DiskImage),
            Download(3, "Figma-old.msi", 420 * MB, 300, FileKind.DiskImage),
            Download(4, "notes.bin", 300 * MB, 400, FileKind.Other),
            Download(5, "fresh.zip", 2 * GB, 3, FileKind.Archive),
        ];
        var chips = CleanDetail.DownloadTypeChips(all, OldDownloadsAgeFilter.Days30, OldDownloadsSizeFilter.Any, compact: false);
        Assert.Equal(
            [(OldDownloadsTypeFilter.All, 4), (OldDownloadsTypeFilter.Archive, 1), (OldDownloadsTypeFilter.Installer, 2), (OldDownloadsTypeFilter.Other, 1)],
            chips);
        var narrow = CleanDetail.DownloadTypeChips(all, OldDownloadsAgeFilter.Days30, OldDownloadsSizeFilter.Gb1, compact: true);
        Assert.Equal([(OldDownloadsTypeFilter.All, 2), (OldDownloadsTypeFilter.Archive, 1)], narrow);
    }

    // ---- Large media ----

    [Fact]
    public void MediaWordsAndRequests()
    {
        Assert.Equal("Media project", CleanDetail.MediaKindText(MediaKind.Project));
        Assert.Equal("Image", CleanDetail.MediaKindText(MediaKind.Image));
        Assert.Equal("Video", CleanDetail.MediaKindText(MediaKind.Video));
        Assert.Equal("Audio", CleanDetail.MediaKindText(MediaKind.Audio));
        Assert.Equal("rec", CleanDetail.MediaNote(Media(1, "a.mov", GB, 10, MediaKind.Video)));
        Assert.Equal("why", CleanDetail.MediaNote(Media(1, "a.mov", GB, 10, MediaKind.Video, recommendation: "")));
        Assert.Equal([(9, "Large Media: Wedding.mov")],
            CleanDetail.MediaRequests(Media(9, "Wedding.mov", GB, 10, MediaKind.Video)).Select(r => (r.NodeId, r.Reason)));
        Assert.Empty(CleanDetail.MediaRequests(Media(9, "x.mov", GB, 10, MediaKind.Video, SafetyLevel.Protected)));
    }

    [Fact]
    public void MediaTypeChipsFollowTheOtherFilters()
    {
        MediaCandidate[] all =
        [
            Media(1, "a.mov", 26 * GB, 400, MediaKind.Video), Media(2, "b.mov", 18 * GB, 10, MediaKind.Video),
            Media(3, "c.heic", 21 * GB, 30, MediaKind.Image), Media(4, "d.logicx", 4 * GB, 180, MediaKind.Project),
        ];
        var chips = CleanDetail.MediaTypeChips(all, MediaSizeFilter.Any, MediaAgeFilter.All, MediaLocationFilter.Any, compact: false);
        Assert.Equal([(MediaTypeFilter.All, 4), (MediaTypeFilter.Video, 2), (MediaTypeFilter.Image, 1), (MediaTypeFilter.Project, 1)], chips);
        var older = CleanDetail.MediaTypeChips(all, MediaSizeFilter.Gb5, MediaAgeFilter.Days30, MediaLocationFilter.Any, compact: true);
        Assert.Equal([(MediaTypeFilter.All, 2), (MediaTypeFilter.Video, 1), (MediaTypeFilter.Image, 1)], older);
    }
}
