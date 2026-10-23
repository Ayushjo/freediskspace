namespace DiskMap.Core;

/// <summary>Path display helpers shared by the catalogs (Mac: CanonicalPath.swift).</summary>
public static class CanonicalPath
{
    /// <summary>
    /// The user profile shown as "~": "C:\Users\me\Downloads\a.mkv" → "~\Downloads\a.mkv".
    /// Mac: displayPath(absolutePath:home:). The Mac also folds the /System/Volumes/Data
    /// firmlinks back to /Users, /Applications and /Library; Windows has no such aliases.
    /// Case-insensitive, '\' and '/' interchangeable; the rest of the path keeps its own separators.
    /// <paramref name="home"/> defaults to <see cref="UserFolders.Home"/>.
    /// </summary>
    public static string DisplayPath(string absolutePath, string? home = null)
    {
        home ??= UserFolders.Home;
        home = home.TrimEnd('\\', '/');
        if (home.Length == 0 || !StartsWithFolder(absolutePath, home)) return absolutePath;
        string rest = absolutePath[home.Length..];
        return rest.TrimEnd('\\', '/').Length == 0 ? "~" : "~" + rest;
    }

    /// <summary>
    /// The folder a file sits in, for list rows: "~\Downloads\misc\" (Mac: parentDisplay(of:)),
    /// shortened by <see cref="TruncatedDirectory"/>. Ends with the path's own separator.
    /// </summary>
    public static string ParentDisplay(string absolutePath, string? home = null)
    {
        int cut = absolutePath.TrimEnd('\\', '/').LastIndexOfAny(['\\', '/']);
        string parent = cut <= 0 ? absolutePath : absolutePath[..cut];
        char separator = cut > 0 ? absolutePath[cut] : '\\';
        string display = DisplayPath(parent, home);
        if (!IsSeparator(display[^1])) display += separator;
        return TruncatedDirectory(display);
    }

    /// <summary>
    /// Truncate for list rows (Mac: truncatedDirectory): drop a trailing file name, keep the
    /// first 18 and the last characters around an ellipsis when longer than <paramref name="maxChars"/>.
    /// </summary>
    public static string TruncatedDirectory(string displayPath, int maxChars = 48)
    {
        string p = displayPath;
        int slash = p.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            char separator = p[slash];
            p = p[..slash];
            if (p.Length == 0 || !IsSeparator(p[^1])) p += separator;
        }
        if (p.Length <= maxChars) return p;
        const int head = 18;
        int tail = Math.Max(12, maxChars - head - 1);
        return p[..head] + "…" + p[^tail..];
    }

    /// <summary>
    /// <paramref name="path"/> is <paramref name="folder"/> or inside it: case-insensitive,
    /// '\' and '/' interchangeable, matching whole segments only.
    /// </summary>
    private static bool StartsWithFolder(string path, string folder)
    {
        if (path.Length < folder.Length) return false;
        for (int k = 0; k < folder.Length; k++)
        {
            char a = path[k], b = folder[k];
            if (IsSeparator(a) && IsSeparator(b)) continue;
            if (char.ToUpperInvariant(a) != char.ToUpperInvariant(b)) return false;
        }
        return path.Length == folder.Length || IsSeparator(path[folder.Length]);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
