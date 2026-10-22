namespace DiskMap.Core;

public enum ReviewableCategory { Caches, BuildArtifacts, PackageCaches, Other }

public static class ReviewableCategoryText
{
    public static string Title(this ReviewableCategory category) => category switch
    {
        ReviewableCategory.Caches => "Caches",
        ReviewableCategory.BuildArtifacts => "Build & dependencies",
        ReviewableCategory.PackageCaches => "Package caches",
        _ => "Other reviewable",
    };
}

/// <summary>
/// One thing Clean up and Overview offer: an app's caches grouped together ("Chrome"), or a
/// quick-win folder ("npm cache", "Node modules"). Mac: ReviewableTargets.swift.
/// </summary>
public sealed record ReviewableTarget(
    string Id,
    ReviewableCategory Category,
    string DisplayName,
    string Detail,
    long Bytes,
    /// <summary>The path to stage or show (the first path).</summary>
    string PrimaryPath,
    IReadOnlyList<string> Paths,
    IReadOnlyList<int> NodeIds,
    SafetyAssessment Safety,
    string Consequence,
    /// <summary>The Mac's SF Symbol name; the app maps it to its own icon.</summary>
    string Symbol,
    string? BundleHint)
{
    public bool IsGenerallySafe => Safety.Level == SafetyLevel.Safe;
    public bool IsReviewFirst => Safety.Level == SafetyLevel.Review;
    public bool IsProtected => Safety.Level == SafetyLevel.Protected;
}

public sealed record ReviewableSummary(
    long TotalBytes, long CacheBytes, long BuildBytes, long PackageBytes, long OtherBytes,
    int TargetCount, int CacheAppCount)
{
    public static readonly ReviewableSummary Empty = new(0, 0, 0, 0, 0, 0, 0);
}

public static class ReviewableCatalog
{
    public static (List<ReviewableTarget> Targets, ReviewableSummary Summary) Build(
        FileTree tree, string root, long[] totals, IReadOnlyList<QuickWins.Hit> quickWins)
    {
        if (tree.Count == 0 || totals.Length != tree.Count) return ([], ReviewableSummary.Empty);
        var targets = new List<ReviewableTarget>();
        var used = new HashSet<int>();

        // 1) App caches under AppData, grouped per app (Mac: Library/Caches children).
        foreach (var group in GroupCaches(tree, root, totals))
        {
            foreach (int id in group.NodeIds) used.Add(id);
            targets.Add(group);
        }

        // 2) Quick-win folders the cache groups didn't take.
        foreach (var hit in quickWins)
        {
            int id = hit.Id;
            if (used.Contains(id) || id < 0 || id >= tree.Count || !tree.IsDirectory[id]) continue;
            if (HasAncestorIn(tree, id, used)) continue;
            // A quick win that contains grouped caches only offers what's left beside them.
            long bytes = totals[id] - used.Where(u => IsAncestor(tree, id, u)).Sum(u => totals[u]);
            if (bytes <= 0) continue;
            string path = tree.PathOf(id, root);
            string name = tree.NameOf(id);
            var safety = SafetyClassifier.Assess(path, name, true);
            if (safety.Level == SafetyLevel.Protected) continue;
            var category = Categorize(name, path);
            used.Add(id);
            targets.Add(new ReviewableTarget(
                Id: $"qw-{id}",
                Category: category,
                DisplayName: DisplayName(name, path, category),
                Detail: Detail(name, category),
                Bytes: bytes,
                PrimaryPath: path,
                Paths: [path],
                NodeIds: [id],
                Safety: safety,
                Consequence: Consequence(category, name, safety),
                Symbol: SymbolFor(category),
                BundleHint: null));
        }

        targets.Sort((a, b) => b.Bytes != a.Bytes ? b.Bytes.CompareTo(a.Bytes)
            : string.CompareOrdinal(a.Id, b.Id));
        return (targets, Summarize(targets));
    }

    public static ReviewableSummary Summarize(IEnumerable<ReviewableTarget> targets)
    {
        long cache = 0, build = 0, package = 0, other = 0;
        int count = 0, cacheApps = 0;
        foreach (var t in targets)
        {
            count++;
            switch (t.Category)
            {
                case ReviewableCategory.Caches: cache += t.Bytes; cacheApps++; break;
                case ReviewableCategory.BuildArtifacts: build += t.Bytes; break;
                case ReviewableCategory.PackageCaches: package += t.Bytes; break;
                default: other += t.Bytes; break;
            }
        }
        return new ReviewableSummary(cache + build + package + other, cache, build, package, other, count, cacheApps);
    }

    // ---- Cache grouping ----

    private sealed class Acc
    {
        public long Bytes;
        public readonly List<string> Paths = [];
        public readonly List<int> NodeIds = [];
        public required string Detail;
        public required SafetyAssessment Safety;
        public required string Symbol;
        public string? BundleHint;
        public required string Consequence;
    }

    /// <summary>
    /// Every outermost cache folder (Cache, Code Cache, GPUCache, cache2, INetCache …, plus
    /// Spotify's Data) under the scan's AppData\Local or AppData\Roaming, summed per app.
    /// </summary>
    private static List<ReviewableTarget> GroupCaches(FileTree tree, string root, long[] totals)
    {
        var byApp = new Dictionary<string, Acc>(StringComparer.Ordinal);
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (id != 0 && tree.IsDirectory[id] && IsCacheCandidate(tree, id)
                && totals[id] > 0 && CacheRoot(tree, root, id) is { } location)
            {
                Absorb(byApp, location, id, totals[id]);
                continue; // outermost only: never count a cache inside a cache twice
            }
            for (int child = tree.FirstChild[id]; child != -1; child = tree.NextSibling[child])
                if (tree.IsDirectory[child]) stack.Push(child);
        }
        return byApp.Select(kv => new ReviewableTarget(
                Id: $"cache-{kv.Key}",
                Category: ReviewableCategory.Caches,
                DisplayName: kv.Key,
                Detail: kv.Value.Detail,
                Bytes: kv.Value.Bytes,
                PrimaryPath: kv.Value.Paths.Order(StringComparer.Ordinal).First(),
                Paths: kv.Value.Paths.Order(StringComparer.Ordinal).ToList(),
                NodeIds: kv.Value.NodeIds,
                Safety: kv.Value.Safety.Level == SafetyLevel.Protected
                    ? new SafetyAssessment(SafetyLevel.Review, kv.Value.Safety.Reason, kv.Key)
                    : kv.Value.Safety,
                Consequence: kv.Value.Consequence,
                Symbol: kv.Value.Symbol,
                BundleHint: kv.Value.BundleHint))
            .OrderByDescending(t => t.Bytes).ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsCacheCandidate(FileTree tree, int id)
    {
        string name = tree.NameOf(id);
        if (SafetyClassifier.CacheFolderNames.Contains(name)) return true;
        // Spotify keeps its song cache in Data, beside the app.
        return name.Equals("Data", StringComparison.OrdinalIgnoreCase)
            && tree.Parent[id] is int parent and >= 0
            && tree.NameOf(parent).Equals("Spotify", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CacheLocation(string Path, string[] AppSegments);

    /// <summary>The cache's path and the folders between AppData\Local|Roaming and it, or null when it isn't under AppData.</summary>
    private static CacheLocation? CacheRoot(FileTree tree, string root, int id)
    {
        string path = tree.PathOf(id, root);
        string[] parts = path.Split('\\', '/');
        for (int i = 0; i + 2 < parts.Length; i++)
        {
            if (!parts[i].Equals("AppData", StringComparison.OrdinalIgnoreCase)) continue;
            string area = parts[i + 1];
            if (!area.Equals("Local", StringComparison.OrdinalIgnoreCase)
                && !area.Equals("Roaming", StringComparison.OrdinalIgnoreCase)) continue;
            string[] inner = parts[(i + 2)..];
            // Temp is its own quick win; never fold it into an app.
            if (inner.Length > 0 && inner[0].Equals("Temp", StringComparison.OrdinalIgnoreCase)) return null;
            return new CacheLocation(path, inner);
        }
        return null;
    }

    private static void Absorb(Dictionary<string, Acc> byApp, CacheLocation location, int id, long bytes)
    {
        var mapped = MapCacheFolder(location);
        var safety = SafetyClassifier.Assess(location.Path, location.AppSegments[^1], true);
        if (!byApp.TryGetValue(mapped.App, out var acc))
        {
            acc = new Acc
            {
                Detail = mapped.Detail, Safety = safety, Symbol = mapped.Symbol,
                BundleHint = mapped.BundleHint, Consequence = mapped.Consequence,
            };
            byApp[mapped.App] = acc;
        }
        acc.Bytes += bytes;
        acc.Paths.Add(location.Path);
        acc.NodeIds.Add(id);
        if (acc.Safety.Level == SafetyLevel.Safe && safety.Level == SafetyLevel.Review) acc.Safety = safety;
    }

    private sealed record Mapped(string App, string Detail, string Symbol, string? BundleHint, string Consequence);

    /// <summary>Vendor folders that hold one folder per product (Google\Chrome, Microsoft\Edge).</summary>
    private static readonly HashSet<string> VendorContainers = new(
        ["Google", "Microsoft", "Adobe", "Mozilla", "BraveSoftware", "JetBrains", "Packages", "Programs"],
        StringComparer.OrdinalIgnoreCase);

    private static Mapped MapCacheFolder(CacheLocation location)
    {
        string inner = string.Join('\\', location.AppSegments).ToLowerInvariant();
        static Mapped M(string app, string detail, string symbol, string? bundle, string consequence) =>
            new(app, detail, symbol, bundle, consequence);

        if (inner.StartsWith(@"google\chrome"))
            return M("Chrome", "Browser cache", "globe", "Google.Chrome",
                "Chrome can recreate this. Some sites may load more slowly next time.");
        if (inner.StartsWith(@"microsoft\edge"))
            return M("Edge", "Browser cache", "globe", "Microsoft.Edge",
                "Edge can recreate this. Some sites may load more slowly next time.");
        if (inner.StartsWith(@"bravesoftware\brave"))
            return M("Brave", "Browser cache", "globe", null, "Brave can recreate this cache.");
        if (inner.StartsWith("chromium")) return M("Chromium", "Browser cache", "globe", null, "Browser can recreate this cache.");
        if (inner.StartsWith(@"mozilla\firefox"))
            return M("Firefox", "Browser cache", "globe", "Mozilla.Firefox", "Firefox can recreate this cache.");
        if (inner.StartsWith("opera software")) return M("Opera", "Browser cache", "globe", null, "Opera can recreate this cache.");
        if (inner.StartsWith("vivaldi")) return M("Vivaldi", "Browser cache", "globe", null, "Vivaldi can recreate this cache.");
        if (inner.StartsWith(@"microsoft\windows\inetcache"))
            return M("Internet cache", "Windows web cache", "globe", null,
                "Windows and older apps can recreate this cache.");
        if (inner.StartsWith("spotify"))
            return M("Spotify", "Cached media", "music.note", "Spotify.Spotify",
                "Some songs or podcasts may need to be downloaded again.");
        if (inner.StartsWith("adobe"))
            return M("Adobe", "Application cache", "paintbrush.pointed", null,
                "Adobe apps can recreate cache data. First launch afterward may be slower.");
        if (inner.StartsWith(@"code\") || inner.StartsWith(@"code - insiders\"))
            return M("VS Code", "Extension / application cache", "chevron.left.forwardslash.chevron.right",
                "Microsoft.VisualStudioCode", "VS Code can recreate extension and app caches.");
        if (inner.StartsWith("slack"))
            return M("Slack", "Application cache", "bubble.left.and.bubble.right", "SlackTechnologies.Slack",
                "Slack can recreate this. Recent media may re-download.");
        if (inner.StartsWith("discord")) return M("Discord", "Application cache", "bubble.left", null, "Discord can recreate this cache.");
        if (inner.StartsWith(@"microsoft\teams") || inner.StartsWith(@"packages\msteams"))
            return M("Teams", "Application cache", "bubble.left.and.bubble.right", null, "Teams can recreate this cache.");
        if (inner.StartsWith("zoom")) return M("Zoom", "Application cache", "video", "Zoom.Zoom", "Zoom can recreate this cache.");
        if (inner.StartsWith("docker")) return M("Docker", "Docker cache", "shippingbox", "Docker.DockerDesktop",
            "Prefer Docker's own cleanup for images/volumes when possible.");
        if (inner.StartsWith("figma")) return M("Figma", "Application cache", "paintpalette", null, "Figma can recreate this cache.");
        if (inner.StartsWith("notion")) return M("Notion", "Application cache", "doc.text", null, "Notion can recreate this cache.");
        if (inner.StartsWith("telegram")) return M("Telegram", "Application cache", "paperplane", null, "Some media may re-download.");
        if (inner.StartsWith(@"yarn\"))
            return M("Yarn", "Package download cache", "shippingbox", null, "Yarn re-downloads packages as needed.");
        if (inner.StartsWith(@"pnpm\"))
            return M("pnpm", "Package store cache", "shippingbox", null, "Projects may need pnpm install again.");
        if (inner.StartsWith(@"pip\"))
            return M("pip", "Package download cache", "shippingbox", null, "pip re-downloads packages as needed.");

        // Otherwise the app is the first folder under AppData (or the product under a vendor folder).
        string[] seg = location.AppSegments;
        string app = seg.Length > 1 && VendorContainers.Contains(seg[0]) && seg.Length > 2 ? seg[1] : seg[0];
        return M(app, "Application cache", "internaldrive", null,
            "Apps can usually recreate this. You may see slower first runs or re-downloads.");
    }

    // ---- Quick-win targets ----

    private static readonly HashSet<string> BuildNames = new(
        ["node_modules", "build", "dist", "out", "target", ".next", ".venv", "venv", ".build", ".nuxt", ".turbo",
         ".parcel-cache", ".svelte-kit", ".expo", "pods", "__pycache__", ".dart_tool"],
        StringComparer.OrdinalIgnoreCase);

    private static ReviewableCategory Categorize(string name, string path)
    {
        string n = name.ToLowerInvariant();
        string p = path.Replace('/', '\\').ToLowerInvariant();
        if (n == "deriveddata" || n == "inetcache" || SafetyClassifier.CacheFolderNames.Contains(n)) return ReviewableCategory.Caches;
        if (BuildNames.Contains(n)) return ReviewableCategory.BuildArtifacts;
        if (n is "npm-cache" or ".npm" or ".pnpm-store" or ".cargo" or ".gradle" or ".pub-cache" or "v3-cache" or "plugins-cache"
            || p.Contains(@"\.cargo\registry") || p.Contains(@"\.gradle\caches") || p.Contains(@"\nuget\"))
            return ReviewableCategory.PackageCaches;
        return ReviewableCategory.Other;
    }

    private static string DisplayName(string name, string path, ReviewableCategory category)
    {
        string p = path.Replace('/', '\\').ToLowerInvariant();
        switch (category)
        {
            case ReviewableCategory.BuildArtifacts:
                return name switch
                {
                    "node_modules" => "Node modules",
                    "target" => "Rust target",
                    "build" => "Build output",
                    ".build" => "Swift build",
                    _ => name,
                };
            case ReviewableCategory.PackageCaches:
                if (name is "npm-cache" or ".npm") return "npm cache";
                if (name == ".cargo" || p.Contains(@"\.cargo\")) return "Cargo cache";
                if (name == ".gradle" || p.Contains(@"\.gradle\")) return "Gradle cache";
                if (p.Contains(@"\nuget\")) return "NuGet cache";
                return name;
            case ReviewableCategory.Caches:
                return name == "DerivedData" ? "Xcode DerivedData" : name;
            default:
                if (p.Contains(@"\android\sdk\system-images")) return "Android system images";
                if (name.Equals("Temp", StringComparison.OrdinalIgnoreCase) && p.Contains(@"\appdata\local\temp")) return "Temporary files";
                if (name.Equals("CrashDumps", StringComparison.OrdinalIgnoreCase)) return "Crash dumps";
                return name;
        }
    }

    private static string Detail(string name, ReviewableCategory category) => category switch
    {
        ReviewableCategory.Caches => "Application cache",
        ReviewableCategory.BuildArtifacts => name switch
        {
            "node_modules" => "Project dependencies",
            "target" => "Rust / Cargo build output",
            ".venv" or "venv" => "Python environment (installed packages)",
            _ => "Generated development output",
        },
        ReviewableCategory.PackageCaches => "Downloaded package data",
        _ => "Reviewable storage",
    };

    private static string Consequence(ReviewableCategory category, string name, SafetyAssessment safety)
    {
        // A rule's own line wins; the level's generic "could delete personal files" line must
        // not replace what we know about build output.
        if (safety.HasSpecificConsequences || category == ReviewableCategory.Other) return safety.Consequences;
        return category switch
        {
            ReviewableCategory.Caches => "Apps can recreate this. First launch afterward may be slower or re-download data.",
            ReviewableCategory.BuildArtifacts => name == "node_modules"
                ? "Reinstall dependencies (npm/yarn/pnpm install) before the project runs again."
                : "The project may need to rebuild. The next build can take longer.",
            ReviewableCategory.PackageCaches => "Package managers can re-download as needed.",
            _ => "Inspect before clearing. freedisk.space cannot guarantee this is unused.",
        };
    }

    private static string SymbolFor(ReviewableCategory category) => category switch
    {
        ReviewableCategory.Caches => "internaldrive",
        ReviewableCategory.BuildArtifacts => "hammer",
        ReviewableCategory.PackageCaches => "shippingbox",
        _ => "folder",
    };

    private static bool IsAncestor(FileTree tree, int ancestor, int id)
    {
        for (int p = tree.Parent[id]; p >= 0; p = tree.Parent[p])
            if (p == ancestor) return true;
        return false;
    }

    private static bool HasAncestorIn(FileTree tree, int id, HashSet<int> set)
    {
        for (int p = tree.Parent[id]; p >= 0; p = tree.Parent[p])
            if (set.Contains(p)) return true;
        return false;
    }
}
