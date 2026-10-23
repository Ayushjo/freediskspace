using System.Globalization;

namespace DiskMap.Core;

/// <summary>Where a program came from (Mac: ApplicationSource).</summary>
public enum ApplicationSource { Store, Other }

/// <summary>
/// What a leftover is, by where it sits (Mac: ApplicationRelatedKind). Windows keeps an
/// app's data in AppData, ProgramData and Documents rather than ~/Library.
/// </summary>
public enum ApplicationRelatedKind { Caches, Logs, RoamingData, LocalData, ProgramData, Documents, Programs, Other }

/// <summary>One place an app keeps data outside its install folder.</summary>
public sealed record ApplicationRelatedItem(ApplicationRelatedKind Kind, string Path, long Bytes);

/// <summary>
/// One installed program with its own size and the data it keeps elsewhere (Mac:
/// ApplicationEntry). Windows never removes a program's own files: Clean up stages only the
/// leftovers, and the program is uninstalled from Settings or its own uninstaller.
/// </summary>
public sealed record ApplicationEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    /// <summary>The install folder, or "" when the registry names none (or a too-broad one).</summary>
    public string InstallPath { get; init; } = "";
    /// <summary>The program's own size: its install folder, else the registry's estimate.</summary>
    public long AppBytes { get; init; }
    public IReadOnlyList<ApplicationRelatedItem> Related { get; init; } = [];
    public ApplicationSource Source { get; init; } = ApplicationSource.Other;
    /// <summary>The last launch Windows recorded; null is unknown, not never.</summary>
    public DateTimeOffset? LastUsed { get; init; }
    public DateTimeOffset? Installed { get; init; }
    /// <summary>The registry's own uninstall command line — copied for the user, never run.</summary>
    public string? UninstallCommand { get; init; }
    /// <summary>True while sizes and leftovers are still being measured.</summary>
    public bool SizePending { get; init; }

    public long RelatedBytes => Related.Sum(r => r.Bytes);
    public long TotalBytes => AppBytes + RelatedBytes;
    public bool IsLarge => TotalBytes >= ApplicationsCatalog.LargeThresholdBytes;
    public bool IsNotRecentlyUsed(DateTimeOffset now) => ApplicationsCatalog.IsNotRecentlyUsed(LastUsed, now);
    /// <summary>Something can go in the basket: the leftovers (the program itself never does).</summary>
    public bool CanStageForCleanup => Related.Count > 0;
}

/// <summary>Mac: ApplicationsCatalog.Sort.</summary>
public enum ApplicationSort { SizeDesc, SizeAsc, NameAsc, LastUsedDesc, LastUsedAsc, Source }

/// <summary>
/// The Applications page's logic (Mac: ApplicationsCatalog.swift and the pure parts of
/// AppsView.swift): "not opened in 6 months", sorting, the search, which leftovers are
/// ticked for you, and what uninstalling frees.
/// </summary>
public static class ApplicationsCatalog
{
    public const long LargeThresholdBytes = 1_000_000_000;
    public const int NotRecentlyUsedDays = 180;

    /// <summary>Whole days from one moment to another (Calendar.dateComponents([.day])).</summary>
    public static int DaysBetween(DateTimeOffset from, DateTimeOffset to) => (int)Math.Floor((to - from).TotalDays);

    public static bool IsNotRecentlyUsed(DateTimeOffset? lastUsed, DateTimeOffset now) =>
        lastUsed is { } last && DaysBetween(last, now) >= NotRecentlyUsedDays;

    /// <summary>"Today", "Yesterday", "4 months ago"; "—" when Windows has no launch date (unknown, not never).</summary>
    public static string LastOpenedText(DateTimeOffset? date, DateTimeOffset now)
    {
        if (date is not { } d) return "—";
        int days = DaysBetween(d, now);
        if (days <= 0) return "Today";
        if (days == 1) return "Yesterday";
        if (days < 7) return $"{days} days ago";
        if (days < 14) return "Last week";
        if (days < 60) return $"{days / 7} weeks ago";
        if (days < 365) return $"{days / 30} months ago";
        int years = days / 365;
        return years == 1 ? "1 year ago" : $"{years} years ago";
    }

    public static string SourceTitle(ApplicationSource source) => source switch
    {
        ApplicationSource.Store => "Microsoft Store",
        _ => "Other",
    };

    public static string KindTitle(ApplicationRelatedKind kind) => kind switch
    {
        ApplicationRelatedKind.Caches => "Caches",
        ApplicationRelatedKind.Logs => "Logs",
        ApplicationRelatedKind.RoamingData => "AppData (Roaming)",
        ApplicationRelatedKind.LocalData => "AppData (Local)",
        ApplicationRelatedKind.ProgramData => "ProgramData",
        ApplicationRelatedKind.Documents => "Documents",
        ApplicationRelatedKind.Programs => "Programs",
        _ => "Other",
    };

    /// <summary>Ticked for you: what the app rebuilds. Unticked: places that can hold your own files.</summary>
    public static bool TickedByDefault(ApplicationRelatedKind kind) =>
        kind is ApplicationRelatedKind.Caches or ApplicationRelatedKind.Logs;

    /// <summary>The places the "Your files may be in here" note is about.</summary>
    public static bool MayHoldYourFiles(ApplicationRelatedKind kind) =>
        kind is ApplicationRelatedKind.RoamingData or ApplicationRelatedKind.LocalData or ApplicationRelatedKind.Documents;

    /// <summary>What uninstalling frees: the program plus the ticked leftovers.</summary>
    public static long UninstallBytes(ApplicationEntry app, Func<ApplicationRelatedItem, bool> ticked) =>
        app.AppBytes + app.Related.Where(ticked).Sum(r => r.Bytes);

    public static List<ApplicationEntry> Sorted(IEnumerable<ApplicationEntry> apps, ApplicationSort sort) => sort switch
    {
        ApplicationSort.SizeDesc => apps.OrderByDescending(a => a.TotalBytes).ToList(),
        ApplicationSort.SizeAsc => apps.OrderBy(a => a.TotalBytes).ToList(),
        ApplicationSort.NameAsc => apps.OrderBy(a => a.Name, NaturalComparer.Instance).ToList(),
        ApplicationSort.LastUsedDesc => apps.OrderByDescending(a => a.LastUsed ?? DateTimeOffset.MinValue).ToList(),
        // Last opened puts the longest-unused first; unknown dates go last.
        ApplicationSort.LastUsedAsc => apps.OrderBy(a => a.LastUsed ?? DateTimeOffset.MaxValue).ToList(),
        _ => apps.OrderBy(a => SourceTitle(a.Source), StringComparer.Ordinal)
                 .ThenBy(a => a.Name, NaturalComparer.Instance).ToList(),
    };

    /// <summary>The table's rows: the "Not opened in 6 months" filter, the search, then the sort.</summary>
    public static List<ApplicationEntry> Visible(IEnumerable<ApplicationEntry> apps, bool onlyStale, string query,
        ApplicationSort sort, DateTimeOffset now)
    {
        var list = onlyStale ? apps.Where(a => a.IsNotRecentlyUsed(now)) : apps;
        string q = query.Trim().ToLowerInvariant();
        if (q.Length > 0)
            list = list.Where(a => a.Name.ToLowerInvariant().Contains(q)
                                   || (a.Publisher?.ToLowerInvariant().Contains(q) ?? false));
        return Sorted(list, sort);
    }

    /// <summary>
    /// Finder-style name order (localizedStandardCompare): case-insensitive, digits by value,
    /// so "App 9" comes before "App 10".
    /// </summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int i = 0, j = 0;
            var culture = CultureInfo.CurrentCulture.CompareInfo;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    string a = x[si..i].TrimStart('0'), b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    int digits = string.CompareOrdinal(a, b);
                    if (digits != 0) return digits;
                }
                else
                {
                    int si = i, sj = j;
                    while (i < x.Length && !char.IsDigit(x[i])) i++;
                    while (j < y.Length && !char.IsDigit(y[j])) j++;
                    int text = culture.Compare(x[si..i], y[sj..j], CompareOptions.IgnoreCase);
                    if (text != 0) return text;
                }
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }

    // ---- Windows data ----

    /// <summary>The folders leftovers are classified by.</summary>
    public sealed record Places(string RoamingAppData, string LocalAppData, string ProgramData, string Documents, string Programs)
    {
        public static Places Current() => new(
            UserFolders.RoamingAppData,
            UserFolders.LocalAppData,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(UserFolders.Home, "Documents"),
            Path.Combine(UserFolders.LocalAppData, "Programs"));
    }

    private static readonly HashSet<string> CacheWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", "caches", "gpucache", "shadercache", "crashdumps", "crashpad", "crashreports",
    };

    private static readonly HashSet<string> LogWords = new(StringComparer.OrdinalIgnoreCase) { "log", "logs" };

    /// <summary>What a leftover is: your Documents and per-user programs first, then caches and logs by name, then by place.</summary>
    public static ApplicationRelatedKind RelatedKind(string path, Places places)
    {
        if (IsSameOrInside(path, places.Documents)) return ApplicationRelatedKind.Documents;
        if (IsSameOrInside(path, places.Programs)) return ApplicationRelatedKind.Programs;
        var words = AppLeftoverFinder.Words(LastSegment(path));
        if (words.Any(CacheWords.Contains)) return ApplicationRelatedKind.Caches;
        if (words.Any(LogWords.Contains)) return ApplicationRelatedKind.Logs;
        if (IsSameOrInside(path, places.LocalAppData)) return ApplicationRelatedKind.LocalData;
        if (IsSameOrInside(path, places.RoamingAppData)) return ApplicationRelatedKind.RoamingData;
        if (IsSameOrInside(path, places.ProgramData)) return ApplicationRelatedKind.ProgramData;
        return ApplicationRelatedKind.Other;
    }

    /// <summary>
    /// The leftovers as the inspector lists them, biggest first. A match that is the program's
    /// own install folder, holds it, or sits inside it is the program, not a leftover: it is
    /// counted in the app's size and never offered for the basket (Squirrel apps install to
    /// %LOCALAPPDATA%\Name, per-user installs to %LOCALAPPDATA%\Programs\Name).
    /// </summary>
    public static List<ApplicationRelatedItem> RelatedItems(IEnumerable<(string Path, long Bytes)> leftovers,
        string? installPath, Places places) =>
        leftovers
            .Where(l => string.IsNullOrWhiteSpace(installPath)
                        || !(IsSameOrInside(l.Path, installPath) || IsSameOrInside(installPath, l.Path)))
            .Select(l => new ApplicationRelatedItem(RelatedKind(l.Path, places), l.Path, l.Bytes))
            .OrderByDescending(r => r.Bytes)
            .ToList();

    /// <summary>
    /// Folders an install path must never be or contain: a broken registry entry that names
    /// "C:\Program Files" would otherwise measure (and match launches in) every program.
    /// </summary>
    public static List<string> BroadFolders()
    {
        var list = new List<string>();
        void add(string? p) { if (!string.IsNullOrWhiteSpace(p)) list.Add(p); }
        add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        add(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
        add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        add(UserFolders.RoamingAppData);
        add(UserFolders.LocalAppData);
        add(Path.Combine(UserFolders.LocalAppData, "Programs"));
        add(UserFolders.Home);
        return list;
    }

    /// <summary>A real, specific install folder: not empty, not a drive root, not one of (or above) <paramref name="broad"/>.</summary>
    public static bool IsUsableInstallPath(string? path, IEnumerable<string> broad)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string trimmed = path.Trim().Trim('"').TrimEnd('\\', '/');
        if (trimmed.Length <= 2) return false;                  // "C:", "", "\"
        if (trimmed.IndexOfAny(['\\', '/']) < 0) return false;  // not a folder path at all
        return !broad.Any(b => IsSameOrInside(b, trimmed));
    }

    /// <summary>The program's own size: its install folder when there is a usable one, else the registry estimate.</summary>
    public static long AppBytes(InstalledApp app, IEnumerable<string> broad) =>
        IsUsableInstallPath(app.InstallLocation, broad) && Directory.Exists(app.InstallLocation)
            ? AppLeftoverFinder.AllocatedSize(app.InstallLocation!)
            : app.EstimatedBytes ?? 0;

    /// <summary>The registry's "yyyyMMdd" install date, as a local date.</summary>
    public static DateTimeOffset? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !DateTime.TryParseExact(raw.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day.Year < 1990)
            return null;
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }

    /// <summary>When the program was installed: the registry's date, else its install folder's creation date.</summary>
    public static DateTimeOffset? InstalledDate(InstalledApp app, IEnumerable<string> broad)
    {
        if (ParseInstallDate(app.InstallDate) is { } date) return date;
        try
        {
            if (IsUsableInstallPath(app.InstallLocation, broad) && Directory.Exists(app.InstallLocation))
                return new DateTimeOffset(Directory.GetCreationTime(app.InstallLocation!));
        }
        catch { /* unreadable: unknown */ }
        return null;
    }

    /// <summary>A stable id: the registry key (or the package's full name).</summary>
    public static string EntryId(InstalledApp app) =>
        (app.IsStorePackage ? "pkg:" : "reg:") + (app.RegistryKeyName ?? app.Name);

    /// <summary>The row shown before anything is measured (sizes "…").</summary>
    public static ApplicationEntry Stub(InstalledApp app, DateTimeOffset? lastUsed, IEnumerable<string> broad) => new()
    {
        Id = EntryId(app),
        Name = app.Name,
        Publisher = string.IsNullOrWhiteSpace(app.Publisher) ? null : app.Publisher,
        Version = string.IsNullOrWhiteSpace(app.Version) ? null : app.Version,
        InstallPath = IsUsableInstallPath(app.InstallLocation, broad) ? app.InstallLocation!.Trim().Trim('"') : "",
        Source = app.IsStorePackage ? ApplicationSource.Store : ApplicationSource.Other,
        LastUsed = lastUsed,
        Installed = InstalledDate(app, broad),
        UninstallCommand = string.IsNullOrWhiteSpace(app.UninstallString) ? null : app.UninstallString,
        SizePending = true,
    };

    /// <summary>
    /// Measures one program: its own size and its leftovers (Store packages keep their data
    /// with the package, so they list none). Slow — walks folders; run off the UI thread.
    /// </summary>
    public static ApplicationEntry Measure(ApplicationEntry stub, InstalledApp app,
        IReadOnlyCollection<InstalledApp> installed, Places places, IReadOnlyList<string> broad)
    {
        long appBytes = AppBytes(app, broad);
        var related = new List<ApplicationRelatedItem>();
        if (!app.IsStorePackage)
        {
            var found = AppLeftoverFinder.FindLeftovers(app, measureSizes: false, installed);
            related = RelatedItems(found.LeftoverPaths.Select(p => (p, AppLeftoverFinder.AllocatedSize(p))),
                stub.InstallPath, places);
        }
        return stub with { AppBytes = appBytes, Related = related, SizePending = false };
    }

    // ---- Paths ----

    /// <summary>The last path segment, '\' and '/' alike.</summary>
    public static string LastSegment(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it: whole segments, case-insensitive, '\' and '/' alike.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        string p = path.Trim().Trim('"').TrimEnd('\\', '/'), f = folder.Trim().Trim('"').TrimEnd('\\', '/');
        if (f.Length == 0 || p.Length < f.Length) return false;
        for (int k = 0; k < f.Length; k++)
        {
            char a = p[k], b = f[k];
            if (a is '\\' or '/' && b is '\\' or '/') continue;
            if (char.ToUpperInvariant(a) != char.ToUpperInvariant(b)) return false;
        }
        return p.Length == f.Length || p[f.Length] is '\\' or '/';
    }
}
