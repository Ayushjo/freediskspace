using System.Globalization;

namespace DiskMap.Core;

// Old Downloads: big, old files in the user's Downloads, never called "safe to delete"
// (Mac: OldDownloadsCatalog.swift). Thresholds, sort orders and strings match the Mac
// character for character; Windows differences are commented where they occur.

public enum OldDownloadsAgeFilter { All, Days30, Days90, Months6, Year1 }

public enum OldDownloadsSizeFilter { Any, Mb100, Mb500, Gb1, Gb5 }

public enum OldDownloadsTypeFilter { All, Video, Archive, Installer, Document, Other }

public enum OldDownloadsSort { Largest, Oldest, Newest, Name, Smart }

/// <summary>Inclusion status for Old Downloads — never "safe to delete" for personal media.</summary>
public enum OldDownloadsStatus { ReviewFirst, LikelyDisposable }

public static class OldDownloadsFilterExtensions
{
    public static string Title(this OldDownloadsAgeFilter filter) => filter switch
    {
        OldDownloadsAgeFilter.Days30 => "30d+",
        OldDownloadsAgeFilter.Days90 => "90d+",
        OldDownloadsAgeFilter.Months6 => "6mo+",
        OldDownloadsAgeFilter.Year1 => "1y+",
        _ => "All",
    };

    public static int MinAgeDays(this OldDownloadsAgeFilter filter) => filter switch
    {
        OldDownloadsAgeFilter.Days30 => 30,
        OldDownloadsAgeFilter.Days90 => 90,
        OldDownloadsAgeFilter.Months6 => 180,
        OldDownloadsAgeFilter.Year1 => 365,
        _ => 0,
    };

    public static string Title(this OldDownloadsSizeFilter filter) => filter switch
    {
        OldDownloadsSizeFilter.Mb100 => ">100 MB",
        OldDownloadsSizeFilter.Mb500 => ">500 MB",
        OldDownloadsSizeFilter.Gb1 => ">1 GB",
        OldDownloadsSizeFilter.Gb5 => ">5 GB",
        _ => "Any",
    };

    public static long MinBytes(this OldDownloadsSizeFilter filter) => filter switch
    {
        OldDownloadsSizeFilter.Mb100 => 100_000_000,
        OldDownloadsSizeFilter.Mb500 => 500_000_000,
        OldDownloadsSizeFilter.Gb1 => 1_000_000_000,
        OldDownloadsSizeFilter.Gb5 => 5_000_000_000,
        _ => 0,
    };

    public static string Title(this OldDownloadsTypeFilter filter) => filter switch
    {
        OldDownloadsTypeFilter.Video => "Videos",
        OldDownloadsTypeFilter.Archive => "Archives",
        OldDownloadsTypeFilter.Installer => "Installers",
        OldDownloadsTypeFilter.Document => "Documents",
        OldDownloadsTypeFilter.Other => "Other",
        _ => "All",
    };

    /// <summary>"Installers" is <see cref="FileKind.DiskImage"/> — on Windows that kind also holds .msi/.msix/setup .exe.</summary>
    public static bool Matches(this OldDownloadsTypeFilter filter, FileKind kind) => filter switch
    {
        OldDownloadsTypeFilter.Video => kind == FileKind.Video,
        OldDownloadsTypeFilter.Archive => kind == FileKind.Archive,
        OldDownloadsTypeFilter.Installer => kind == FileKind.DiskImage,
        OldDownloadsTypeFilter.Document => kind == FileKind.Document,
        OldDownloadsTypeFilter.Other => kind != FileKind.Video && kind != FileKind.Archive
            && kind != FileKind.DiskImage && kind != FileKind.Document,
        _ => true,
    };

    public static string Title(this OldDownloadsSort sort) => sort switch
    {
        OldDownloadsSort.Oldest => "Oldest first",
        OldDownloadsSort.Newest => "Newest first",
        OldDownloadsSort.Name => "Name",
        OldDownloadsSort.Smart => "Smart (space × age)",
        _ => "Largest first",
    };

    public static string Title(this OldDownloadsStatus status) => status switch
    {
        OldDownloadsStatus.LikelyDisposable => "Likely disposable",
        _ => "Review first",
    };
}

public sealed record OldDownloadsCandidate(
    int NodeID,
    string Name,
    string AbsolutePath,
    string DisplayPath,
    long Bytes,
    int ModifiedDay,
    int AgeDays,
    FileKind Kind,
    OldDownloadsStatus Status,
    SafetyAssessment Safety,
    string WhyHere,
    string Recommendation,
    double Score)
{
    public int Id => NodeID;
}

public sealed record OldDownloadsAgeBucket(string Title, long Bytes, int Count)
{
    public string Id => Title;
}

public sealed record OldDownloadsTypeBucket(FileKind Kind, long Bytes, int Count)
{
    public string Id => Kind.RawValue();
}

public sealed record OldDownloadsSummary(
    long TotalBytes,
    int TotalCount,
    long Bytes30,
    int Count30,
    long Bytes90,
    int Count90,
    long Bytes365,
    int Count365,
    int ReviewCount,
    IReadOnlyList<OldDownloadsAgeBucket> AgeBuckets,
    IReadOnlyList<OldDownloadsTypeBucket> TypeBuckets,
    IReadOnlyList<string> InsightLines)
{
    public static readonly OldDownloadsSummary Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);
}

public sealed record OldDownloadsCatalogResult(
    IReadOnlyList<OldDownloadsCandidate> Candidates,
    OldDownloadsSummary Summary)
{
    public static readonly OldDownloadsCatalogResult Empty = new([], OldDownloadsSummary.Empty);
}

public static class OldDownloadsCatalog
{
    /// <summary>Minimum size to appear when age is young; always include older files above this floor.</summary>
    public const long ListingFloorBytes = 1_000_000;

    /// <summary>
    /// <paramref name="today"/>: days since 1970-01-01 UTC; 0 means
    /// <see cref="AgeMap.Today"/> (DateTimeOffset.UtcNow). <paramref name="home"/>: the
    /// profile shown as "~" in <see cref="OldDownloadsCandidate.DisplayPath"/>; null means
    /// <see cref="UserFolders.Home"/>.
    /// </summary>
    public static OldDownloadsCatalogResult Build(
        FileTree tree,
        string root,
        long[] totals,
        int today = 0,
        // Every match is built anyway (the summary needs them); the screen
        // draws 200 rows. A 500 cap made its filters count 500 of 810.
        int limit = 20_000,
        string? home = null)
    {
        if (totals.Length != tree.Count) return OldDownloadsCatalogResult.Empty;
        if (today == 0) today = AgeMap.Today();

        // `IsUnderDownloads(path)` is "some path component starts with
        // downloads". Answer it per folder in one pass and build a path only
        // for files that pass (Mac TASK-042).
        var underDownloads = CatalogTree.FolderChainFlags(tree, IsUnderDownloads(root),
            name => name.StartsWith("downloads", StringComparison.OrdinalIgnoreCase));
        // A "Downloads" folder inside a Library is a tool's own cache
        // (~/Library/Caches/Homebrew/downloads), not the user's Downloads.
        // Windows: the Library's role is played by AppData and ProgramData
        // (AppData\Local\…\downloads, ProgramData\chocolatey\…).
        var underLibrary = CatalogTree.FolderChainFlags(tree, IsUnderAppData(root),
            name => name.Equals("appdata", StringComparison.OrdinalIgnoreCase)
                || name.Equals("programdata", StringComparison.OrdinalIgnoreCase));
        // Mac: Trash, Spotlight … Windows: Recycle Bin, System Volume Information.
        var held = CatalogTree.SystemHoldingFlags(tree, root);
        var hits = new List<OldDownloadsCandidate>();
        for (int i = 0; i < tree.Count; i++)
        {
            if (tree.IsDirectory[i]) continue;
            long bytes = totals[i];
            if (bytes < ListingFloorBytes) continue;
            string name = tree.NameOf(i);
            int parentID = tree.Parent[i];
            bool inFolder = parentID >= 0 && parentID < underDownloads.Length && underDownloads[parentID];
            if (!(inFolder || name.StartsWith("downloads", StringComparison.OrdinalIgnoreCase))) continue;
            if (parentID >= 0 && parentID < underLibrary.Length && underLibrary[parentID]) continue;
            if (held[i]) continue;
            string abs = tree.PathOf(i, root);
            int day = tree.ModifiedDay[i];
            int age = day > 0 ? Math.Max(0, today - day) : 0;
            var kind = FileKinds.Classify(name, abs);
            var safety = SafetyClassifier.Assess(abs, name, false);
            if (safety.Level == SafetyLevel.Protected) continue;
            var status = ClassifyStatus(kind, age, name);
            string why = WhyHere(name, kind, bytes, age);
            string rec = Recommendation(status, kind);
            double score = bytes / 1_000_000.0 * (1.0 + age / 30.0)
                * (status == OldDownloadsStatus.LikelyDisposable ? 1.4 : 1.0);
            hits.Add(new OldDownloadsCandidate(
                NodeID: i,
                Name: name,
                AbsolutePath: abs,
                DisplayPath: CanonicalPath.DisplayPath(abs, home),
                Bytes: bytes,
                ModifiedDay: day,
                AgeDays: age,
                Kind: kind,
                Status: status,
                Safety: safety,
                WhyHere: why,
                Recommendation: rec,
                Score: score));
        }

        // Stable, like Swift's sort: equal sizes keep node order.
        var sorted = hits.OrderByDescending(c => c.Bytes).ToList();
        // Totals cover every match; only the list is capped (the header
        // said "500 files" for the cap on a Downloads holding 52k items).
        var summary = Summarize(sorted);
        if (sorted.Count > limit) sorted = sorted.Take(Math.Max(0, limit)).ToList();
        return new OldDownloadsCatalogResult(sorted, summary);
    }

    /// <summary>Mac rule on a '/'-separated copy of the path, so Windows separators match too.</summary>
    public static bool IsUnderDownloads(string path)
    {
        string lower = path.Replace('\\', '/').ToLowerInvariant();
        return lower.Contains("/downloads/") || lower.EndsWith("/downloads")
            || lower.Contains("/downloads");
    }

    /// <summary>Windows stand-in for the Mac's root.path.contains("/library").</summary>
    private static bool IsUnderAppData(string path)
    {
        string lower = path.Replace('\\', '/').ToLowerInvariant();
        return lower.Contains("/appdata") || lower.Contains("/programdata");
    }

    public static OldDownloadsStatus ClassifyStatus(FileKind kind, int ageDays, string name)
    {
        string n = name.ToLowerInvariant();
        // Installers / disk images older than 90 days → likely disposable
        if (kind == FileKind.DiskImage && ageDays >= 90) return OldDownloadsStatus.LikelyDisposable;
        if (kind == FileKind.Archive && ageDays >= 180
            && (n.Contains("installer") || n.Contains("setup") || n.EndsWith(".pkg.zip")))
        {
            return OldDownloadsStatus.LikelyDisposable;
        }
        return OldDownloadsStatus.ReviewFirst;
    }

    public static string WhyHere(string name, FileKind kind, long bytes, int ageDays)
    {
        string size = ShellFormat.Bytes(bytes);
        string age = AgeLabel(ageDays);
        return kind switch
        {
            FileKind.Video => $"Large video ({size}) in Downloads that hasn’t been modified recently ({age}).",
            FileKind.DiskImage => $"Installer or disk image ({size}) in Downloads, last modified {age}.",
            FileKind.Archive => $"Archive ({size}) in Downloads, last modified {age}.",
            FileKind.Document => $"Document ({size}) in Downloads, last modified {age}. Review carefully — it may still matter.",
            _ => $"{name} ({size}) in Downloads, last modified {age}.",
        };
    }

    public static string Recommendation(OldDownloadsStatus status, FileKind kind)
    {
        if (status == OldDownloadsStatus.LikelyDisposable)
            return "If the software is already installed and you don’t need the installer again, add it to Cleanup and review it before removing.";
        if (kind == FileKind.Video || kind == FileKind.Document)
            return "This looks like a personal file. freedisk.space can’t determine whether you still need it. Review before removing.";
        return "Review first. Old doesn’t mean unused — confirm you no longer need this before staging.";
    }

    public static string AgeLabel(int days)
    {
        if (days <= 0) return "unknown age";
        if (days < 30) return $"{days}d ago";
        if (days < 365) return $"{days / 30}mo ago";
        int y = days / 365;
        int m = days % 365 / 30;
        return m == 0 ? $"{y}y ago" : $"{y}y {m}m ago";
    }

    public static List<OldDownloadsCandidate> Filter(
        IEnumerable<OldDownloadsCandidate> candidates,
        OldDownloadsAgeFilter age,
        OldDownloadsSizeFilter size,
        OldDownloadsTypeFilter type,
        string query)
    {
        string q = query.Trim().ToLowerInvariant();
        return candidates.Where(c =>
        {
            if (c.AgeDays < age.MinAgeDays()) return false;
            if (c.Bytes < size.MinBytes()) return false;
            if (!type.Matches(c.Kind)) return false;
            if (q.Length == 0) return true;
            return c.Name.ToLowerInvariant().Contains(q)
                || c.DisplayPath.ToLowerInvariant().Contains(q)
                || c.Kind.Title().ToLowerInvariant().Contains(q);
        }).ToList();
    }

    /// <summary>Stable sorts, like Swift's. Name order is Finder's (localizedStandardCompare): case-insensitive, numbers by value.</summary>
    public static List<OldDownloadsCandidate> Sorted(IEnumerable<OldDownloadsCandidate> candidates, OldDownloadsSort sort) => sort switch
    {
        OldDownloadsSort.Oldest => candidates.OrderByDescending(c => c.AgeDays).ToList(),
        OldDownloadsSort.Newest => candidates.OrderBy(c => c.AgeDays).ToList(),
        OldDownloadsSort.Name => candidates.OrderBy(c => c.Name, CatalogText.StandardComparer).ToList(),
        OldDownloadsSort.Smart => candidates.OrderByDescending(c => c.Score).ToList(),
        _ => candidates.OrderByDescending(c => c.Bytes).ToList(),
    };

    private static readonly string[] AgeOrder = ["<30 days", "30–90 days", "90d–1 year", "1–2 years", "2+ years"];

    public static OldDownloadsSummary Summarize(IReadOnlyList<OldDownloadsCandidate> candidates)
    {
        long total = 0;
        long b30 = 0, b90 = 0, b365 = 0;
        int c30 = 0, c90 = 0, c365 = 0;
        var ageBytes = new long[AgeOrder.Length];
        var ageCounts = new int[AgeOrder.Length];
        var typeMap = new Dictionary<FileKind, (long Bytes, int Count)>();

        foreach (var c in candidates)
        {
            total += c.Bytes;
            if (c.AgeDays >= 30) { b30 += c.Bytes; c30++; }
            if (c.AgeDays >= 90) { b90 += c.Bytes; c90++; }
            if (c.AgeDays >= 365) { b365 += c.Bytes; c365++; }

            int bucket = c.AgeDays < 30 ? 0
                : c.AgeDays < 90 ? 1
                : c.AgeDays < 365 ? 2
                : c.AgeDays < 730 ? 3
                : 4;
            ageBytes[bucket] += c.Bytes;
            ageCounts[bucket]++;

            var t = typeMap.GetValueOrDefault(c.Kind);
            typeMap[c.Kind] = (t.Bytes + c.Bytes, t.Count + 1);
        }

        var ageBuckets = new List<OldDownloadsAgeBucket>();
        for (int k = 0; k < AgeOrder.Length; k++)
            if (ageBytes[k] > 0) ageBuckets.Add(new OldDownloadsAgeBucket(AgeOrder[k], ageBytes[k], ageCounts[k]));
        // Swift's dictionary order is arbitrary on a tie; kind order keeps it deterministic.
        var typeBuckets = typeMap
            .Select(kv => new OldDownloadsTypeBucket(kv.Key, kv.Value.Bytes, kv.Value.Count))
            .OrderByDescending(b => b.Bytes).ThenBy(b => b.Kind)
            .ToList();

        var insights = new List<string>();
        if (total > 0)
            insights.Add($"Downloads has {ShellFormat.Bytes(total)} of files matching this view.");
        var videos = candidates.Where(c => c.Kind == FileKind.Video).OrderByDescending(c => c.Bytes).ToList();
        if (videos.Count >= 1)
        {
            var top = videos.Take(3).ToList();
            long vBytes = top.Sum(c => c.Bytes);
            insights.Add($"{ShellFormat.Bytes(vBytes)} comes from {top.Count} large video file{(top.Count == 1 ? "" : "s")}.");
        }
        if (b365 > 0)
            insights.Add($"{ShellFormat.Bytes(b365)} hasn’t been modified in over a year.");
        insights.Add("Nothing here is automatically considered safe to delete.");

        return new OldDownloadsSummary(
            TotalBytes: total,
            TotalCount: candidates.Count,
            Bytes30: b30, Count30: c30,
            Bytes90: b90, Count90: c90,
            Bytes365: b365, Count365: c365,
            ReviewCount: candidates.Count,
            AgeBuckets: ageBuckets,
            TypeBuckets: typeBuckets,
            InsightLines: insights);
    }
}

/// <summary>Per-node folder flags the catalogs share (Mac: FileTree.folderChainFlags, StorageClassifier.systemHoldingFlags).</summary>
internal static class CatalogTree
{
    /// <summary>
    /// Per node: the node, or some folder between it and the root, is a directory whose name
    /// matches — or <paramref name="rootMatches"/>. One pass, no path building. Index 0 (the
    /// root) is <paramref name="rootMatches"/> alone.
    /// </summary>
    public static bool[] FolderChainFlags(FileTree tree, bool rootMatches, Func<string, bool> matches)
    {
        var flags = new bool[tree.Count];
        if (tree.Count == 0) return flags;
        flags[0] = rootMatches;
        for (int index = 1; index < tree.Count; index++)
        {
            int parentID = tree.Parent[index];
            bool inherited;
            if (parentID >= 0 && parentID < index)
            {
                inherited = flags[parentID];
            }
            else
            {
                // Not produced by the scanner or the snapshot codec; walk the chain rather
                // than trusting a later index.
                var chain = tree.AncestorIds(index);
                inherited = rootMatches;
                for (int k = 1; k < chain.Count - 1 && !inherited; k++)
                    inherited = tree.IsDirectory[chain[k]] && matches(tree.NameOf(chain[k]));
            }
            flags[index] = inherited || (tree.IsDirectory[index] && matches(tree.NameOf(index)));
        }
        return flags;
    }

    /// <summary>
    /// Per node: inside the Recycle Bin or System Volume Information — the Windows
    /// counterpart of the Mac's Trash/Spotlight holding folders
    /// (<see cref="StorageClassifier.IsSystemHolding"/>, per folder instead of per path).
    /// </summary>
    public static bool[] SystemHoldingFlags(FileTree tree, string root) =>
        FolderChainFlags(tree, StorageClassifier.IsSystemHolding(root),
            name => name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase));
}

internal static class CatalogText
{
    /// <summary>Finder's localizedStandardCompare: case-insensitive, digit runs by value ("file9" before "file10").</summary>
    public static readonly IComparer<string> StandardComparer = Comparer<string>.Create(StandardCompare);

    /// <summary>localizedCaseInsensitiveCompare: current culture, case-insensitive.</summary>
    public static readonly IComparer<string> CaseInsensitiveComparer = Comparer<string>.Create((a, b) =>
        string.Compare(a, b, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase));

    private static int StandardCompare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var da = a.AsSpan(si, i - si).TrimStart('0');
                var db = b.AsSpan(sj, j - sj).TrimStart('0');
                if (da.Length != db.Length) return da.Length.CompareTo(db.Length);
                int digits = da.SequenceCompareTo(db);
                if (digits != 0) return Math.Sign(digits);
                continue;
            }
            int ti = i, tj = j;
            while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
            while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
            int text = string.Compare(a[ti..i], b[tj..j], CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            if (text != 0) return text;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
