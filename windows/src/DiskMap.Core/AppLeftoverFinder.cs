using DiskMap.Core.Native;
using Microsoft.Win32;

namespace DiskMap.Core;

public sealed record AppLeftovers(
    string? AppId,
    string AppName,
    string InstallPath,
    long InstallSize,
    List<string> LeftoverPaths,
    long LeftoverSize);

public sealed record InstalledApp(
    string Name,
    string? Publisher,
    string? InstallLocation,
    string? RegistryKeyName,
    /// <summary>The registry's own uninstall command line — revealable, never auto-run.</summary>
    string? UninstallString = null,
    /// <summary>True for MSIX/Store packages (from the AppModel hive, not Uninstall).</summary>
    bool IsStorePackage = false);

/// <summary>
/// Finds files an app scattered outside its install directory when it was
/// installed/run — caches, preferences, saved state, logs.
///
/// Windows has no API that enumerates "everything this app touched"
/// either. The heuristic mirrors the macOS build: enumerate installed apps
/// from the registry Uninstall hives (HKLM x64 + 32-bit view + HKCU), then
/// search the standard user/system data locations for items whose name
/// contains the app's display name or publisher token. Because it's
/// heuristic, leftovers always land in a *staged* cleanup list for review —
/// never delete automatically.
/// </summary>
public static class AppLeftoverFinder
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static string[] SearchLocations =>
    [
        UserFolders.RoamingAppData,                                                // %APPDATA%
        UserFolders.LocalAppData,                                                  // %LOCALAPPDATA%
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),// %PROGRAMDATA%
        Path.Combine(UserFolders.Home, "Documents"),
        Path.Combine(UserFolders.LocalAppData, "Programs"),
    ];

    /// <summary>
    /// Installed apps from the three Uninstall hives: HKLM 64-bit,
    /// HKLM 32-bit (Wow6432Node), HKCU. Same role as scanning
    /// /Applications on macOS.
    /// </summary>
    public static List<InstalledApp> InstalledApplications()
    {
        var apps = new List<InstalledApp>();
        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry64, apps);
        ReadUninstallHive(RegistryHive.LocalMachine, RegistryView.Registry32, apps);
        ReadUninstallHive(RegistryHive.CurrentUser, RegistryView.Registry64, apps);
        return apps
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadUninstallHive(RegistryHive root, RegistryView view, List<InstalledApp> apps)
    {
        using var baseKey = RegistryKey.OpenBaseKey(root, view);
        using var uninstall = baseKey.OpenSubKey(UninstallPath);
        if (uninstall is null) return;
        foreach (var subName in uninstall.GetSubKeyNames())
        {
            using var sub = uninstall.OpenSubKey(subName);
            if (sub is null) continue;
            var name = sub.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(name)) continue;
            // System components and updates aren't user-uninstallable apps.
            if (sub.GetValue("SystemComponent") is int sc && sc == 1) continue;
            apps.Add(new InstalledApp(
                name.Trim(),
                (sub.GetValue("Publisher") as string)?.Trim(),
                (sub.GetValue("InstallLocation") as string)?.Trim(),
                subName,
                // Quiet or classic — prefer the quiet form when present.
                ((sub.GetValue("QuietUninstallString") as string)
                    ?? (sub.GetValue("UninstallString") as string))?.Trim()));
        }
    }

    /// <summary>
    /// MSIX / Microsoft Store packages from the per-user AppModel
    /// repository — the no-WinRT enumeration (PackageManager needs a
    /// versioned Windows SDK TFM this port deliberately avoids). Each
    /// subkey name is the package full name; PackageRootFolder points at
    /// the immutable install under Program Files\WindowsApps.
    /// </summary>
    public static List<InstalledApp> StoreApplications()
    {
        var apps = new List<InstalledApp>();
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages is null) return apps;
            foreach (var fullName in packages.GetSubKeyNames())
            {
                using var sub = packages.OpenSubKey(fullName);
                if (sub is null) continue;
                // Full name is Name_Version_Arch_ResourceId_PublisherHash.
                string display = fullName.Split('_')[0];
                if (display.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                    || display.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase))
                    continue;   // OS plumbing — not user-facing apps
                string? installRoot = (sub.GetValue("PackageRootFolder") as string)?.Trim();
                apps.Add(new InstalledApp(
                    display,
                    Publisher: null,
                    InstallLocation: installRoot,
                    RegistryKeyName: fullName,
                    UninstallString: null,
                    IsStorePackage: true));
            }
        }
        catch { /* the AppModel hive is per-user; absence is normal */ }
        return apps
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AppLeftovers FindLeftovers(InstalledApp app) =>
        FindLeftovers(app, measureSizes: true);

    /// <param name="measureSizes">
    /// When false, returns just the matched leftover paths — no recursive
    /// walks. Use for a fast listing pass; measure per-app later.
    /// </param>
    /// <param name="installed">
    /// Every installed app, so one app's leftovers never include a folder a
    /// sibling owns (SteamVR's, or the shared "Google" folder Drive uses).
    /// </param>
    public static AppLeftovers FindLeftovers(InstalledApp app, bool measureSizes,
                                             IReadOnlyCollection<InstalledApp>? installed = null)
    {
        var matches = new List<string>();
        var others = installed ?? Array.Empty<InstalledApp>();
        string documents = Path.Combine(UserFolders.Home, "Documents");

        foreach (var location in SearchLocations)
        {
            if (!Directory.Exists(location)) continue;
            IEnumerable<string> contents;
            try { contents = Directory.EnumerateFileSystemEntries(location); }
            catch { continue; }
            // The user's own Documents only ever match the exact app name.
            bool exactOnly = string.Equals(location, documents, StringComparison.OrdinalIgnoreCase);

            foreach (var item in contents)
            {
                if (item.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) continue;
                if (MatchesApp(Path.GetFileName(item), app, others, exactOnly))
                    matches.Add(item);
            }
        }

        long installSize = 0, leftoverSize = 0;
        if (measureSizes)
        {
            installSize = !string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation)
                ? AllocatedSize(app.InstallLocation)
                : 0;
            leftoverSize = matches.Sum(AllocatedSize);
        }

        return new AppLeftovers(
            AppId: app.RegistryKeyName,
            AppName: app.Name,
            InstallPath: app.InstallLocation ?? "",
            InstallSize: installSize,
            LeftoverPaths: matches,
            LeftoverSize: leftoverSize);
    }

    /// <summary>Folder names under AppData/ProgramData that hold many apps' data.</summary>
    private static readonly HashSet<string> SharedFolderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "packages", "programs", "temp", "common", "apple", "google", "adobe",
        "intel", "nvidia", "amd", "mozilla", "oracle", "jetbrains", "package", "cache", "crashdumps",
    };

    private static readonly HashSet<string> FileExtensionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "log", "json", "ini", "xml", "cfg", "dat", "txt", "db", "sqlite",
    };

    private static readonly HashSet<string> CompanySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "llc", "ltd", "limited", "corporation", "corp", "co", "gmbh", "software", "technologies", "ag", "sa", "bv",
        "ab", "as", "oy", "pty", "plc", "srl", "spa", "kk", "llp", "sarl", "sas", "nv", "pte", "pvt",
    };

    /// <summary>Lower-case words of a name: letters and digits only.</summary>
    public static string[] Words(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? []
            : System.Text.RegularExpressions.Regex.Split(name.ToLowerInvariant(), "[^\\p{L}\\p{Nd}]+")
                .Where(w => w.Length > 0).ToArray();

    private static bool StartsWith(string[] longer, string[] prefix) =>
        prefix.Length > 0 && prefix.Length <= longer.Length && prefix.Select((w, i) => longer[i] == w).All(x => x);

    /// <summary>
    /// Whether a data folder or file belongs to <paramref name="app"/>. Whole
    /// words only: until 2026-10-21 a substring match let "Steam" claim
    /// "SteamVR" and any 4+ letter publisher claim every folder from that
    /// vendor. A missed leftover costs a few KB; a wrong one costs another
    /// app's data (AGENTS.md: fewer false positives).
    /// </summary>
    public static bool MatchesApp(string itemName, InstalledApp app, IReadOnlyCollection<InstalledApp> installed,
                                  bool exactOnly = false)
    {
        var item = Words(itemName);
        // "Spotify.log", "Discord.json": a data file named after the app.
        if (item.Length > 1 && FileExtensionWords.Contains(item[^1])) item = item[..^1];
        var name = Words(app.Name);
        if (item.Length == 0 || name.Length == 0) return false;
        if (string.Join(" ", name).Length <= 3) return false;   // "Git", "VLC": too short to trust

        var siblings = installed.Where(o => !string.Equals(o.Name, app.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exactOnly) return item.SequenceEqual(name);

        // A sibling whose name the folder spells out at least as fully owns it.
        bool siblingOwns(string[] words) => siblings.Any(o =>
        {
            var other = Words(o.Name);
            return other.Length > 0 && (StartsWith(words, other) && other.Length > name.Length
                                        || words.SequenceEqual(other));
        });

        if (item.SequenceEqual(name)) return true;
        if (StartsWith(item, name)) return !siblingOwns(item);          // "Zoom Workplace Data" for "Zoom Workplace"
        if (StartsWith(name, item))                                     // "Zoom" for "Zoom Workplace"
        {
            if (SharedFolderWords.Contains(item[0])) return false;
            bool shared = siblings.Any(o => StartsWith(Words(o.Name), item));
            return !shared;
        }

        // Publisher folder ("Spotify AB" → "Spotify"): only when no other
        // installed app comes from that publisher.
        var publisher = Words(app.Publisher).Where(w => !CompanySuffixes.Contains(w)).ToArray();
        if (publisher.Length > 0 && item.SequenceEqual(publisher) && !SharedFolderWords.Contains(publisher[0]))
        {
            bool samePublisher = siblings.Any(o =>
                Words(o.Publisher).Where(w => !CompanySuffixes.Contains(w)).SequenceEqual(publisher));
            return !samePublisher;
        }
        return false;
    }

    public static long AllocatedSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists && !Directory.Exists(path)) return 0;
            if ((info.Attributes & FileAttributes.Directory) == 0)
                return OnDiskSize(path, info.Length, info.Attributes);

            long total = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(entry);
                    if ((fi.Attributes & FileAttributes.Directory) != 0) continue;
                    total += OnDiskSize(entry, fi.Length, fi.Attributes);
                }
                catch { /* skip unreadable */ }
            }
            return total;
        }
        catch { return 0; }
    }

    private static long OnDiskSize(string path, long logical, FileAttributes attributes)
    {
        const FileAttributes special = FileAttributes.Compressed | FileAttributes.SparseFile;
        if ((attributes & special) != 0)
        {
            uint low = Win32.GetCompressedFileSizeW(Win32.ExtendedPath(path), out uint high);
            if (low != 0xFFFFFFFF) return ((long)high << 32) | low;
        }
        if (logical == 0) return 0;
        // Round up to a 4K cluster — the common NTFS size; precise per-file
        // clusters would need per-volume queries that don't pay for a
        // heuristic leftover listing.
        return (logical + 4095) / 4096 * 4096;
    }
}
