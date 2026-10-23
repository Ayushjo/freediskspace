namespace DiskMap.Core;

// Large Media: personal video, images, audio and media projects anywhere in the scan, always
// Review first (Mac: MediaCatalog.swift). Thresholds, sort orders and strings match the Mac
// character for character; Windows differences are commented where they occur.

/// <summary>Media kinds (not <see cref="FileKind"/> — a media-only taxonomy).</summary>
public enum MediaKind { Video, Image, Audio, Project, Other }

public enum MediaTypeFilter { All, Video, Image, Audio, Project, Other }

public enum MediaSizeFilter { Any, Mb100, Mb500, Gb1, Gb5, Gb10 }

public enum MediaAgeFilter { All, Days30, Days90, Months6, Year1 }

/// <summary>Where a media file sits. Windows: the Mac's Movies folder is Videos.</summary>
public enum MediaLocationBucket { Downloads, Desktop, Videos, Pictures, Music, Documents, Other }

public enum MediaLocationFilter { Any, Downloads, Desktop, Videos, Pictures, Music, Documents, Other }

public enum MediaSort { Largest, Oldest, Newest, Name, Opportunities }

/// <summary>Personal media is Review first. Never "safe to delete" from age alone.</summary>
public enum MediaStatus { ReviewFirst, LikelyDisposable }

public static class MediaCatalogExtensions
{
    public static string RawValue(this MediaKind kind) => kind switch
    {
        MediaKind.Video => "video",
        MediaKind.Image => "image",
        MediaKind.Audio => "audio",
        MediaKind.Project => "project",
        _ => "other",
    };

    public static string Title(this MediaKind kind) => kind switch
    {
        MediaKind.Video => "Video",
        MediaKind.Image => "Images",
        MediaKind.Audio => "Audio",
        MediaKind.Project => "Media Projects",
        _ => "Other",
    };

    public static string ShortTitle(this MediaKind kind) => kind switch
    {
        MediaKind.Video => "Video",
        MediaKind.Image => "Image",
        MediaKind.Audio => "Audio",
        MediaKind.Project => "Project",
        _ => "Media",
    };

    /// <summary>The Mac's SF Symbol name; the app maps it to its own icon.</summary>
    public static string SymbolName(this MediaKind kind) => kind switch
    {
        MediaKind.Video => "film",
        MediaKind.Image => "photo",
        MediaKind.Audio => "waveform",
        MediaKind.Project => "rectangle.stack",
        _ => "play.rectangle",
    };

    public static string Title(this MediaTypeFilter filter) => filter switch
    {
        MediaTypeFilter.Video => "Video",
        MediaTypeFilter.Image => "Images",
        MediaTypeFilter.Audio => "Audio",
        MediaTypeFilter.Project => "Projects",
        MediaTypeFilter.Other => "Other",
        _ => "All",
    };

    public static bool Matches(this MediaTypeFilter filter, MediaKind kind) => filter switch
    {
        MediaTypeFilter.Video => kind == MediaKind.Video,
        MediaTypeFilter.Image => kind == MediaKind.Image,
        MediaTypeFilter.Audio => kind == MediaKind.Audio,
        MediaTypeFilter.Project => kind == MediaKind.Project,
        MediaTypeFilter.Other => kind == MediaKind.Other,
        _ => true,
    };

    public static string Title(this MediaSizeFilter filter) => filter switch
    {
        MediaSizeFilter.Mb100 => ">100 MB",
        MediaSizeFilter.Mb500 => ">500 MB",
        MediaSizeFilter.Gb1 => ">1 GB",
        MediaSizeFilter.Gb5 => ">5 GB",
        MediaSizeFilter.Gb10 => ">10 GB",
        _ => "Any",
    };

    public static long MinBytes(this MediaSizeFilter filter) => filter switch
    {
        MediaSizeFilter.Mb100 => 100_000_000,
        MediaSizeFilter.Mb500 => 500_000_000,
        MediaSizeFilter.Gb1 => 1_000_000_000,
        MediaSizeFilter.Gb5 => 5_000_000_000,
        MediaSizeFilter.Gb10 => 10_000_000_000,
        _ => 0,
    };

    public static string Title(this MediaAgeFilter filter) => filter switch
    {
        MediaAgeFilter.Days30 => "30d+",
        MediaAgeFilter.Days90 => "90d+",
        MediaAgeFilter.Months6 => "6mo+",
        MediaAgeFilter.Year1 => "1y+",
        _ => "Any",
    };

    public static int MinAgeDays(this MediaAgeFilter filter) => filter switch
    {
        MediaAgeFilter.Days30 => 30,
        MediaAgeFilter.Days90 => 90,
        MediaAgeFilter.Months6 => 180,
        MediaAgeFilter.Year1 => 365,
        _ => 0,
    };

    public static string Title(this MediaLocationFilter filter) => filter switch
    {
        MediaLocationFilter.Downloads => "Downloads",
        MediaLocationFilter.Desktop => "Desktop",
        MediaLocationFilter.Videos => "Videos",
        MediaLocationFilter.Pictures => "Pictures",
        MediaLocationFilter.Music => "Music",
        MediaLocationFilter.Documents => "Documents",
        MediaLocationFilter.Other => "Other",
        _ => "Any",
    };

    public static bool Matches(this MediaLocationFilter filter, MediaLocationBucket location) => filter switch
    {
        MediaLocationFilter.Downloads => location == MediaLocationBucket.Downloads,
        MediaLocationFilter.Desktop => location == MediaLocationBucket.Desktop,
        MediaLocationFilter.Videos => location == MediaLocationBucket.Videos,
        MediaLocationFilter.Pictures => location == MediaLocationBucket.Pictures,
        MediaLocationFilter.Music => location == MediaLocationBucket.Music,
        MediaLocationFilter.Documents => location == MediaLocationBucket.Documents,
        MediaLocationFilter.Other => location == MediaLocationBucket.Other,
        _ => true,
    };

    public static string RawValue(this MediaLocationBucket location) => location switch
    {
        MediaLocationBucket.Downloads => "downloads",
        MediaLocationBucket.Desktop => "desktop",
        MediaLocationBucket.Videos => "videos",
        MediaLocationBucket.Pictures => "pictures",
        MediaLocationBucket.Music => "music",
        MediaLocationBucket.Documents => "documents",
        _ => "other",
    };

    public static string Title(this MediaLocationBucket location) => location switch
    {
        MediaLocationBucket.Downloads => "Downloads",
        MediaLocationBucket.Desktop => "Desktop",
        MediaLocationBucket.Videos => "Videos",
        MediaLocationBucket.Pictures => "Pictures",
        MediaLocationBucket.Music => "Music",
        MediaLocationBucket.Documents => "Documents",
        _ => "Other",
    };

    public static string Title(this MediaSort sort) => sort switch
    {
        MediaSort.Oldest => "Oldest first",
        MediaSort.Newest => "Newest first",
        MediaSort.Name => "Name",
        MediaSort.Opportunities => "Best cleanup opportunities",
        _ => "Largest first",
    };

    public static string Title(this MediaStatus status) => status switch
    {
        MediaStatus.LikelyDisposable => "Likely disposable",
        _ => "Review first",
    };
}

public sealed record MediaCandidate(
    int NodeID,
    string Name,
    string AbsolutePath,
    string DisplayPath,
    long Bytes,
    int ModifiedDay,
    int AgeDays,
    MediaKind Kind,
    MediaLocationBucket Location,
    MediaStatus Status,
    SafetyAssessment Safety,
    string WhyHere,
    string Recommendation,
    bool IsDirectory,
    double Score,
    /// <summary>Optional UI hint (filled lazily in the app layer).</summary>
    string? DurationLabel = null,
    string? DimensionsLabel = null)
{
    public int Id => NodeID;
}

public sealed record MediaBucket(string Id, string Title, long Bytes, int Count, double Fraction);

public sealed record MediaSummary(
    long TotalBytes,
    int TotalCount,
    long VideoBytes,
    int VideoCount,
    long ImageBytes,
    int ImageCount,
    long AudioBytes,
    int AudioCount,
    long ProjectBytes,
    int ProjectCount,
    int Over1GBCount,
    IReadOnlyList<MediaBucket> TypeBuckets,
    IReadOnlyList<MediaBucket> LocationBuckets,
    IReadOnlyList<MediaBucket> AgeBuckets,
    IReadOnlyList<string> InsightLines)
{
    public static readonly MediaSummary Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], [], []);
}

public sealed record MediaCatalogResult(
    IReadOnlyList<MediaCandidate> Candidates,
    MediaSummary Summary,
    IReadOnlyList<MediaCandidate> Opportunities)
{
    /// <summary>
    /// More media matched than `limit`: <see cref="Candidates"/> and <see cref="Summary"/>
    /// cover the largest `limit` only, and the UI must say so.
    /// </summary>
    public bool IsTruncated { get; init; }

    public static readonly MediaCatalogResult Empty = new([], MediaSummary.Empty, []);
}

public static class MediaCatalog
{
    /// <summary>Floor for inclusion in the catalog (images can be smaller than videos).</summary>
    public const long ListingFloorBytes = 500_000;

    // Explicit non-media — never appear even if large. Windows adds its own disk and
    // installer formats (vhd, vhdx, msix, appx, wim, cab).
    private static readonly HashSet<string> NonMediaExtensions =
    [
        "dmg", "iso", "pkg", "img", "app", "zip", "tar", "gz", "tgz", "7z", "rar", "bz2",
        "exe", "msi", "deb", "rpm", "apk", "ipa",
        "vmdk", "vdi", "qcow2", "sparseimage", "sparsebundle",
        "sqlite", "db", "sql", "pdf", "doc", "docx", "pages", "txt", "rtf", "md",
        "csv", "json", "xml", "plist", "log", "bin", "dat",
        "vhd", "vhdx", "avhdx", "msix", "msixbundle", "appx", "appxbundle", "wim", "cab",
    ];

    private static readonly HashSet<string> VideoExtensions =
    [
        "mp4", "mov", "mkv", "avi", "m4v", "webm", "wmv", "flv", "mpeg", "mpg", "m2ts", "mts", "ts", "vob", "3gp",
    ];

    private static readonly HashSet<string> ImageExtensions =
    [
        "jpg", "jpeg", "png", "heic", "heif", "webp", "gif", "tiff", "tif", "bmp", "dng", "cr2", "nef", "orf", "arw", "rw2", "psd", "ico",
    ];

    private static readonly HashSet<string> AudioExtensions =
    [
        "mp3", "wav", "flac", "aac", "m4a", "ogg", "aiff", "aif", "wma", "alac", "opus", "caf",
    ];

    // The Mac's libraries and creator-app projects, plus the Windows editors' project
    // files: Vegas (.veg), Camtasia (.tscproj), Filmora (.wfp), FL Studio (.flp),
    // Ableton Live (.als), Cubase (.cpr), Audition (.sesx), Kdenlive (.kdenlive).
    // Premiere (.prproj), After Effects (.aep) and DaVinci Resolve (.drp) are shared.
    private static readonly HashSet<string> ProjectExtensions =
    [
        "fcpbundle", "logicx", "band", "photoslibrary", "imovielibrary", "tvlibrary", "musiclibrary",
        "finalcutproject", "fcpxml", "prproj", "aep", "drp",
        "veg", "tscproj", "wfp", "flp", "als", "cpr", "sesx", "kdenlive",
    ];

    public static MediaKind? Classify(string fileName, string path = "", bool isDirectory = false)
    {
        string n = fileName.ToLowerInvariant();
        string ext = PathExtension(n);
        string p = path.Replace('\\', '/').ToLowerInvariant();

        // Packages / libraries treated as media projects (often directories).
        if (ProjectExtensions.Contains(ext)
            || n.EndsWith(".photoslibrary")
            || n.EndsWith(".imovielibrary")
            || n.EndsWith(".fcpbundle")
            || n.EndsWith(".logicx")
            || n.EndsWith(".band")
            || (p.Contains("/final cut") && isDirectory))
        {
            return MediaKind.Project;
        }

        if (isDirectory) return null;

        if (NonMediaExtensions.Contains(ext)) return null;
        // Clone/installer naming without relying on extension alone.
        if (n.Contains(".clone.") && (ext == "dmg" || n.EndsWith(".dmg"))) return null;

        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        if (ImageExtensions.Contains(ext)) return MediaKind.Image;
        if (AudioExtensions.Contains(ext)) return MediaKind.Audio;

        // UTType-ish fallbacks via name patterns (no shell lookups in Core).
        if (n.StartsWith("img_") && (ext.Length == 0 || ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext)))
        {
            if (VideoExtensions.Contains(ext)) return MediaKind.Video;
            if (ImageExtensions.Contains(ext) || ext.Length == 0) return MediaKind.Image;
        }

        return null;
    }

    /// <summary>
    /// NSString.pathExtension: the text after the last dot, but none for a leading-dot name
    /// (".mp4"), a trailing dot, or an extension containing a space — checked against Foundation.
    /// </summary>
    internal static string PathExtension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1) return "";
        string ext = fileName[(dot + 1)..];
        if (ext.Contains(' ')) return "";
        if (fileName.AsSpan(0, dot).Trim('.').IsEmpty) return "";
        return ext;
    }

    /// <summary>
    /// Mac rule on a '/'-separated copy of the path. Windows: Videos is the Mac's Movies
    /// (a folder literally named Movies counts too).
    /// </summary>
    public static MediaLocationBucket LocationBucket(string path)
    {
        string lower = path.Replace('\\', '/').ToLowerInvariant();
        if (lower.Contains("/downloads/") || lower.EndsWith("/downloads")) return MediaLocationBucket.Downloads;
        if (lower.Contains("/desktop/") || lower.EndsWith("/desktop")) return MediaLocationBucket.Desktop;
        if (lower.Contains("/videos/") || lower.EndsWith("/videos")
            || lower.Contains("/movies/") || lower.EndsWith("/movies"))
        {
            return MediaLocationBucket.Videos;
        }
        if (lower.Contains("/pictures/") || lower.EndsWith("/pictures")) return MediaLocationBucket.Pictures;
        if (lower.Contains("/music/") || lower.EndsWith("/music")) return MediaLocationBucket.Music;
        if (lower.Contains("/documents/") || lower.EndsWith("/documents")) return MediaLocationBucket.Documents;
        return MediaLocationBucket.Other;
    }

    /// <summary>
    /// Windows' own media, never the user's. Mac: /System, the Preboot and VM volumes,
    /// /private/var/vm and /Library outside /Users. Windows, on any drive: \Windows (its
    /// wallpapers and sounds), \ProgramData (the system-wide Library), \Recovery, \Boot,
    /// System Volume Information and the Windows Update staging folders ($Windows.~BT/~WS).
    /// </summary>
    public static bool IsExcludedSystemPath(string path)
    {
        string lower = path.Replace('\\', '/').ToLowerInvariant();
        // Strip the drive ("c:") so the folder is compared from the drive root.
        if (lower.Length >= 2 && lower[1] == ':') lower = lower[2..];
        foreach (string folder in ExcludedRootFolders)
        {
            if (lower == "/" + folder || lower.StartsWith("/" + folder + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static readonly string[] ExcludedRootFolders =
    [
        "windows", "programdata", "recovery", "boot", "system volume information",
        "$windows.~bt", "$windows.~ws",
    ];

    /// <summary>
    /// <paramref name="today"/>: days since 1970-01-01 UTC; 0 means
    /// <see cref="AgeMap.Today"/> (DateTimeOffset.UtcNow). <paramref name="home"/>: the
    /// profile shown as "~" in <see cref="MediaCandidate.DisplayPath"/>; null means
    /// <see cref="UserFolders.Home"/>.
    /// </summary>
    public static MediaCatalogResult Build(
        FileTree tree,
        string root,
        long[] totals,
        int today = 0,
        int limit = 2000,
        string? home = null)
    {
        if (totals.Length != tree.Count) return MediaCatalogResult.Empty;
        if (today == 0) today = AgeMap.Today();

        // Only the "/final cut" rule in `Classify` reads the path, and only for
        // folders. Precompute it once, classify by name, and build a path only
        // for actual matches (Mac TASK-042).
        var finalCut = CatalogTree.FolderChainFlags(tree,
            root.Replace('\\', '/').ToLowerInvariant().Contains("/final cut"),
            name => name.StartsWith("final cut", StringComparison.OrdinalIgnoreCase));
        // Pass 1: cheap, name-only matching. Pass 2 builds full candidates in
        // size order and stops once `limit` survive the path-based filters.
        // Ties keep node order.
        var matches = new List<(int Id, long Bytes, MediaKind Kind, string Name)>();
        // Mac: Trash, Spotlight … Windows: Recycle Bin, System Volume Information.
        var held = CatalogTree.SystemHoldingFlags(tree, root);
        for (int i = 0; i < tree.Count; i++)
        {
            bool isDir = tree.IsDirectory[i];
            long bytes = totals[i];
            if (bytes < ListingFloorBytes || held[i]) continue;
            string name = tree.NameOf(i);
            var classified = Classify(name, isDirectory: isDir);
            if (classified is null && isDir && finalCut[i]) classified = MediaKind.Project;
            if (classified is not { } kind) continue;
            // ".ts" is MPEG transport stream video and TypeScript source; no
            // TypeScript file is anywhere near 20 MB (25k of them were listed
            // as video on a real Desktop).
            if (kind == MediaKind.Video && name.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                && bytes < 20_000_000)
            {
                continue;
            }
            // For directories, only keep media projects (already gated in Classify).
            if (isDir && kind != MediaKind.Project) continue;
            matches.Add((i, bytes, kind, name));
        }
        matches.Sort((a, b) => a.Bytes != b.Bytes ? b.Bytes.CompareTo(a.Bytes) : a.Id.CompareTo(b.Id));

        var hits = new List<MediaCandidate>(Math.Max(0, Math.Min(limit, matches.Count)));
        foreach (var (id, bytes, kind, name) in matches)
        {
            if (hits.Count >= limit) break;
            bool isDir = tree.IsDirectory[id];
            string abs = tree.PathOf(id, root);
            if (IsExcludedSystemPath(abs)) continue;

            var safety = SafetyClassifier.Assess(abs, name, isDir);
            if (safety.Level == SafetyLevel.Protected) continue;

            int day = tree.ModifiedDay[id];
            int age = day > 0 ? Math.Max(0, today - day) : 0;
            var location = LocationBucket(abs);
            var status = ClassifyStatus(kind, age, name);
            string why = WhyHere(kind, bytes, age, location);
            string rec = Recommendation(status, kind);
            double score = OpportunityScore(bytes, age, kind, location, status);

            hits.Add(new MediaCandidate(
                NodeID: id,
                Name: name,
                AbsolutePath: abs,
                DisplayPath: CanonicalPath.DisplayPath(abs, home),
                Bytes: bytes,
                ModifiedDay: day,
                AgeDays: age,
                Kind: kind,
                Location: location,
                Status: status,
                Safety: safety,
                WhyHere: why,
                Recommendation: rec,
                IsDirectory: isDir,
                Score: score));
        }

        var summary = Summarize(hits);
        var opportunities = hits.Where(c => c.Bytes >= 1_000_000_000).Take(5).ToList();
        var opp = opportunities.Count == 0 ? hits.Take(5).ToList() : opportunities;
        return new MediaCatalogResult(hits, summary, opp)
        {
            IsTruncated = hits.Count >= limit && matches.Count > limit,
        };
    }

    public static MediaStatus ClassifyStatus(MediaKind kind, int ageDays, string name)
    {
        string n = name.ToLowerInvariant();
        // Never mark personal video/photos as disposable from age alone.
        if (kind is MediaKind.Video or MediaKind.Image or MediaKind.Project)
            return MediaStatus.ReviewFirst;
        // Screen recordings / scratch audio mixes sometimes disposable when old.
        if (kind == MediaKind.Audio && ageDays >= 365
            && (n.Contains("mix") || n.Contains("bounce") || n.Contains("render") || n.Contains("scratch")))
        {
            return MediaStatus.LikelyDisposable;
        }
        return MediaStatus.ReviewFirst;
    }

    public static string WhyHere(MediaKind kind, long bytes, int ageDays, MediaLocationBucket location)
    {
        string size = ShellFormat.Bytes(bytes);
        string age = AgeLabel(ageDays);
        return kind switch
        {
            MediaKind.Video => $"Large video ({size}) in {location.Title()} that hasn’t been modified in {age}.",
            MediaKind.Image => $"Large image ({size}) in {location.Title()}. Unusual size for a still — review before removing.",
            MediaKind.Audio => $"Audio file ({size}) in {location.Title()}, last modified {age} ago.",
            MediaKind.Project => $"Media project/library ({size}). Projects often contain caches and media — review inside the creator app when possible.",
            _ => $"Media-related item ({size}) in {location.Title()}.",
        };
    }

    public static string Recommendation(MediaStatus status, MediaKind kind) => status switch
    {
        MediaStatus.LikelyDisposable => "Looks like scratch/export media that may be safe to review, but confirm before removing.",
        _ => "Personal media. freedisk.space can’t determine whether you still need it — review first.",
    };

    public static double OpportunityScore(
        long bytes, int ageDays, MediaKind kind, MediaLocationBucket location, MediaStatus status)
    {
        double s = bytes / 1_000_000.0 * (1.0 + ageDays / 60.0);
        if (location is MediaLocationBucket.Downloads or MediaLocationBucket.Desktop) s *= 1.25;
        if (kind == MediaKind.Video) s *= 1.1;
        if (status == MediaStatus.LikelyDisposable) s *= 1.3;
        return s;
    }

    public static string AgeLabel(int days)
    {
        if (days < 30) return $"{days}d";
        if (days < 365) return $"{Math.Max(1, days / 30)}m";
        int y = days / 365;
        int m = days % 365 / 30;
        return m == 0 ? $"{y}y" : $"{y}y {m}m";
    }

    public static List<MediaCandidate> Filter(
        IEnumerable<MediaCandidate> items,
        MediaTypeFilter type,
        MediaSizeFilter size,
        MediaAgeFilter age,
        MediaLocationFilter location,
        string query)
    {
        string q = query.Trim().ToLowerInvariant();
        return items.Where(c =>
        {
            if (!type.Matches(c.Kind)) return false;
            if (c.Bytes < size.MinBytes()) return false;
            if (c.AgeDays < age.MinAgeDays()) return false;
            if (!location.Matches(c.Location)) return false;
            if (q.Length == 0) return true;
            if (q == "video") return c.Kind == MediaKind.Video;
            if (q is "image" or "photo" or "photos") return c.Kind == MediaKind.Image;
            if (q is "audio" or "music") return c.Kind == MediaKind.Audio || c.Location == MediaLocationBucket.Music;
            if (q == "downloads") return c.Location == MediaLocationBucket.Downloads;
            return c.Name.ToLowerInvariant().Contains(q)
                || c.DisplayPath.ToLowerInvariant().Contains(q)
                || c.AbsolutePath.ToLowerInvariant().Contains(q)
                || c.Kind.Title().ToLowerInvariant().Contains(q)
                || c.Location.Title().ToLowerInvariant().Contains(q);
        }).ToList();
    }

    /// <summary>Stable sorts, like Swift's. Name order is localizedCaseInsensitiveCompare.</summary>
    public static List<MediaCandidate> Sorted(IEnumerable<MediaCandidate> items, MediaSort sort) => sort switch
    {
        MediaSort.Oldest => items.OrderByDescending(c => c.AgeDays).ToList(),
        MediaSort.Newest => items.OrderBy(c => c.AgeDays).ToList(),
        MediaSort.Name => items.OrderBy(c => c.Name, CatalogText.CaseInsensitiveComparer).ToList(),
        MediaSort.Opportunities => items.OrderByDescending(c => c.Score).ToList(),
        _ => items.OrderByDescending(c => c.Bytes).ToList(),
    };

    private static readonly MediaLocationBucket[] LocationOrder =
    [
        MediaLocationBucket.Downloads, MediaLocationBucket.Desktop, MediaLocationBucket.Videos,
        MediaLocationBucket.Pictures, MediaLocationBucket.Music, MediaLocationBucket.Documents,
        MediaLocationBucket.Other,
    ];

    public static MediaSummary Summarize(IReadOnlyList<MediaCandidate> items)
    {
        long total = 0;
        long videoB = 0, imageB = 0, audioB = 0, projectB = 0, otherB = 0;
        int videoC = 0, imageC = 0, audioC = 0, projectC = 0, otherC = 0;
        int over1 = 0;
        var locBytes = new Dictionary<MediaLocationBucket, long>();
        var locCount = new Dictionary<MediaLocationBucket, int>();
        long ageA = 0, ageB = 0, ageC = 0, ageD = 0, ageE = 0;
        int ageAc = 0, ageBc = 0, ageCc = 0, ageDc = 0, ageEc = 0;

        foreach (var c in items)
        {
            total += c.Bytes;
            if (c.Bytes >= 1_000_000_000) over1++;
            switch (c.Kind)
            {
                case MediaKind.Video: videoB += c.Bytes; videoC++; break;
                case MediaKind.Image: imageB += c.Bytes; imageC++; break;
                case MediaKind.Audio: audioB += c.Bytes; audioC++; break;
                case MediaKind.Project: projectB += c.Bytes; projectC++; break;
                default: otherB += c.Bytes; otherC++; break;
            }
            locBytes[c.Location] = locBytes.GetValueOrDefault(c.Location) + c.Bytes;
            locCount[c.Location] = locCount.GetValueOrDefault(c.Location) + 1;
            switch (c.AgeDays)
            {
                case < 30: ageA += c.Bytes; ageAc++; break;
                case < 90: ageB += c.Bytes; ageBc++; break;
                case < 365: ageC += c.Bytes; ageCc++; break;
                case < 730: ageD += c.Bytes; ageDc++; break;
                default: ageE += c.Bytes; ageEc++; break;
            }
        }

        double Frac(long b) => total > 0 ? (double)b / total : 0;

        var typeBuckets = new List<MediaBucket>
        {
            new("video", "Video", videoB, videoC, Frac(videoB)),
            new("image", "Images", imageB, imageC, Frac(imageB)),
            new("audio", "Audio", audioB, audioC, Frac(audioB)),
            new("project", "Media Projects", projectB, projectC, Frac(projectB)),
            new("other", "Other", otherB, otherC, Frac(otherB)),
        }.Where(b => b.Bytes > 0 || b.Count > 0).ToList();

        var locationBuckets = new List<MediaBucket>();
        foreach (var loc in LocationOrder)
        {
            long b = locBytes.GetValueOrDefault(loc);
            int count = locCount.GetValueOrDefault(loc);
            if (b <= 0) continue;
            locationBuckets.Add(new MediaBucket(loc.RawValue(), loc.Title(), b, count, Frac(b)));
        }

        var ageBuckets = new List<MediaBucket>
        {
            new("a30", "< 30 days", ageA, ageAc, Frac(ageA)),
            new("a90", "30–90 days", ageB, ageBc, Frac(ageB)),
            new("a365", "90 days–1 year", ageC, ageCc, Frac(ageC)),
            new("a730", "1–2 years", ageD, ageDc, Frac(ageD)),
            new("a2p", "2+ years", ageE, ageEc, Frac(ageE)),
        }.Where(b => b.Bytes > 0).ToList();

        var insights = new List<string>();
        if (locationBuckets.Count > 0)
        {
            var topLoc = locationBuckets[0];
            insights.Add($"{ShellFormat.Bytes(topLoc.Bytes)} of media is in {topLoc.Title}.");
        }
        if (ageE > 0)
            insights.Add($"{ShellFormat.Bytes(ageE)} of media hasn’t been modified in over 2 years.");
        else if (ageD > 0)
            insights.Add($"{ShellFormat.Bytes(ageD)} of media hasn’t been modified in over a year.");
        if (videoB > 0)
            insights.Add($"Video accounts for {(int)Math.Round(Frac(videoB) * 100, MidpointRounding.AwayFromZero)}% of media storage.");

        return new MediaSummary(
            TotalBytes: total, TotalCount: items.Count,
            VideoBytes: videoB, VideoCount: videoC,
            ImageBytes: imageB, ImageCount: imageC,
            AudioBytes: audioB, AudioCount: audioC,
            ProjectBytes: projectB, ProjectCount: projectC,
            Over1GBCount: over1,
            TypeBuckets: typeBuckets, LocationBuckets: locationBuckets, AgeBuckets: ageBuckets,
            InsightLines: insights);
    }
}
