using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMap.Core;

/// <summary>
/// Sidecar metadata for a saved freedisk.space scan (Mac: SnapshotMeta.swift) — its name,
/// an optional note, a favourite flag and the volume and scan figures at the time it was
/// saved. Stored as `&lt;snapshot name&gt;.meta.json` beside the `.snapshot`, with the Mac's
/// JSON keys so a saved scan's notes read the same on either platform.
/// </summary>
public sealed record SnapshotMeta
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("note")] public string Note { get; init; } = "";
    [JsonPropertyName("favorite")] public bool Favorite { get; init; }
    [JsonPropertyName("volumeName")] public string? VolumeName { get; init; }
    [JsonPropertyName("totalBytes")] public ulong? TotalBytes { get; init; }
    [JsonPropertyName("freeBytes")] public ulong? FreeBytes { get; init; }
    [JsonPropertyName("usedBytes")] public ulong? UsedBytes { get; init; }
    [JsonPropertyName("scannedBytes")] public long? ScannedBytes { get; init; }
    [JsonPropertyName("fileCount")] public long? FileCount { get; init; }
    [JsonPropertyName("folderCount")] public long? FolderCount { get; init; }
    [JsonPropertyName("scanSeconds")] public double? ScanSeconds { get; init; }
    [JsonPropertyName("diskMapVersion")] public string? DiskMapVersion { get; init; }

    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// "Today — 8:51 PM" for a scan saved today, else "Oct 10, 2026 at 8:51 PM"
    /// (Mac: date.formatted(date: .abbreviated, time: .shortened)).
    /// </summary>
    public static string DefaultName(DateTimeOffset date, DateTimeOffset? now = null, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = date.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime();
        if (local.Date == today.Date) return "Today — " + ShortTime(date, culture);
        return local.ToString("MMM d, yyyy", culture) + " at " + ShortTime(date, culture);
    }

    /// <summary>The time half of <see cref="DefaultName"/>: "8:51 PM" (the culture's short time).</summary>
    public static string ShortTime(DateTimeOffset date, CultureInfo? culture = null) =>
        date.ToLocalTime().ToString("t", culture ?? CultureInfo.CurrentCulture);
}

/// <summary>A saved scan as the What changed page lists it: where it is, its header and its notes.</summary>
public sealed record SnapshotRecord(string Path, SnapshotHeader Header, SnapshotMeta Meta, bool IsCurrent)
{
    /// <summary>The live scan's id — it isn't a file.</summary>
    public const string CurrentId = "current-scan";

    public string Id => IsCurrent ? CurrentId : Path;

    /// <summary>
    /// The saved name, except that an automatic "Today — 8:51 PM" is re-derived from the
    /// capture date: stored as-is it still said "Today" two weeks later.
    /// </summary>
    public string DisplayName => DisplayNameAt(DateTimeOffset.Now);

    public string DisplayNameAt(DateTimeOffset now, CultureInfo? culture = null)
    {
        var captured = Header.CapturedAt;
        if (Meta.Name.Length == 0
            || Spaced(Meta.Name) == Spaced("Today — " + SnapshotMeta.ShortTime(captured, culture)))
            return SnapshotMeta.DefaultName(captured, now, culture);
        return Meta.Name;
    }

    /// <summary>ICU writes "8:51\u202FPM", Windows NLS "8:51 PM": compare names with plain spaces.</summary>
    private static string Spaced(string text) => text.Replace('\u202F', ' ').Replace('\u00A0', ' ');

    /// <summary>Short label for compare pickers (avoids truncating to "Curre…"): "Name · 380 GB".</summary>
    public string PickerLabel => PickerLabelAt(DateTimeOffset.Now);

    public string PickerLabelAt(DateTimeOffset now, CultureInfo? culture = null)
    {
        string name = IsCurrent ? "Current" : DisplayNameAt(now, culture);
        string clipped = name.Length > 22 ? name[..20] + "…" : name;
        return $"{clipped} · {HumanUnits.Format(UsedBytes)}";
    }

    public long UsedBytes => Meta.UsedBytes is { } used ? (long)used : Meta.ScannedBytes ?? 0;
    public long FreeBytes => Meta.FreeBytes is { } free ? (long)free : 0;
    public long TotalBytes => Meta.TotalBytes is { } total ? (long)total : UsedBytes + FreeBytes;
}
