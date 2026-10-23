namespace DiskMap.Core;

/// <summary>How sure the app is that clearing an item is harmless (Mac: SafetyClassification.swift).</summary>
public enum SafetyLevel
{
    /// <summary>Rebuilds by itself: caches and package downloads. Clean up ticks these for you.</summary>
    Safe,
    /// <summary>Comes back, but check first: dependencies, build output, environments, unknown paths.</summary>
    Review,
    /// <summary>Never offered: Windows itself, saved credentials and keys.</summary>
    Protected,
}

public static class SafetyLevelText
{
    public static string Title(this SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => "Safe to review",
        SafetyLevel.Review => "Review first",
        _ => "Protected",
    };
}

/// <summary>One verdict: the level, why, what clearing it costs and what to do instead.</summary>
public sealed record SafetyAssessment
{
    public SafetyLevel Level { get; }
    public string Reason { get; }
    public string Title { get; }
    public string Consequences { get; }
    public string RecommendedAction { get; }

    public SafetyAssessment(SafetyLevel level, string reason, string title,
        string consequences = "", string recommendedAction = "")
    {
        Level = level;
        Reason = reason;
        Title = title;
        Consequences = consequences.Length == 0 ? DefaultConsequences(level) : consequences;
        RecommendedAction = recommendedAction.Length == 0 ? DefaultAction(level) : recommendedAction;
    }

    /// <summary>True when a rule wrote its own consequences rather than the level's generic line.</summary>
    public bool HasSpecificConsequences => Consequences != DefaultConsequences(Level);

    public static string DefaultConsequences(SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => "Apps or tools can usually recreate this data. You may see slower first runs.",
        SafetyLevel.Review => "Removing the wrong item could delete personal files or break a project until you restore it.",
        _ => "Removing this can break Windows or lock you out of critical data.",
    };

    private static string DefaultAction(SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => "Add to Cleanup, then review it before moving it to the Recycle Bin.",
        SafetyLevel.Review => "Inspect contents, show it in File Explorer, then stage only what you recognize.",
        _ => "Do not remove. Leave system and credential data alone.",
    };
}

/// <summary>
/// Deterministic path and name rules, the Mac's table with Windows locations. Unknown paths
/// default to Review — never confident-green.
/// </summary>
public static class SafetyClassifier
{
    /// <summary>Folder names apps use for disposable caches (Chromium, Electron, Firefox, WinINet).</summary>
    public static readonly IReadOnlySet<string> CacheFolderNames = new HashSet<string>(
        ["Cache", "Code Cache", "GPUCache", "cache2", "INetCache", "ShaderCache", "GrShaderCache", "DawnCache"],
        StringComparer.OrdinalIgnoreCase);

    public static SafetyAssessment Assess(string path, string name, bool isDirectory)
    {
        string p = Normalize(path);
        string n = name.ToLowerInvariant();

        // ---- Protected: Windows itself, the page file, credentials and keys ----
        if (AtDriveRoot(p, "windows") || AtDriveRoot(p, "system volume information")
            || AtDriveRoot(p, "$recycle.bin") || AtDriveRoot(p, "recovery")
            || AtDriveRoot(p, @"programdata\microsoft"))
        {
            return new SafetyAssessment(SafetyLevel.Protected,
                "System location. Removing items here can break Windows.", name,
                "Windows may fail to start or update; many files here are locked anyway.",
                "Do not stage. Use Settings > System > Storage if you need space.");
        }
        if (!isDirectory && (n is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys"))
        {
            return new SafetyAssessment(SafetyLevel.Protected,
                "Windows memory file (paging or hibernation).", name,
                "Windows manages its size; it can't be moved while Windows runs.",
                "Change virtual memory or hibernation in Windows settings instead.");
        }
        if (p.Contains(@"\microsoft\credentials") || p.Contains(@"\microsoft\protect")
            || p.Contains(@"\microsoft\vault") || p.Contains(@"\microsoft\crypto")
            || p.Contains(@"\microsoft\systemcertificates"))
        {
            return new SafetyAssessment(SafetyLevel.Protected,
                "Saved credentials, certificates and the keys that protect them.", "Credentials",
                "You can lose saved passwords and break app sign-ins.",
                "Never remove via freedisk.space.");
        }
        if (n == "appdata" && p.EndsWith(@"\appdata") && p.Contains(@"\users\"))
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "AppData holds app data, settings and caches. Review carefully before removing anything.",
                "AppData");
        }

        // ---- Known regenerable caches and build products ----
        if (n is "npm-cache" or ".npm" || p.Contains(@"\npm-cache\") || p.EndsWith(@"\npm-cache"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "npm cache stores downloaded packages so installs are faster. npm can recreate it.",
                "npm cache", "Next npm install may re-download packages (slower once).",
                "Add to Cleanup and review before removing.");
        }
        if (n is ".pnpm-store" or ".pnpm" || p.Contains(@"\pnpm\cache") || p.Contains(@"\pnpm\store")
            || p.Contains(@"\.pnpm-store"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "pnpm content-addressable store. pnpm can rehydrate packages after a clear.",
                "pnpm store", "Projects may need pnpm install again.",
                "Safe to clear if you can reinstall dependencies.");
        }
        if (n is ".yarn" or "yarn-cache" || p.Contains(@"\yarn\cache") || p.Contains(@"\.yarn\cache"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "Yarn package cache. Yarn recreates it on the next install.", "Yarn cache",
                "Yarn re-downloads packages as needed.");
        }
        if (p.Contains(@"\nuget\v3-cache") || p.Contains(@"\nuget\plugins-cache") || p.Contains(@"\nuget\http-cache"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "NuGet download cache. The next restore downloads what it needs again.", "NuGet cache",
                "Next restore may re-download packages (slower once).");
        }
        if (CacheFolderNames.Contains(n) && (p.Contains(@"\appdata\local\") || p.Contains(@"\appdata\roaming\")))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "Application caches. Apps usually recreate them; you may need to re-download some data.",
                "Caches");
        }
        if (p.Contains(@"\appdata\local\spotify\data") || p.Contains(@"\appdata\local\spotify\storage"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "Spotify's cached songs and podcasts. Spotify downloads them again when played.", "Spotify cache",
                "Some songs or podcasts may need to be downloaded again.");
        }
        if (n == "deriveddata" || p.Contains(@"\developer\xcode\deriveddata"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "Xcode DerivedData holds build products. Xcode regenerates it on the next build.",
                "Xcode DerivedData", "Next Xcode build will be slower until DerivedData rebuilds.",
                "Safe to clear when you are not mid-build.");
        }
        if (n == ".cargo" || p.Contains(@"\.cargo\registry"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "Cargo registry/cache for Rust crates. Cargo re-downloads as needed.", "Cargo cache");
        }
        if (n == ".gradle" || p.Contains(@"\.gradle\caches"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "Gradle caches. Builds re-download dependencies afterward.", "Gradle cache");
        }
        if (n == ".pub-cache" || p.Contains(@"\pub\cache"))
        {
            return new SafetyAssessment(SafetyLevel.Safe,
                "Dart/Flutter pub cache. flutter pub get restores packages.", "Pub cache");
        }

        // ---- Comes back, but check first ----
        if (n == "node_modules")
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Project dependencies. Safe to delete if you can reinstall with npm/yarn/pnpm, but the project won't run until you do.",
                "node_modules", "Project won't build or run until you reinstall dependencies.",
                "Stage only unused project folders you recognize.");
        }
        if (p.Contains(@"\android\sdk\system-images") || p.Contains(@"\.android\avd") || n == "system-images")
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Android emulator images. Usually re-downloadable but Android Studio has to fetch them again.",
                "Android emulator images", "Android Studio may re-download system images.",
                "Review size in Developer Storage before staging.");
        }
        if (n == ".cursor" || p.Contains(@"\.cursor"))
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Cursor IDE data (caches, indexes, or agent state). Clearing caches is usually safe; review project data carefully.",
                "Cursor data");
        }
        if (n == ".codex" || p.Contains(@"\.codex"))
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Codex / AI coding tool data. Confirm you don't need session history before removing.",
                "Codex data");
        }
        if (n == ".docker" || p.Contains(@"\docker\wsl") || p.Contains(@"\appdata\local\docker"))
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Docker images, volumes, or VM data. Removing volumes can delete containerized databases.",
                "Docker data", "Images and named volumes may need to be pulled or recreated.",
                "Prefer Docker Desktop's cleanup for volumes; stage only caches you recognize.");
        }
        if (n == ".rustup")
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Rust toolchains installed via rustup. Removing them uninstalls compilers until you reinstall.",
                "rustup toolchains");
        }
        if (n is "venv" or ".venv" or "__pycache__" or ".mypy_cache")
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Python environment or bytecode cache. Recreatable if you have requirements/lockfiles.", name);
        }
        if (n == "temp" && p.Contains(@"\appdata\local\temp"))
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Temporary files. Most are leftovers, but an installer or app may be using some right now.",
                "Temporary files", "Files still in use are skipped; an unfinished install may need restarting.");
        }
        if (n == "downloads")
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "User Downloads. May contain installers you can remove and unique files you need to keep.",
                "Downloads");
        }
        if (n is "documents" or "desktop" or "pictures" or "videos" or "music")
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "User documents folder. Treat contents as personal data until proven otherwise.", name);
        }
        if (!isDirectory && (n.EndsWith(".exe") || n.EndsWith(".msi") || n.EndsWith(".zip") || n.EndsWith(".iso")))
        {
            return new SafetyAssessment(SafetyLevel.Review,
                "Installer or archive. Often safe after the software is installed — confirm you don't need it.", name);
        }

        return new SafetyAssessment(SafetyLevel.Review,
            "No confident rule for this path. Inspect contents before removing.", name);
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    /// <summary>"c:\windows" or "c:\windows\…", on any drive letter.</summary>
    private static bool AtDriveRoot(string p, string folder)
    {
        if (p.Length < 3 || p[1] != ':' || p[2] != '\\') return false;
        string rest = p[3..];
        return rest == folder || rest.StartsWith(folder + "\\", StringComparison.Ordinal);
    }
}
