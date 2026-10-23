namespace DiskMap.Core;

/// <summary>One thing a detail page's Add puts in the basket: a scanned node and the reason the basket shows.</summary>
public sealed record CleanDetailRequest(int NodeId, string Reason);

/// <summary>
/// One row of a Clean up detail page (Caches, Old downloads, Large media), whatever it came
/// from (Mac: Kit/CleanDetail.swift CleanDetailRow). <see cref="StagePaths"/> are the paths
/// that must all be in the basket for the row to read as added.
/// </summary>
public sealed record CleanDetailRow(
    string Id,
    int? NodeId,
    string Name,
    string Path,
    string Place,
    string Badge,
    string Kind,
    long Bytes,
    string Changed,
    string Note,
    bool Accent,
    IReadOnlyList<CleanDetailRequest> Requests,
    IReadOnlyList<string> StagePaths);

/// <summary>
/// The detail pages' words, filters, footer and bulk add (Mac: CleanDetailPage,
/// CachesReviewView, OldDownloadsView, LargeMediaView). Pure, so the pages only lay out.
/// </summary>
public static class CleanDetail
{
    /// <summary>How many rows a page draws; the footer says when there are more.</summary>
    public const int RowLimit = 300;

    // ---- Caches ----

    /// <summary>
    /// The cache groups the Caches page lists: the per-app AppData caches, never anything
    /// protected or empty, biggest first (Mac: cachedReviewables filtered to .caches, then
    /// CleanUpView.buildItems' rules).
    /// </summary>
    public static List<ReviewableTarget> Caches(IEnumerable<ReviewableTarget> reviewables) =>
        reviewables.Where(t => t.Category == ReviewableCategory.Caches && !t.IsProtected && t.Bytes > 0)
            .OrderByDescending(t => t.Bytes).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();

    /// <summary>Each of a cache group's folders, under the reason Clean up gives it ("Clean up: Chrome").</summary>
    public static IReadOnlyList<CleanDetailRequest> CacheRequests(ReviewableTarget target) =>
        target.NodeIds.Where(id => id >= 0).Distinct()
            .Select(id => new CleanDetailRequest(id, "Clean up: " + target.DisplayName)).ToList();

    /// <summary>The Where column: "1 place", "3 places".</summary>
    public static string Places(int count) => count == 1 ? "1 place" : $"{count:N0} places";

    /// <summary>The Suggestion column for a cache: it rebuilds by itself, or look first.</summary>
    public static string CacheSuggestion(bool generallySafe) => generallySafe ? "Rebuilds" : "Look first";

    /// <summary>Search over name and path, then "Rebuild by themselves" (Mac: CachesReviewView.body).</summary>
    public static List<CleanDetailRow> FilterCaches(IEnumerable<CleanDetailRow> rows, string query, bool onlySafe)
    {
        string q = query.Trim().ToLowerInvariant();
        return rows.Where(r => (!onlySafe || r.Accent)
            && (q.Length == 0 || r.Name.ToLowerInvariant().Contains(q) || r.Path.ToLowerInvariant().Contains(q))).ToList();
    }

    // ---- Old downloads ----

    /// <summary>The Suggestion column for a download.</summary>
    public static string DownloadSuggestion(OldDownloadsStatus status) =>
        status == OldDownloadsStatus.LikelyDisposable ? "Likely done with" : "Look first";

    /// <summary>One request for a download, none for something protected.</summary>
    public static IReadOnlyList<CleanDetailRequest> DownloadRequests(OldDownloadsCandidate c) =>
        c.Safety.Level == SafetyLevel.Protected || c.NodeID < 0 ? [] : [new CleanDetailRequest(c.NodeID, "Old Downloads: " + c.Name)];

    /// <summary>
    /// The type chips: All, then each type with what the age and size filters leave, the first
    /// three on a narrow window; a type with nothing in it is left out (All never is).
    /// </summary>
    public static List<(OldDownloadsTypeFilter Filter, int Count)> DownloadTypeChips(
        IReadOnlyList<OldDownloadsCandidate> candidates, OldDownloadsAgeFilter age, OldDownloadsSizeFilter size, bool compact) =>
        Enum.GetValues<OldDownloadsTypeFilter>().Take(compact ? 3 : 6)
            .Select(type => (type, OldDownloadsCatalog.Filter(candidates, age, size, type, "").Count))
            .Where(chip => chip.type == OldDownloadsTypeFilter.All || chip.Count > 0)
            .ToList();

    // ---- Large media ----

    /// <summary>The Kind column: "Media project", "Image", "Video", "Audio", "Other".</summary>
    public static string MediaKindText(MediaKind kind) => kind switch
    {
        MediaKind.Project => "Media project",
        MediaKind.Image => "Image",
        _ => kind.Title(),
    };

    /// <summary>The note: what to do about it, else why it is listed.</summary>
    public static string MediaNote(MediaCandidate c) => c.Recommendation.Length == 0 ? c.WhyHere : c.Recommendation;

    /// <summary>One request for a media item, none for something protected.</summary>
    public static IReadOnlyList<CleanDetailRequest> MediaRequests(MediaCandidate c) =>
        c.Safety.Level == SafetyLevel.Protected || c.NodeID < 0 ? [] : [new CleanDetailRequest(c.NodeID, "Large Media: " + c.Name)];

    /// <summary>The type chips, like <see cref="DownloadTypeChips"/>, under the other filters.</summary>
    public static List<(MediaTypeFilter Filter, int Count)> MediaTypeChips(IReadOnlyList<MediaCandidate> candidates,
        MediaSizeFilter size, MediaAgeFilter age, MediaLocationFilter location, bool compact) =>
        Enum.GetValues<MediaTypeFilter>().Take(compact ? 3 : 6)
            .Select(type => (type, MediaCatalog.Filter(candidates, type, size, age, location, "").Count))
            .Where(chip => chip.type == MediaTypeFilter.All || chip.Count > 0)
            .ToList();

    // ---- The shared shell ----

    /// <summary>The header's figure: "5 · 22.9 GB".</summary>
    public static string HeaderFigure(int count, long bytes) => $"{count:N0} · {ShellFormat.HeroBytes(bytes)}";

    /// <summary>"5 shown · 22.9 GB", or past the row limit, how to see the rest.</summary>
    public static string FooterLine(IReadOnlyCollection<CleanDetailRow> rows, int limit = RowLimit) =>
        rows.Count > limit
            ? $"Largest {limit:N0} of {rows.Count:N0} shown — narrow the filters for the rest"
            : $"{rows.Count:N0} shown · {ShellFormat.HeroBytes(rows.Sum(r => r.Bytes))}";

    /// <summary>What the footer's button adds: the rows shown that can go in the basket and pass the page's filter.</summary>
    public static List<CleanDetailRow> Bulk(IEnumerable<CleanDetailRow> shown, Func<CleanDetailRow, bool> filter) =>
        shown.Where(r => r.Requests.Count > 0 && filter(r)).ToList();

    /// <summary>"Add 5 that rebuild · 22.9 GB" (Caches).</summary>
    public static string CachesBulkTitle(int count, long bytes) => $"Add {count:N0} that rebuild · {ShellFormat.HeroBytes(bytes)}";

    /// <summary>"Add 4 shown · 21.5 GB" (Old downloads).</summary>
    public static string DownloadsBulkTitle(int count, long bytes) => $"Add {count:N0} shown · {ShellFormat.HeroBytes(bytes)}";

    /// <summary>"Add 2 likely done with · 4.1 GB" (Large media).</summary>
    public static string MediaBulkTitle(int count, long bytes) => $"Add {count:N0} likely done with · {ShellFormat.HeroBytes(bytes)}";

    /// <summary>In the basket: something can be added, and every path it adds already is.</summary>
    public static bool InBasket(CleanDetailRow row, Func<string, bool> isStaged) =>
        row.Requests.Count > 0 && row.StagePaths.Count > 0 && row.StagePaths.All(isStaged);
}
