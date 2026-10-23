using System.Globalization;

namespace DiskMap.Core;

/// <summary>
/// The Explore list's search box (Mac: Charts/ListQuery.swift): plain words plus a few tokens
/// (size&gt;1GB, kind:video, changed&gt;1y, in:Downloads, name:report). Anything it doesn't
/// recognise is searched as text, so nothing typed is lost.
/// </summary>
public sealed record ListQuery
{
    public List<string> Words { get; init; } = [];
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public FileKind? Kind { get; init; }
    /// <summary>Not changed for at least this many days.</summary>
    public int? OlderThanDays { get; init; }
    /// <summary>Changed within this many days.</summary>
    public int? NewerThanDays { get; init; }
    /// <summary>Lowercased folder name or path fragment the item must sit under.</summary>
    public string? InFolder { get; init; }
    /// <summary>The tokens that were understood, as typed, for the field to show.</summary>
    public List<string> Tokens { get; init; } = [];

    public bool IsEmpty => Words.Count == 0 && Tokens.Count == 0;

    public static ListQuery Parse(string text)
    {
        var words = new List<string>();
        var tokens = new List<string>();
        long? min = null, max = null;
        FileKind? kind = null;
        int? older = null, newer = null;
        string? inFolder = null;
        foreach (string raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.ToLowerInvariant();
            if (Split(part, "size") is { } size && Bytes(size.Value) is { } bytes)
            {
                if (size.Op == ">") min = bytes; else if (size.Op == "<") max = bytes; else continue;
                tokens.Add(raw);
            }
            else if (part.StartsWith("kind:", StringComparison.Ordinal) && KindOf(part[5..]) is { } k)
            {
                kind = k;
                tokens.Add(raw);
            }
            else if ((Split(part, "changed") ?? Split(part, "opened") ?? Split(part, "modified")) is { } when
                     && Days(when.Value) is { } days)
            {
                if (when.Op == ">") older = days; else if (when.Op == "<") newer = days; else continue;
                tokens.Add(raw);
            }
            else if (part.StartsWith("in:", StringComparison.Ordinal) && part.Length > 3)
            {
                inFolder = part[3..];
                tokens.Add(raw);
            }
            else if (part.StartsWith("name:", StringComparison.Ordinal) && part.Length > 5)
            {
                words.Add(part[5..]);
                tokens.Add(raw);
            }
            else
            {
                words.Add(part);
            }
        }
        return new ListQuery
        {
            Words = words, Tokens = tokens, MinSize = min, MaxSize = max, Kind = kind,
            OlderThanDays = older, NewerThanDays = newer, InFolder = inFolder,
        };
    }

    /// <summary><paramref name="searchText"/>: lowercased name and display path; <paramref name="folderPath"/>: lowercased display path of the folder.</summary>
    public bool Matches(string searchText, string folderPath, long size, FileKind kind, int? ageDays)
    {
        if (MinSize is { } minSize && size <= minSize) return false;
        if (MaxSize is { } maxSize && size >= maxSize) return false;
        if (Kind is { } want && kind != want) return false;
        if (OlderThanDays is { } older && !(ageDays is { } a1 && a1 >= older)) return false;
        if (NewerThanDays is { } newer && !(ageDays is { } a2 && a2 <= newer)) return false;
        if (InFolder is { } folder && !folderPath.Contains(folder, StringComparison.Ordinal)) return false;
        return Words.All(w => searchText.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>"size&gt;1gb" → (">", "1gb"); also "size:&gt;1gb" and "size:1gb" (meaning &gt;).</summary>
    private static (string Op, string Value)? Split(string part, string key)
    {
        if (!part.StartsWith(key, StringComparison.Ordinal)) return null;
        string rest = part[key.Length..];
        bool colon = rest.StartsWith(':');
        if (colon) rest = rest[1..];
        if (rest.Length == 0) return null;
        if (rest[0] is '>' or '<') return (rest[0].ToString(), rest[1..]);
        return colon ? (">", rest) : null;
    }

    /// <summary>"1gb", "500mb", "2.5tb", "100k" → bytes (decimal, as Explorer and Finder count).</summary>
    public static long? Bytes(string text)
    {
        (string Suffix, double Scale)[] units =
            [("tb", 1e12), ("gb", 1e9), ("mb", 1e6), ("kb", 1e3), ("t", 1e12), ("g", 1e9), ("m", 1e6), ("k", 1e3), ("b", 1)];
        foreach (var (suffix, scale) in units)
        {
            if (!text.EndsWith(suffix, StringComparison.Ordinal)) continue;
            return Number(text[..^suffix.Length]) is { } n ? (long)(n * scale) : null;
        }
        return Number(text) is { } plain ? (long)plain : null;
    }

    /// <summary>"30d", "6w", "3m", "1y" → days.</summary>
    public static int? Days(string text)
    {
        if (text.Length == 0 || Number(text[..^1]) is not { } n) return null;
        return text[^1] switch
        {
            'd' => (int)n,
            'w' => (int)(n * 7),
            'm' => (int)(n * 30),
            'y' => (int)(n * 365),
            _ => null,
        };
    }

    /// <summary>Swift's Double(String): a plain decimal, non-negative here; no thousands separators.</summary>
    private static double? Number(string text) =>
        text.Length > 0 && double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double n) && n >= 0
            ? n : null;

    private static FileKind? KindOf(string text) => text switch
    {
        "video" or "videos" or "movie" or "movies" => FileKind.Video,
        "image" or "disk" or "diskimage" or "dmg" or "iso" or "installer" or "installers" or "msi" or "exe" => FileKind.DiskImage,
        "archive" or "archives" or "zip" => FileKind.Archive,
        "app" or "apps" or "application" or "applications" => FileKind.Application,
        "doc" or "docs" or "document" or "documents" => FileKind.Document,
        "db" or "database" or "databases" => FileKind.Database,
        "vm" or "vms" or "virtualdisk" or "vhd" or "vhdx" => FileKind.VirtualDisk,
        "backup" or "backups" => FileKind.DeviceBackup,
        _ => null,
    };
}
