using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMap.Core;

/// <summary>
/// Every cleanup freedisk.space made, newest first, numbered, with what was put back since
/// (Mac: CleanupHistory in PutBack.swift, redesign board 10). Only a record: nothing here
/// deletes, and Put Back still goes through <see cref="PutBack.Run"/>. Windows also keeps how
/// many too-big regenerable folders were deleted permanently (AGENTS.md rule 1 exception), so
/// History can say so instead of the Mac's fixed "0".
/// </summary>
public sealed class CleanupHistory
{
    public sealed class Entry
    {
        public int Number { get; set; }
        public CleanupRecord Record { get; set; } = new(DateTimeOffset.MinValue, []);
        /// <summary>Recycle Bin paths of the items already put back.</summary>
        public List<string> Restored { get; set; } = [];
    }

    /// <summary>Where an item is now.</summary>
    public enum ItemState { InTrash, PutBack, TrashEmptied }

    public List<Entry> Entries { get; set; } = [];
    public int DeletedPermanentlyCount { get; set; }
    public long DeletedPermanentlyBytes { get; set; }

    /// <summary>Kept to a few hundred cleanups; the oldest drop off.</summary>
    public const int Limit = 300;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static string DefaultPath() => Path.Combine(UserFolders.StateDirectory, "cleanup-history.json");

    public static CleanupHistory? Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath();
            return File.Exists(path) ? JsonSerializer.Deserialize<CleanupHistory>(File.ReadAllText(path), Json) : null;
        }
        catch { return null; }
    }

    /// <summary>The saved history, or — the first run after updating — one seeded from the last cleanup.</summary>
    public static CleanupHistory LoadOrSeed(string? path = null) => Load(path) ?? Seeded(CleanupRecord.Load());

    /// <summary>Written whole and atomically.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Adds a cleanup at the top with the next number. Empty records are not history.</summary>
    public void Append(CleanupRecord record)
    {
        if (record.Items.Count == 0) return;
        int next = (Entries.Count == 0 ? 0 : Entries.Max(e => e.Number)) + 1;
        Entries.Insert(0, new Entry { Number = next, Record = record });
        if (Entries.Count > Limit) Entries.RemoveRange(Limit, Entries.Count - Limit);
    }

    /// <summary>Notes items that came back, so they read "Put back" from now on.</summary>
    public void MarkRestored(IEnumerable<CleanupRecord.Item> items, int number)
    {
        if (Entries.FirstOrDefault(e => e.Number == number) is not { } entry) return;
        foreach (var item in items)
            if (!entry.Restored.Contains(item.TrashedPath)) entry.Restored.Add(item.TrashedPath);
    }

    /// <summary>Put back wins; otherwise the Recycle Bin copy decides.</summary>
    public static ItemState State(CleanupRecord.Item item, Entry entry, Func<string, bool> existsInTrash) =>
        entry.Restored.Contains(item.TrashedPath) ? ItemState.PutBack
        : existsInTrash(item.TrashedPath) ? ItemState.InTrash
        : ItemState.TrashEmptied;

    /// <summary>Bytes moved to the Recycle Bin and not put back, over every cleanup.</summary>
    public long FreedBytes =>
        Entries.Sum(e => e.Record.Items.Where(i => !e.Restored.Contains(i.TrashedPath)).Sum(i => i.Bytes));

    /// <summary>Bytes still in the Recycle Bin that Put Back can return.</summary>
    public long BytesInTrash(Func<string, bool> existsInTrash) =>
        Entries.Sum(e => e.Record.Items.Where(i => State(i, e, existsInTrash) == ItemState.InTrash).Sum(i => i.Bytes));

    public static CleanupHistory Seeded(CleanupRecord? last)
    {
        var history = new CleanupHistory();
        if (last is not null) history.Append(last);
        return history;
    }

    /// <summary>A path still in the Recycle Bin (file or folder).</summary>
    public static bool ExistsInTrash(string path) => File.Exists(path) || Directory.Exists(path);

    // ---- Called by CleanupQueue after it acts ----

    /// <summary>A commit's record joins the history (never fails the commit).</summary>
    public static void RecordCommit(CleanupRecord record, string? path = null)
    {
        lock (Gate)
        {
            try
            {
                var history = Load(path) ?? new CleanupHistory();
                history.Append(record);
                history.Save(path);
            }
            catch { /* a failed history write never fails the commit */ }
        }
    }

    /// <summary>The documented permanent delete of too-big regenerable folders is counted, honestly.</summary>
    public static void RecordPermanentDelete(int count, long bytes, string? path = null)
    {
        if (count <= 0) return;
        lock (Gate)
        {
            try
            {
                var history = LoadOrSeed(path);
                history.DeletedPermanentlyCount += count;
                history.DeletedPermanentlyBytes += Math.Max(0, bytes);
                history.Save(path);
            }
            catch { }
        }
    }

    /// <summary>Put Back for some items of one cleanup: restores them and marks them in the history.</summary>
    public static PutBackReport PutBackItems(IReadOnlyList<CleanupRecord.Item> items, int number, string? path = null)
    {
        lock (Gate)
        {
            var history = LoadOrSeed(path);
            if (history.Entries.FirstOrDefault(e => e.Number == number) is not { } entry) return new PutBackReport([], []);
            var report = PutBack.Run(new CleanupRecord(entry.Record.Date, items.ToList()));
            history.MarkRestored(report.Restored, number);
            try { history.Save(path); } catch { }
            // The last cleanup's own Put Back no longer offers what came back.
            if (CleanupRecord.Load() is { } last && last.Date == entry.Record.Date)
            {
                var back = report.Restored.Select(r => r.TrashedPath).ToHashSet();
                new CleanupRecord(last.Date, last.Items.Where(i => !back.Contains(i.TrashedPath)).ToList()).Save();
            }
            return report;
        }
    }
}
