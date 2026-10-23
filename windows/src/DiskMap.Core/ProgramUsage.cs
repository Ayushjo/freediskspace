using Microsoft.Win32;

namespace DiskMap.Core;

/// <summary>One launch Windows recorded: what was started (a path, a shortcut or an app ID) and when, last.</summary>
public sealed record ProgramLaunch(string Name, DateTimeOffset LastRun);

/// <summary>
/// "Last opened" for installed programs (the Mac reads Spotlight's kMDItemLastUsedDate).
/// Windows records launches made from Explorer, Start and the taskbar in the per-user
/// UserAssist key: value names are ROT13-encoded paths (with a known-folder GUID for the
/// first part), the data holds the last run as a FILETIME. Folder or .exe access dates are
/// never used — like the Mac, an unknown date stays unknown rather than guessing an install
/// date as a launch. Read-only; local; nothing is written.
/// </summary>
public static class ProgramUsage
{
    private const string UserAssistPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    /// <summary>UserAssist's name encoding: letters rotated by 13, everything else kept.</summary>
    public static string Rot13(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is >= 'a' and <= 'z') chars[i] = (char)('a' + (c - 'a' + 13) % 26);
            else if (c is >= 'A' and <= 'Z') chars[i] = (char)('A' + (c - 'A' + 13) % 26);
        }
        return new string(chars);
    }

    /// <summary>
    /// The last run in a UserAssist value: the FILETIME at offset 60 of the 72-byte record
    /// (Windows 7 and later), or at offset 8 of the 16-byte XP record. Zero or implausible
    /// times are unknown.
    /// </summary>
    public static DateTimeOffset? LastRun(byte[]? data)
    {
        int offset = data?.Length switch { >= 68 => 60, 16 => 8, _ => -1 };
        if (data is null || offset < 0) return null;
        long fileTime = BitConverter.ToInt64(data, offset);
        if (fileTime <= 0) return null;
        try
        {
            var when = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
            return when.Year < 2000 || when > DateTimeOffset.UtcNow.AddDays(2) ? null : when;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>The known folders UserAssist names by GUID, as paths on this PC.</summary>
    public static Dictionary<string, string> KnownFolders()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void add(string guid, string? path) { if (!string.IsNullOrWhiteSpace(path)) map[guid] = path; }
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        add("{6D809377-6AF0-444B-8957-A3773F02200E}", programFiles);                                                 // ProgramFilesX64
        add("{905E63B6-C1BF-494E-B29C-65B732D3D21A}", programFiles);                                                 // ProgramFiles
        add("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        add("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.GetFolderPath(Environment.SpecialFolder.System));
        add("{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        add("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        add("{0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8}", Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        add("{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}", Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        add("{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}", UserFolders.LocalAppData);
        add("{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}", UserFolders.RoamingAppData);
        add("{5E6C858F-0E22-4760-9AFE-EA3317B67173}", UserFolders.Home);
        add("{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        add("{9E3995AB-1F9C-4F13-B827-48B24B6C7174}",
            Path.Combine(UserFolders.RoamingAppData, @"Microsoft\Internet Explorer\Quick Launch\User Pinned"));
        return map;
    }

    /// <summary>"{6D809377-…}\Vendor\app.exe" → "C:\Program Files\Vendor\app.exe"; anything else as it is.</summary>
    public static string Resolve(string name, IReadOnlyDictionary<string, string> knownFolders)
    {
        if (!name.StartsWith('{')) return name;
        int close = name.IndexOf('}');
        if (close < 0 || !knownFolders.TryGetValue(name[..(close + 1)], out var folder)) return name;
        string rest = name[(close + 1)..].TrimStart('\\', '/');
        return rest.Length == 0 ? folder : folder.TrimEnd('\\', '/') + "\\" + rest;
    }

    /// <summary>Every launch in UserAssist, names decoded and resolved; empty when there is none to read.</summary>
    public static List<ProgramLaunch> ReadUserAssist()
    {
        var launches = new List<ProgramLaunch>();
        try
        {
            if (!OperatingSystem.IsWindows()) return launches;
            var folders = KnownFolders();
            using var root = Registry.CurrentUser.OpenSubKey(UserAssistPath);
            if (root is null) return launches;
            foreach (var guid in root.GetSubKeyNames())
            {
                using var count = root.OpenSubKey(guid + @"\Count");
                if (count is null) continue;
                foreach (var valueName in count.GetValueNames())
                {
                    if (LastRun(count.GetValue(valueName) as byte[]) is { } when)
                        launches.Add(new ProgramLaunch(Resolve(Rot13(valueName), folders), when));
                }
            }
        }
        catch { /* tracking off, or no registry: every date stays unknown */ }
        return launches;
    }

    /// <summary>"Name_1.2.3.0_x64__8wekyb3d8bbwe" → "Name_8wekyb3d8bbwe" (what a Store app's launch ID starts with).</summary>
    public static string? PackageFamily(string? packageFullName)
    {
        if (string.IsNullOrWhiteSpace(packageFullName)) return null;
        var parts = packageFullName.Split('_');
        return parts.Length >= 5 && parts[0].Length > 0 && parts[^1].Length > 0 ? parts[0] + "_" + parts[^1] : null;
    }

    /// <summary>The .exe a registry DisplayIcon names ("\"C:\…\app.exe\",0" → C:\…\app.exe); null for icons and DLLs.</summary>
    public static string? ExecutablePath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        string s = displayIcon.Trim();
        int comma = s.LastIndexOf(',');
        if (comma > 0 && s[(comma + 1)..].Trim().TrimStart('-').All(char.IsDigit)) s = s[..comma];
        s = s.Trim().Trim('"').Trim();
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    /// <summary>Uninstallers and setup runs aren't opening the program.</summary>
    private static bool IsUninstaller(string path)
    {
        string file = ApplicationsCatalog.LastSegment(path).ToLowerInvariant();
        return file.StartsWith("unins") || file.Contains("uninstall") || file.StartsWith("setup");
    }

    /// <summary>
    /// The latest recorded launch of <paramref name="app"/>, or null when none matches. A launch
    /// counts when it is an .exe inside the program's own (specific) install folder, the .exe its
    /// DisplayIcon names, a Start/taskbar shortcut spelled exactly like the program's name, or
    /// (Store apps) its package family's app ID. Biased toward fewer false matches: a wrong date
    /// would mark a program you use as "6 mo+".
    /// </summary>
    public static DateTimeOffset? LastUsed(InstalledApp app, IEnumerable<ProgramLaunch> launches, IEnumerable<string> broad)
    {
        string? folder = ApplicationsCatalog.IsUsableInstallPath(app.InstallLocation, broad)
            ? app.InstallLocation!.Trim().Trim('"') : null;
        string? exe = ExecutablePath(app.DisplayIcon);
        string? family = app.IsStorePackage ? PackageFamily(app.RegistryKeyName) : null;
        var nameWords = AppLeftoverFinder.Words(app.Name);
        bool nameUsable = string.Join(" ", nameWords).Length > 3;

        DateTimeOffset? latest = null;
        foreach (var launch in launches)
        {
            string name = launch.Name;
            bool match;
            if (family is not null)
            {
                match = name.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase);
            }
            else if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var words = AppLeftoverFinder.Words(ApplicationsCatalog.LastSegment(name)[..^4]);
                match = nameUsable && words.SequenceEqual(nameWords);
            }
            else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsUninstaller(name))
            {
                match = (folder is not null && ApplicationsCatalog.IsSameOrInside(name, folder))
                        || (exe is not null && ApplicationsCatalog.IsSameOrInside(name, exe) && !IsUninstaller(exe));
            }
            else match = false;
            if (match && (latest is null || launch.LastRun > latest)) latest = launch.LastRun;
        }
        return latest;
    }
}
