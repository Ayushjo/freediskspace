using System.Globalization;

namespace DiskMap.Core;

/// <summary>One tool in Developer's strip: name, size, and how much of it rebuilds by itself.</summary>
public sealed record DeveloperTile(DeveloperEcosystem? Ecosystem, string Title, long Bytes, long RebuildBytes)
{
    /// <summary>The tool's id, or "more" for the folded tile.</summary>
    public string Id => Ecosystem?.ToString().ToLowerInvariant() ?? "more";
}

/// <summary>
/// The Developer page's words and summaries, kept out of the UI so they are tested here
/// (Mac: DeveloperStorageView's statics and DeveloperLabels). Pure functions of the catalog.
/// </summary>
public static class DeveloperBoard
{
    /// <summary>The tools, biggest first; past <paramref name="limit"/> they fold into one "N more" tile.</summary>
    public static List<DeveloperTile> Tiles(IEnumerable<DeveloperItem> items, int limit)
    {
        var by = new Dictionary<DeveloperEcosystem, (long Bytes, long Rebuild)>();
        foreach (var item in items)
        {
            var row = by.GetValueOrDefault(item.Ecosystem);
            row.Bytes += item.Bytes;
            if (item.Reclaimability == DeveloperReclaimability.Reclaimable) row.Rebuild += item.Bytes;
            by[item.Ecosystem] = row;
        }
        var sorted = by.Where(kv => kv.Value.Bytes > 0)
            .Select(kv => new DeveloperTile(kv.Key, kv.Key.Title(), kv.Value.Bytes, kv.Value.Rebuild))
            .OrderByDescending(t => t.Bytes)
            .ThenBy(t => t.Title, StringComparer.Ordinal)
            .ToList();
        if (sorted.Count <= limit || limit <= 1) return sorted;
        var rest = sorted.Skip(limit - 1).ToList();
        var more = new DeveloperTile(null, $"{rest.Count} more", rest.Sum(t => t.Bytes), rest.Sum(t => t.RebuildBytes));
        return [.. sorted.Take(limit - 1), more];
    }

    /// <summary>
    /// The tick line under "Rebuilds by itself": safe when every dependency is pinned
    /// (Mac reinstallLine).
    /// </summary>
    public static (string Title, string Detail, bool Ok) ReinstallLine(IReadOnlyList<DeveloperItem> items)
    {
        var unpinned = items.Where(i => i.RebuildCost == RebuildCost.NetworkedUnpinned).ToList();
        if (unpinned.Count == 0)
        {
            var locks = items.Select(i => i.Lockfile).OfType<string>().Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList();
            return ("Reinstall is safe.", locks.Count == 0
                ? "These are build output and caches; the next build makes them again."
                : $"{string.Join(" and ", locks)} {(locks.Count == 1 ? "is" : "are")} here, so the same versions come back.", true);
        }
        return ("Check before removing.",
            $"{CountLabel(unpinned.Count, "folder")} {(unpinned.Count == 1 ? "has" : "have")} no lockfile, so a reinstall may pull newer versions.",
            false);
    }

    /// <summary>The note card under a folder or project (Mac folderNote).</summary>
    public static (string Title, string Body) FolderNote(bool stale) => stale
        ? ("Untouched for over six months", "If you don't work on this any more, removing these costs nothing until you open it again.")
        : ("You worked on this recently", "The next build will take a few minutes longer while the tools put these back.");

    /// <summary>The line after an item's tag: the tool's own way when the Recycle Bin is wrong, else what rebuilding costs.</summary>
    public static string ItemNote(DeveloperItem item) =>
        item.Recipe is { TrashIsUnsafe: true } recipe
            ? $"Use {recipe.Command.Replace("`", "")} instead."
            : $"{item.Ecosystem.Title()} · {RebuildShort(item.RebuildCost)}";

    /// <summary>A chip's size: "4.2" in GB, "350M" under a gigabyte, "12K" under a megabyte.</summary>
    public static string ChipShort(long bytes)
    {
        if (bytes >= 1_000_000_000) return (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture);
        if (bytes >= 1_000_000) return $"{(long)Math.Round(bytes / 1e6, MidpointRounding.AwayFromZero)}M";
        return $"{Math.Max(1, (long)Math.Round(bytes / 1e3, MidpointRounding.AwayFromZero))}K";
    }

    public static string RebuildShort(RebuildCost cost) => cost switch
    {
        RebuildCost.Free => "Free",
        RebuildCost.Cheap => "Offline build",
        RebuildCost.Networked => "Re-download",
        _ => "No lockfile",
    };

    /// <summary>"Today", "Yesterday", "12d ago", "3 months ago", "2 years ago", "Very old", "Unknown".</summary>
    public static string LastChange(int day, int today)
    {
        if (day <= 0) return "Unknown";
        int age = Math.Max(0, today - day);
        if (age == 0) return "Today";
        if (age == 1) return "Yesterday";
        return AgeString(age);
    }

    /// <summary>Mac ForgottenAgeFormat.string: compact but honest about absurd packaging dates.</summary>
    public static string AgeString(int ageDays)
    {
        if (ageDays >= 15 * 365) return "Very old";
        if (ageDays < 60) return $"{ageDays}d ago";
        if (ageDays < 365)
        {
            int m = Math.Max(1, ageDays / 30);
            return m == 1 ? "1 month ago" : $"{m} months ago";
        }
        int y = Math.Max(1, ageDays / 365);
        return y == 1 ? "1 year ago" : $"{y} years ago";
    }

    public static string GitShort(GitState state) => state switch
    {
        GitState.InSync => "Pushed",
        GitState.NoRemote => "No remote",
        GitState.Differs => "Unpushed",
        GitState.NotARepository => "No git",
        _ => "Unknown",
    };

    /// <summary>"1 place", "12 places", "1,204 items" (Mac countLabel).</summary>
    public static string CountLabel(int count, string singular, string? plural = null) =>
        $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>A project or folder is stale after <see cref="DeveloperSummary.StaleAfterDays"/> without a change (strictly more, as the Mac's panel).</summary>
    public static bool IsStale(int day, int today) => day > 0 && today - day > DeveloperSummary.StaleAfterDays;
}
