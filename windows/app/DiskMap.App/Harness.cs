using System.Globalization;
using System.IO;
using DiskMap.Core;

namespace DiskMap.App;

/// <summary>
/// WIN-080 render harness settings, read from the environment once. They let
/// the Windows app render the same demo data at the same size, theme and scale
/// as the macOS snapshot harness, so the two can be compared pixel for pixel.
///   DISKMAP_THEME          light | dark (overrides the saved Appearance)
///   DISKMAP_SIZE           WxH content size in DIPs, e.g. 1440x900
///   DISKMAP_SHOT_SCALE     render scale for DISKMAP_SHOT, e.g. 2 (default: screen DPI)
///   DISKMAP_SIZE_BASIS     logical | ondisk (default: ondisk)
///   DISKMAP_DETERMINISTIC  1 = fixed 500 GB volume with 120 GB free, like the Mac harness
///   DISKMAP_FIND_DUPLICATES 1 = Duplicates searches as soon as it opens
/// A harness run never writes the user's scan history.
/// </summary>
internal static class Harness
{
    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value.Trim() : null;

    /// <summary>A screenshot run: DISKMAP_SHOT is set.</summary>
    public static bool IsActive => Env("DISKMAP_SHOT") is not null;

    /// <summary>true = dark, false = light, null = use the saved Appearance.</summary>
    public static bool? Dark => Env("DISKMAP_THEME")?.ToLowerInvariant() switch
    {
        "dark" => true,
        "light" => false,
        _ => null,
    };

    public static (double Width, double Height)? ContentSize
    {
        get
        {
            if (Env("DISKMAP_SIZE") is not { } raw) return null;
            var parts = raw.ToLowerInvariant().Split('x');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
                && w >= 400 && h >= 300)
                return (w, h);
            return null;
        }
    }

    public static double? ShotScale =>
        double.TryParse(Env("DISKMAP_SHOT_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
        && s is >= 0.5 and <= 4 ? s : null;

    public static SizeBasis? Basis => Env("DISKMAP_SIZE_BASIS")?.ToLowerInvariant() switch
    {
        "logical" => SizeBasis.Logical,
        "ondisk" or "allocated" => SizeBasis.Allocated,
        _ => null,
    };

    public static bool Deterministic => Env("DISKMAP_DETERMINISTIC") == "1";

    /// <summary>A demo profile is in use (DISKMAP_HOME): nothing from this PC's own setup may show.</summary>
    public static bool DemoProfile => Env(UserFolders.OverrideVariable) is { Length: > 0 };

    /// <summary>
    /// Applications on the demo PC, instead of this PC's registry (its real programs are personal).
    /// Names match the demo profile's AppData folders, so their leftovers are found there.
    /// </summary>
    public static List<InstalledApp> DemoApplications()
    {
        string date(int daysAgo) => DateTime.Today.AddDays(-daysAgo).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        const long MB = 1_000_000;
        return
        [
            new("Google Chrome", "Google LLC", null, "Google Chrome", Version: "141.0.7390.66", InstallDate: date(40), EstimatedBytes: 612 * MB),
            new("Spotify", "Spotify AB", null, "Spotify", Version: "1.2.74", InstallDate: date(120), EstimatedBytes: 402 * MB),
            new("Slack", "Slack Technologies", null, "slack", Version: "4.46.99", InstallDate: date(75), EstimatedBytes: 371 * MB),
            new("Android Studio", "Google LLC", null, "Android Studio", Version: "2025.1.4", InstallDate: date(200), EstimatedBytes: 2_840 * MB),
            new("Microsoft Visual Studio Code", "Microsoft Corporation", null, "{EA457B21}_is1", Version: "1.105.1", InstallDate: date(30), EstimatedBytes: 418 * MB),
            new("Python 3.13.7 (64-bit)", "Python Software Foundation", null, "{python-3.13}", Version: "3.13.7150.0", InstallDate: date(260), EstimatedBytes: 104 * MB),
            new("Node.js", "Node.js Foundation", null, "{nodejs}", Version: "22.20.0", InstallDate: date(90), EstimatedBytes: 98 * MB),
            new("7-Zip 25.01 (x64)", "Igor Pavlov", null, "7-Zip", Version: "25.01", InstallDate: date(330), EstimatedBytes: 6 * MB),
        ];
    }

    /// <summary>"Last opened" for the demo applications (days ago); nil = never seen.</summary>
    public static DateTimeOffset? DemoLastOpened(string name) => name switch
    {
        "Google Chrome" or "Microsoft Visual Studio Code" or "Slack" => DateTimeOffset.Now.AddHours(-3),
        "Spotify" => DateTimeOffset.Now.AddDays(-2),
        "Node.js" => DateTimeOffset.Now.AddDays(-9),
        "Android Studio" => DateTimeOffset.Now.AddDays(-210),
        _ => null,
    };

    /// <summary>
    /// DISKMAP_FIND_DUPLICATES=1: Duplicates starts its search when it opens, so the board
    /// can be rendered (Mac harness: --find-duplicates).
    /// </summary>
    public static bool FindDuplicates => Env("DISKMAP_FIND_DUPLICATES") == "1";

    /// <summary>The Mac harness's fixed volume: 500 GB with 120 GB free.</summary>
    public static VolumeInfo FixedVolume(string? path)
    {
        string label = path is { Length: >= 2 } && path[1] == ':' ? $"Local Disk ({path[..2].ToUpperInvariant()})" : "Local Disk (C:)";
        return new VolumeInfo(label, "NTFS", 500_000_000_000, 120_000_000_000);
    }

    /// <summary>The volume to show: the fixed one in deterministic runs, else the real drive.</summary>
    public static VolumeInfo? VolumeFor(string? path) =>
        Deterministic ? FixedVolume(path) : VolumeStats.Of(path);

    /// <summary>Whether this process runs as administrator (it can read System Volume Information).</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// "Where's the rest?" for the scan. Deterministic runs pin Windows' files and the Recycle
    /// Bin to demo figures, so renders never show this PC's own.
    /// </summary>
    public static SpaceAccount? SpaceAccountFor(VolumeInfo? volume, string path, long scannedOnDiskBytes, int unreadableFolders)
    {
        if (!Deterministic) return SpaceAccount.Read(volume, path, scannedOnDiskBytes, unreadableFolders, IsElevated());
        if (volume is not { UsedBytes: > 0 } v) return null;
        string full = System.IO.Path.GetFullPath(path);
        bool driveRoot = string.Equals(full.TrimEnd('\\'), (System.IO.Path.GetPathRoot(full) ?? full).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        return SpaceAccount.Build(v.UsedBytes, driveRoot, scannedOnDiskBytes, 1_240_000_000,
            [new("pagefile.sys", 12_884_901_888), new("hiberfil.sys", 6_817_349_632), new("swapfile.sys", 268_435_456)],
            unreadableFolders, isElevated: false);
    }
}
