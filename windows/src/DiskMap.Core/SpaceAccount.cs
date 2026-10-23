using System.Runtime.InteropServices;

namespace DiskMap.Core;

/// <summary>
/// "Where's the rest?" (Mac: SpaceAccount.swift): the drive's used space split into parts that
/// add up, each with how sure we are. Only figures Windows gives without administrator rights
/// are measured — the Recycle Bin, the page, hibernation and swap files; what can't be known
/// (restore points without admin, what NTFS keeps for itself) is never claimed, it's the
/// remainder, with the likely reasons named.
/// </summary>
public sealed record SpaceAccount(long UsedBytes, List<SpaceAccount.Row> Rows, SpaceAccount.NotSeenReasons Reasons, bool ScanExceedsCovered)
{
    public enum Confidence
    {
        /// <summary>Reported by Windows for exactly this.</summary>
        Exact,
        /// <summary>What is left after the exact parts.</summary>
        Remainder,
    }

    public enum Part { Scanned, RecycleBin, PageFile, Hibernation, SwapFile, NotSeen }

    public sealed record Row(Part Part, string Title, long Bytes, Confidence Confidence, string Detail);

    /// <summary>Why part of the drive wasn't seen: counts and conditions, not sizes.</summary>
    public sealed record NotSeenReasons(bool OutsideScannedFolder, int UnreadableFolders, bool NotElevated);

    /// <summary>One of Windows' own files at the drive root (pagefile.sys, hiberfil.sys, swapfile.sys).</summary>
    public sealed record SystemFile(string Name, long Bytes);

    public long Total => Rows.Sum(r => r.Bytes);

    /// <summary>
    /// Pure. A scan of the drive root already read Windows' files and the Recycle Bin, so they
    /// are only listed apart when the scan was of a folder.
    /// </summary>
    public static SpaceAccount Build(long usedBytes, bool scanIsDriveRoot, long scannedOnDiskBytes,
        long? recycleBinBytes, IReadOnlyList<SystemFile> systemFiles, int unreadableFolders, bool isElevated)
    {
        long used = Math.Max(0, usedBytes);
        long scanned = Math.Max(0, scannedOnDiskBytes);
        bool exceeds = scanned > used;
        var rows = new List<Row>
        {
            new(Part.Scanned, "Found by this scan", Math.Min(scanned, used), Confidence.Exact, exceeds
                ? $"The scan listed {ShellFormat.Bytes(scanned)}, more than this: hard-linked and block-cloned files share space on disk but are listed once per name."
                : "Every folder and file the scan could read."),
        };
        if (!scanIsDriveRoot)
        {
            foreach (var file in systemFiles.Where(f => f.Bytes > 0))
            {
                switch (file.Name.ToLowerInvariant())
                {
                    case "pagefile.sys":
                        rows.Add(new Row(Part.PageFile, "Page file", file.Bytes, Confidence.Exact,
                            "Memory Windows moved to disk. Windows sizes it itself; it shrinks after a restart when less is needed."));
                        break;
                    case "hiberfil.sys":
                        rows.Add(new Row(Part.Hibernation, "Hibernation file", file.Bytes, Confidence.Exact,
                            "Kept so the PC can hibernate and start fast. Turning hibernation off (powercfg /h off, as administrator) removes it."));
                        break;
                    case "swapfile.sys":
                        rows.Add(new Row(Part.SwapFile, "Swap file", file.Bytes, Confidence.Exact,
                            "Memory for Store apps that Windows moved to disk."));
                        break;
                }
            }
            if (recycleBinBytes is > 0 and var bin)
                rows.Add(new Row(Part.RecycleBin, "Recycle Bin", bin, Confidence.Exact,
                    "Files you deleted, still on this drive until you empty the Recycle Bin."));
        }
        var reasons = new NotSeenReasons(!scanIsDriveRoot, Math.Max(0, unreadableFolders), !isElevated);
        long notSeen = used - rows.Sum(r => r.Bytes);
        if (notSeen > 0)
            rows.Add(new Row(Part.NotSeen, scanIsDriveRoot ? "Not seen by the scan" : "Elsewhere on this drive",
                notSeen, Confidence.Remainder, NotSeenDetail(reasons)));
        return new SpaceAccount(used, rows, reasons, exceeds);
    }

    public static string NotSeenDetail(NotSeenReasons reasons)
    {
        var parts = new List<string>();
        if (reasons.OutsideScannedFolder) parts.Add("files outside the folder you scanned (Windows, programs and other users)");
        if (reasons.UnreadableFolders > 0)
            parts.Add($"{reasons.UnreadableFolders:N0} folder{(reasons.UnreadableFolders == 1 ? "" : "s")} the scan couldn't read");
        if (reasons.NotElevated)
            parts.Add("restore points and shadow copies in System Volume Information, which only an administrator can read");
        parts.Add("NTFS's own records (the master file table and its journal)");
        string list = parts.Count > 1 ? string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1] : parts[0];
        return $"Most likely {list}.";
    }

    // ---- Live ----

    /// <summary>
    /// Reads the parts Windows reports without administrator rights for the drive holding
    /// <paramref name="scanRootPath"/>. Nil without a volume. Off the UI thread.
    /// </summary>
    public static SpaceAccount? Read(VolumeInfo? volume, string scanRootPath, long scannedOnDiskBytes, int unreadableFolders, bool isElevated)
    {
        if (volume is not { UsedBytes: > 0 } v) return null;
        string root = Path.GetPathRoot(Path.GetFullPath(scanRootPath)) ?? scanRootPath;
        bool driveRoot = string.Equals(Path.GetFullPath(scanRootPath).TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        return Build(v.UsedBytes, driveRoot, scannedOnDiskBytes, RecycleBinBytes(root), SystemFiles(root), unreadableFolders, isElevated);
    }

    /// <summary>pagefile.sys, hiberfil.sys and swapfile.sys at the drive root — listed, never opened.</summary>
    public static List<SystemFile> SystemFiles(string driveRoot)
    {
        var found = new List<SystemFile>();
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
            foreach (var file in new DirectoryInfo(driveRoot).EnumerateFiles("*.sys", options))
                if (file.Name.ToLowerInvariant() is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys")
                    found.Add(new SystemFile(file.Name, file.Length));
        }
        catch { }
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>The current user's Recycle Bin on this drive, as Windows reports it; nil when it won't say.</summary>
    public static long? RecycleBinBytes(string driveRoot)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBinW(driveRoot, ref info) == 0 ? Math.Max(0, info.i64Size) : null;
        }
        catch { return null; }
    }
}
