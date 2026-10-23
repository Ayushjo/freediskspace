namespace DiskMap.Core;

// Forgotten files: the user's own big files, untouched for a year or more, with how sure we
// are and why (Mac: ForgottenFiles.swift). Thresholds, scores and strings match the Mac;
// the path rules run on a lowercased '/'-separated copy of the path so the Mac's rules apply
// unchanged, with the Windows counterparts of each Mac location added beside them
// (AppData for ~/Library, Program Files for .app bundles, Videos beside Movies).

public enum ForgottenConfidence { LikelyForgotten, WorthReviewing, OldImportant }

/// <summary>Age buckets used only for Forgotten candidates (over a year).</summary>
public enum ForgottenAgeBucket { OneToTwoYears, TwoToThreeYears, ThreeToFiveYears, OverFiveYears }

public static class ForgottenText
{
    public static string Title(this ForgottenConfidence confidence) => confidence switch
    {
        ForgottenConfidence.LikelyForgotten => "Likely forgotten",
        ForgottenConfidence.WorthReviewing => "Worth reviewing",
        _ => "Old, but probably important",
    };

    public static string ShortTitle(this ForgottenAgeBucket bucket) => bucket switch
    {
        ForgottenAgeBucket.OneToTwoYears => "1–2y",
        ForgottenAgeBucket.TwoToThreeYears => "2–3y",
        ForgottenAgeBucket.ThreeToFiveYears => "3–5y",
        _ => "5y+",
    };

    public static string Title(this ForgottenAgeBucket bucket) => bucket switch
    {
        ForgottenAgeBucket.OneToTwoYears => "1–2 years",
        ForgottenAgeBucket.TwoToThreeYears => "2–3 years",
        ForgottenAgeBucket.ThreeToFiveYears => "3–5 years",
        _ => "Over 5 years",
    };

    /// <summary>The bucket for an age in days; null at a year or less.</summary>
    public static ForgottenAgeBucket? Bucket(int ageDays)
    {
        if (ageDays <= 365) return null;
        if (ageDays < 730) return ForgottenAgeBucket.OneToTwoYears;
        if (ageDays < 1095) return ForgottenAgeBucket.TwoToThreeYears;
        if (ageDays < 1825) return ForgottenAgeBucket.ThreeToFiveYears;
        return ForgottenAgeBucket.OverFiveYears;
    }
}

public sealed record ForgottenCandidate(
    int Id,
    string Name,
    string AbsolutePath,
    string DisplayPath,
    string ParentDisplay,
    long Bytes,
    int ModifiedDay,
    int AgeDays,
    FileKind Kind,
    ForgottenConfidence Confidence,
    SafetyAssessment Safety,
    IReadOnlyList<string> Reasons,
    double Score)
{
    public bool IsReviewable =>
        Confidence is ForgottenConfidence.LikelyForgotten or ForgottenConfidence.WorthReviewing;
}

public sealed record ForgottenSummary(
    long ReviewableBytes,
    long LikelyBytes,
    long WorthBytes,
    long ExcludedBytes,
    int ReviewableCount,
    int LikelyCount,
    int WorthCount,
    int ExcludedCount,
    IReadOnlyDictionary<ForgottenAgeBucket, long> AgeDistribution)
{
    public static readonly ForgottenSummary Empty =
        new(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<ForgottenAgeBucket, long>());
}

public static class ForgottenFiles
{
    /// <summary>Minimum age (days) to be considered forgotten.</summary>
    public const int MinAgeDays = 365;
    /// <summary>Tiny files are noise even if ancient.</summary>
    public const long MinBytes = 1_000_000;
    /// <summary>The default list prefers meaningful storage impact for medium confidence.</summary>
    public const long WorthMinBytes = 25_000_000;

    /// <summary>
    /// Files older than a year and at least 1 MB, scored and capped, best first.
    /// <paramref name="today"/>: days since 1970; 0 means <see cref="AgeMap.Today"/>.
    /// <paramref name="home"/> is the profile shown as "~"; null means <see cref="UserFolders.Home"/>.
    /// </summary>
    public static List<ForgottenCandidate> Candidates(
        FileTree tree, string root, long[] totals, int today = 0, int limit = 500, string? home = null)
    {
        if (tree.Count != totals.Length || limit <= 0) return [];
        if (today == 0) today = AgeMap.Today();
        var held = CatalogTree.SystemHoldingFlags(tree, root);
        var output = new List<ForgottenCandidate>();
        for (int id = 1; id < tree.Count; id++)
        {
            if (tree.IsDirectory[id] || held[id]) continue;
            long bytes = totals[id];
            if (bytes < MinBytes) continue;
            int day = tree.ModifiedDay[id];
            if (day <= 0) continue;
            int age = today - day;
            if (age <= MinAgeDays) continue;

            string name = tree.NameOf(id);
            string abs = tree.PathOf(id, root);
            var safety = SafetyClassifier.Assess(abs, name, false);
            var kind = FileKinds.Classify(name, abs);
            var confidence = ClassifyConfidence(abs, name, kind, safety, bytes);
            output.Add(new ForgottenCandidate(
                Id: id,
                Name: name,
                AbsolutePath: abs,
                DisplayPath: CanonicalPath.DisplayPath(abs, home),
                ParentDisplay: CanonicalPath.ParentDisplay(abs, home),
                Bytes: bytes,
                ModifiedDay: day,
                AgeDays: age,
                Kind: kind,
                Confidence: confidence,
                Safety: safety,
                Reasons: BuildReasons(age, bytes, abs, kind, confidence, safety),
                Score: ReviewScore(age, bytes, confidence, kind, safety)));
        }
        return output
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Bytes)
            .Take(limit)
            .ToList();
    }

    public static ForgottenSummary Summary(IEnumerable<ForgottenCandidate> candidates)
    {
        long likely = 0, worth = 0, excluded = 0;
        int likelyN = 0, worthN = 0, excludedN = 0;
        var distribution = new Dictionary<ForgottenAgeBucket, long>();
        foreach (var c in candidates)
        {
            switch (c.Confidence)
            {
                case ForgottenConfidence.LikelyForgotten: likely += c.Bytes; likelyN++; break;
                case ForgottenConfidence.WorthReviewing: worth += c.Bytes; worthN++; break;
                default: excluded += c.Bytes; excludedN++; break;
            }
            if (c.IsReviewable && ForgottenText.Bucket(c.AgeDays) is { } bucket)
                distribution[bucket] = distribution.GetValueOrDefault(bucket) + c.Bytes;
        }
        return new ForgottenSummary(likely + worth, likely, worth, excluded,
            likelyN + worthN, likelyN, worthN, excludedN, distribution);
    }

    // ---- Classification ----

    /// <summary>The path, lowercased, with '\' folded to '/' so one rule set reads both.</summary>
    private static string Folded(string path) => path.Replace('\\', '/').ToLowerInvariant();

    public static ForgottenConfidence ClassifyConfidence(string path, string name, FileKind kind,
        SafetyAssessment safety, long bytes)
    {
        string lower = Folded(path);
        if (safety.Level == SafetyLevel.Protected) return ForgottenConfidence.OldImportant;
        if (kind == FileKind.VirtualDisk || name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
            return ForgottenConfidence.OldImportant;
        if (IsInsideApplication(lower)) return ForgottenConfidence.OldImportant;
        if (IsDeveloperOrToolchain(lower)) return ForgottenConfidence.OldImportant;
        if (IsManagedData(lower, name.ToLowerInvariant())) return ForgottenConfidence.OldImportant;

        bool personal = IsPersonalLocation(lower);
        bool highType = IsHighSignalType(kind);
        if (personal && highType) return ForgottenConfidence.LikelyForgotten;
        if (personal && bytes >= 50_000_000) return ForgottenConfidence.LikelyForgotten;
        if (personal) return bytes >= WorthMinBytes ? ForgottenConfidence.WorthReviewing : ForgottenConfidence.OldImportant;
        // Regenerable caches (npm, etc.) are cleaned via Clean up — not Forgotten recommendations.
        if (safety.Level == SafetyLevel.Safe) return ForgottenConfidence.OldImportant;
        // Mac: outside /Library/. Windows: outside AppData and ProgramData, the same role.
        if (safety.Level == SafetyLevel.Review && bytes >= 100_000_000 && !lower.Contains("/library/")
            && !lower.Contains("/appdata/") && !lower.Contains("/programdata/"))
            return ForgottenConfidence.WorthReviewing;
        return ForgottenConfidence.OldImportant;
    }

    /// <summary>Mac: inside an .app bundle. Windows: inside an installed program's folder.</summary>
    private static bool IsInsideApplication(string lower) =>
        lower.Contains(".app/contents/") || lower.Contains(".app/frameworks/")
        || lower.Contains("/program files/") || lower.Contains("/program files (x86)/")
        || lower.Contains("/windowsapps/") || lower.Contains("/appdata/local/programs/");

    private static readonly string[] ToolchainMarkers =
    [
        "/library/developer/", "/xcode.app/", "/commandlinetools/", "/.swiftpm/", "/deriveddata/",
        // Package-manager sources preserve ancient packaging mtimes — not "forgotten".
        "/.cargo/", "/.rustup/", "/.gradle/", "/.m2/", "/go/pkg/mod/", "/.nuget/", "/.cocoapods/",
        "/.pub-cache/", "/library/caches/cocoapods/", "/node_modules/", "/.npm/", "/.pnpm-store/",
        "/.yarn/", "/.cache/pip/",
        // Windows: SDKs, the IDE and its extensions.
        "/microsoft visual studio/", "/windows kits/", "/android/sdk/", "/.vscode/extensions/",
    ];

    private static bool IsDeveloperOrToolchain(string lower) => ToolchainMarkers.Any(lower.Contains);

    private static bool IsManagedData(string lower, string name)
    {
        if (lower.Contains("/docker.raw") || name == "docker.raw") return true;
        if (lower.Contains("/library/containers/com.docker")) return true;
        if (lower.Contains("/library/group containers/group.com.docker")) return true;
        if (lower.Contains("/virtualbox vms/")) return true;
        if (lower.Contains("/parallels/") && lower.Contains(".pvm")) return true;
        // Windows: Docker's WSL data and Hyper-V's virtual machines.
        if (lower.Contains("/docker/wsl/") || lower.Contains("/hyper-v/")) return true;
        return false;
    }

    private static readonly string[] PersonalMarkers =
        ["/downloads/", "/movies/", "/videos/", "/desktop/", "/documents/", "/pictures/", "/music/", "/public/"];

    private static bool IsPersonalLocation(string lower)
    {
        // ~/Library and /Library hold app data even when a folder inside is called
        // "downloads"; on Windows AppData and ProgramData play that role.
        if (lower.StartsWith("/library/") || lower.StartsWith("/system/volumes/data/library/")) return false;
        if (lower.Contains("/appdata/") || lower.Contains("/programdata/")) return false;
        int users = lower.IndexOf("/users/", StringComparison.Ordinal);
        if (users >= 0)
        {
            var parts = lower[(users + "/users/".Length)..].Split('/', 3);
            if (parts.Length > 2 && parts[1] is "library" or "appdata") return false;
        }
        if (PersonalMarkers.Any(lower.Contains)) return true;
        // Loose files in the profile itself: \Users\name\file.
        if (users >= 0)
        {
            var parts = lower[(users + "/users/".Length)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) return true;
        }
        return false;
    }

    private static bool IsHighSignalType(FileKind kind) =>
        kind is FileKind.Video or FileKind.DiskImage or FileKind.Archive or FileKind.DeviceBackup;

    private static List<string> BuildReasons(int ageDays, long bytes, string path, FileKind kind,
        ForgottenConfidence confidence, SafetyAssessment safety)
    {
        var reasons = new List<string>();
        int years = Math.Max(1, ageDays / 365);
        if (years >= 15) reasons.Add("Very old modification date (often preserved from packaging)");
        else if (years == 1) reasons.Add("Not modified in over a year");
        else reasons.Add($"Not modified in {years} years");
        if (bytes >= 1_000_000_000) reasons.Add("Very large file");
        else if (bytes >= 100_000_000) reasons.Add("Large file");
        string lower = Folded(path);
        if (lower.Contains("/downloads/")) reasons.Add("Located in Downloads");
        else if (lower.Contains("/movies/")) reasons.Add("Located in Movies");
        else if (lower.Contains("/videos/")) reasons.Add("Located in Videos");
        else if (lower.Contains("/desktop/")) reasons.Add("Located in Desktop");
        else if (lower.Contains("/documents/")) reasons.Add("Located in Documents");
        switch (kind)
        {
            case FileKind.Video: reasons.Add("Personal media file"); break;
            case FileKind.DiskImage: reasons.Add("Disk image / installer"); break;
            case FileKind.Archive: reasons.Add("Archive"); break;
            case FileKind.DeviceBackup: reasons.Add("Device backup"); break;
        }
        if (confidence == ForgottenConfidence.OldImportant) reasons.Add(safety.Reason);
        return reasons;
    }

    private static double ReviewScore(int ageDays, long bytes, ForgottenConfidence confidence, FileKind kind,
        SafetyAssessment safety)
    {
        double sizeSignal = Math.Min(40.0, Math.Log2(Math.Max(bytes, 1) / 1_000_000.0 + 1.0) * 6.0);
        double ageSignal = Math.Min(25.0, (ageDays - 365) / 365.0 * 8.0);
        double typeBoost = IsHighSignalType(kind) ? 12.0 : 0.0;
        double confBoost = confidence switch
        {
            ForgottenConfidence.LikelyForgotten => 30.0,
            ForgottenConfidence.WorthReviewing => 12.0,
            _ => -40.0,
        };
        double risk = 0.0;
        if (safety.Level == SafetyLevel.Protected) risk = 50.0;
        if (safety.Level == SafetyLevel.Review && confidence != ForgottenConfidence.LikelyForgotten) risk += 5.0;
        return sizeSignal + ageSignal + typeBoost + confBoost - risk;
    }
}
