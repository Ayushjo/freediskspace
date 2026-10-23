namespace DiskMap.Core;

/// <summary>
/// What a large file is, for the Old Downloads and large-file views (Mac: FileKind.swift).
/// A coarser, decision-oriented taxonomy than <see cref="FileTypes"/>: "is this an installer I
/// can drop" rather than "what colour is its tile".
/// </summary>
public enum FileKind
{
    Video,
    DiskImage,
    Archive,
    Application,
    Document,
    Database,
    VirtualDisk,
    DeviceBackup,
    Other,
}

public static class FileKindExtensions
{
    public static string Title(this FileKind kind) => kind switch
    {
        FileKind.Video => "Video",
        FileKind.DiskImage => "Disk Image",
        FileKind.Archive => "Archive",
        FileKind.Application => "Application",
        FileKind.Document => "Document",
        FileKind.Database => "Database",
        FileKind.VirtualDisk => "Virtual Disk",
        FileKind.DeviceBackup => "Device Backup",
        _ => "Other",
    };

    /// <summary>The Mac's SF Symbol name; the app maps it to its own icon.</summary>
    public static string SymbolName(this FileKind kind) => kind switch
    {
        FileKind.Video => "film",
        FileKind.DiskImage => "opticaldisc",
        FileKind.Archive => "doc.zipper",
        FileKind.Application => "app",
        FileKind.Document => "doc.text",
        FileKind.Database => "cylinder.split.1x2",
        FileKind.VirtualDisk => "externaldrive",
        FileKind.DeviceBackup => "iphone",
        _ => "doc",
    };

    /// <summary>The Mac's rawValue ("video", "diskImage" …) — stable ids for buckets and settings.</summary>
    public static string RawValue(this FileKind kind) => kind switch
    {
        FileKind.Video => "video",
        FileKind.DiskImage => "diskImage",
        FileKind.Archive => "archive",
        FileKind.Application => "application",
        FileKind.Document => "document",
        FileKind.Database => "database",
        FileKind.VirtualDisk => "virtualDisk",
        FileKind.DeviceBackup => "deviceBackup",
        _ => "other",
    };
}

public static class FileKinds
{
    /// <summary>
    /// The Mac's rules in the Mac's order, with Windows locations. The path is compared with
    /// '/' separators (either separator is accepted), so a rule is written once.
    /// </summary>
    public static FileKind Classify(string fileName, string path = "")
    {
        string n = fileName.ToLowerInvariant();
        string p = path.Replace('\\', '/').ToLowerInvariant();
        // iPhone / iPad backups: Apple Devices / iTunes for Windows keep them in
        // %APPDATA%\Apple Computer\MobileSync\Backup or %USERPROFILE%\Apple\MobileSync\Backup
        // — the same MobileSync\Backup tail as the Mac.
        if (p.Contains("/mobilesync/backup/"))
            return FileKind.DeviceBackup;
        if (n.EndsWith(".mkv") || n.EndsWith(".mp4") || n.EndsWith(".mov") || n.EndsWith(".m4v")
            || n.EndsWith(".avi") || n.EndsWith(".webm"))
        {
            return FileKind.Video;
        }
        // Windows: installers are this kind too — the Mac's .dmg/.pkg role is played by
        // .msi/.msix/.appx packages and setup .exe files. An .exe inside an installed app's
        // folder is that app, not an installer (handled by the Application rule below).
        if (n.EndsWith(".dmg") || n.EndsWith(".iso") || n.EndsWith(".pkg") || n.EndsWith(".img")
            || n.EndsWith(".msi") || n.EndsWith(".msix") || n.EndsWith(".msixbundle")
            || n.EndsWith(".appx") || n.EndsWith(".appxbundle")
            || (n.EndsWith(".exe") && !InInstalledAppFolder(p)))
        {
            return FileKind.DiskImage;
        }
        // Windows adds Hyper-V / WSL disks (.vhd, .vhdx, .avhdx).
        if (n.EndsWith(".raw") || n.EndsWith(".vmdk") || n.EndsWith(".vdi") || n.EndsWith(".qcow2")
            || n.EndsWith(".sparseimage") || n.EndsWith(".sparsebundle")
            || n.EndsWith(".vhd") || n.EndsWith(".vhdx") || n.EndsWith(".avhdx"))
        {
            return FileKind.VirtualDisk;
        }
        if (n.EndsWith(".zip") || n.EndsWith(".tar") || n.EndsWith(".gz") || n.EndsWith(".tgz")
            || n.EndsWith(".7z") || n.EndsWith(".rar") || n.EndsWith(".bz2"))
        {
            return FileKind.Archive;
        }
        // Mac: ".app" or under /Applications/. Windows: an .exe in, or anything under,
        // Program Files, per-user Programs or WindowsApps.
        if (n.EndsWith(".app") || n.EndsWith(".exe") || InInstalledAppFolder(p))
            return FileKind.Application;
        if (n.EndsWith(".sqlite") || n.EndsWith(".db") || n.EndsWith(".sql"))
            return FileKind.Database;
        if (p.Contains("mobilebackup") || (p.Contains("backup") && (n.EndsWith(".backup") || p.Contains("iphone"))))
            return FileKind.DeviceBackup;
        if (n.EndsWith(".pdf") || n.EndsWith(".doc") || n.EndsWith(".docx") || n.EndsWith(".pages")
            || n.EndsWith(".txt") || n.EndsWith(".rtf") || n.EndsWith(".md"))
        {
            return FileKind.Document;
        }
        return FileKind.Other;
    }

    /// <summary>
    /// The Windows counterpart of the Mac's "/applications/" path test, on a '/'-separated
    /// lower-cased path. A pure string test (not <see cref="CleanupQueue.IsInsideInstalledApp"/>,
    /// which reads the real Program Files location) so a demo tree classifies the same anywhere.
    /// </summary>
    private static bool InInstalledAppFolder(string p) =>
        p.Contains("/program files/") || p.Contains("/program files (x86)/")
        || p.Contains("/appdata/local/programs/") || p.Contains("/windowsapps/");
}
