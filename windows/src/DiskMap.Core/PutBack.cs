using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskMap.Core.Native;

namespace DiskMap.Core;

/// <summary>
/// What the last cleanup moved to the Recycle Bin and where it went, kept
/// so it can be put back — after a relaunch too (TASK-080 port).
///
/// The Windows Recycle Bin has no API that returns where a file landed:
/// recycling writes a <c>$R&lt;id&gt;</c> data file plus a <c>$I&lt;id&gt;</c>
/// metadata file under <c>&lt;drive&gt;:\$Recycle.Bin\&lt;user-SID&gt;</c>.
/// The $I file carries the original path, so a commit resolves each item's
/// real bin path after the fact and records it here.
/// </summary>
public sealed record CleanupRecord(
    DateTimeOffset Date,
    List<CleanupRecord.Item> Items)
{
    public sealed record Item(string OriginalPath, string TrashedPath, long Bytes);

    public static string DefaultPath() =>
        Path.Combine(UserFolders.StateDirectory, "last-cleanup.json");

    public static CleanupRecord? Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath();
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CleanupRecord>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    /// <summary>Written whole and atomically; an empty record replaces a used one.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Outcome of a Put Back run.</summary>
public sealed record PutBackReport(
    List<CleanupRecord.Item> Restored,
    List<(CleanupRecord.Item Item, string Reason)> Skipped);

/// <summary>
/// Reads the Recycle Bin's per-user store: parse <c>$I*</c> files (the
/// metadata record for each recycled item — original path, size, deletion
/// time) and map them to their <c>$R*</c> payloads.
/// </summary>
public static class RecycleBinStore
{
    public sealed record Entry(string OriginalPath, string DataPath, string InfoPath,
        long OriginalBytes, long DeletedFileTimeUtc);

    /// <summary>
    /// Entries currently sitting in the bin on the volume that holds
    /// <paramref name="pathOnVolume"/> for the current user.
    /// </summary>
    public static IEnumerable<Entry> Entries(string pathOnVolume, DateTime? createdSinceUtc = null)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(pathOnVolume));
        if (root is null) yield break;
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        if (sid.Length == 0) yield break;
        string store = Path.Combine(root, "$Recycle.Bin", sid);
        if (!Directory.Exists(store)) yield break;

        IEnumerable<FileInfo> infoFiles;
        try { infoFiles = new DirectoryInfo(store).EnumerateFiles("$I*"); }
        catch { yield break; }

        foreach (var infoFile in infoFiles)
        {
            // Creation time comes free with the listing; skipping old
            // records avoids opening them — freshly recycled ones cost
            // ~4 ms each to open (on-access scan), thousands per commit.
            if (createdSinceUtc is { } since && infoFile.CreationTimeUtc < since) continue;
            Entry? entry = ReadInfoFile(infoFile.FullName);
            if (entry is not null) yield return entry;
        }
    }

    /// <summary>
    /// The newest bin entry whose recorded original path equals
    /// <paramref name="originalPath"/> — or null (emptied, or moved by
    /// another user/process).
    /// </summary>
    public static Entry? Resolve(string originalPath)
    {
        // Newest record first, stop at the first match: the newest match is
        // the answer, so a full bin is never read for one path (each fresh
        // $I open costs ~4 ms of on-access scanning).
        string normalized = Normalize(originalPath);
        string? root = Path.GetPathRoot(normalized);
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        if (root is null || sid.Length == 0) return null;
        string store = Path.Combine(root, "$Recycle.Bin", sid);
        if (!Directory.Exists(store)) return null;
        IEnumerable<FileInfo> infos;
        try { infos = new DirectoryInfo(store).EnumerateFiles("$I*").OrderByDescending(f => f.CreationTimeUtc).ToList(); }
        catch { return null; }
        foreach (var info in infos)
            if (ReadInfoFile(info.FullName) is { } e
                && e.OriginalPath.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    /// <summary>
    /// <see cref="Resolve"/> for many paths, reading each volume's bin once
    /// instead of once per path. Keyed by normalized full path; paths not
    /// in the bin are absent.
    /// </summary>
    public static Dictionary<string, Entry> ResolveAll(IEnumerable<string> originalPaths, DateTime? recycledSinceUtc = null)
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var volume in originalPaths.Select(Normalize)
                     .GroupBy(p => Path.GetPathRoot(p) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var wanted = volume.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Entries(volume.First(), recycledSinceUtc))
            {
                if (!wanted.Contains(e.OriginalPath)) continue;
                if (!result.TryGetValue(e.OriginalPath, out var seen) || e.DeletedFileTimeUtc > seen.DeletedFileTimeUtc)
                    result[e.OriginalPath] = e;
            }
        }
        return result;
    }

    internal static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

    /// <summary>
    /// $I v1 (Win7/8): version(8)=1, size(8), deletedTime(8), name = 260
    /// UTF-16 chars fixed. v2 (Win10+): version(8)=2, size(8), time(8),
    /// nameLen(4 incl. null), name UTF-16 variable. Anything else: null.
    /// </summary>
    internal static Entry? ReadInfoFile(string infoPath)
    {
        byte[] data;
        try { data = File.ReadAllBytes(infoPath); }
        catch { return null; }
        if (data.Length < 24) return null;
        long version = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(0));
        long size = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
        long deleted = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(16));
        string original;
        if (version == 2)
        {
            if (data.Length < 28) return null;
            int chars = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(24));
            int byteCount = Math.Min(chars * 2, data.Length - 28);
            if (byteCount <= 0) return null;
            original = System.Text.Encoding.Unicode.GetString(data.AsSpan(28, byteCount));
        }
        else if (version == 1)
        {
            int byteCount = Math.Min(260 * 2, data.Length - 24);
            if (byteCount <= 0) return null;
            original = System.Text.Encoding.Unicode.GetString(data.AsSpan(24, byteCount));
        }
        else return null;

        original = original.TrimEnd('\0');
        if (original.Length == 0) return null;
        string dataPath = Path.Combine(
            Path.GetDirectoryName(infoPath)!,
            "$R" + Path.GetFileName(infoPath)[2..]);
        if (!File.Exists(dataPath) && !Directory.Exists(dataPath)) return null;
        return new Entry(original, dataPath, infoPath, size, deleted);
    }
}

public static class PutBack
{
    private const string NotInBin = "No longer in the Recycle Bin";
    private const string ExplorerRestores = "restore it from the Recycle Bin in File Explorer";

    /// <summary>
    /// Moves the last cleanup's items from the Recycle Bin back where they
    /// were. Never a removal: an item is skipped, with the reason, when
    /// it is no longer in the bin or when something new already sits at
    /// its old path (nothing is replaced). A missing parent folder is
    /// recreated. Only File.Move / Directory.Move — restoring a file.
    ///
    /// The record is a file any program the user runs can rewrite, and this
    /// app runs elevated, so nothing in it is trusted: only this user's bin
    /// copy of that very path comes back, never to a place the queue would
    /// refuse to stage, and elevated the moves run as the signed-in user.
    /// </summary>
    public static PutBackReport Run(CleanupRecord record) => Run(record, asShellUser: UnelevatedLaunch.IsElevated);

    /// <param name="asShellUser">Move with the desktop shell's token; tests force it unelevated.</param>
    internal static PutBackReport Run(CleanupRecord record, bool asShellUser)
    {
        var restored = new List<CleanupRecord.Item>();
        var skipped = new List<(CleanupRecord.Item, string)>();
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";

        // Stale bin paths (the bin renamed an item on a later collision) are
        // looked up anew: one read of each volume's bin for all of them.
        var resolved = new Dictionary<string, RecycleBinStore.Entry>();
        try
        {
            var stale = record.Items.Where(i => !File.Exists(i.TrashedPath) && !Directory.Exists(i.TrashedPath))
                .Select(i => i.OriginalPath).ToList();
            if (stale.Count > 0) resolved = RecycleBinStore.ResolveAll(stale);
        }
        catch { /* an unreadable path is skipped by the check below */ }

        var moves = new List<(CleanupRecord.Item Item, string Original, RecycleBinStore.Entry Copy)>();
        foreach (var item in record.Items)
        {
            try
            {
                string original = RecycleBinStore.Normalize(item.OriginalPath);
                if (CleanupQueue.IsExcludedPath(original))
                {
                    skipped.Add((item, "A protected location — " + ExplorerRestores));
                    continue;
                }
                var copy = BinCopyOf(item.TrashedPath, original, sid)
                    ?? (resolved.TryGetValue(original, out var e) ? BinCopyOf(e.DataPath, original, sid) : null);
                if (copy is null) skipped.Add((item, NotInBin));
                else moves.Add((item, original, copy));
            }
            catch { skipped.Add((item, NotInBin)); }
        }

        void MoveAll()
        {
            foreach (var (item, original, copy) in moves)
            {
                if (File.Exists(original) || Directory.Exists(original))
                {
                    skipped.Add((item, $"Something new is at {original}"));
                    continue;
                }
                try
                {
                    string? parent = Path.GetDirectoryName(original);
                    if (parent is not null) Directory.CreateDirectory(parent);
                    if (Directory.Exists(copy.DataPath)) Directory.Move(copy.DataPath, original);
                    else File.Move(copy.DataPath, original);
                    // Its $I metadata record goes too — otherwise the bin keeps
                    // listing a ghost entry. Only the one checked above, inside
                    // this user's $Recycle.Bin, never user data.
                    File.Delete(copy.InfoPath);
                    restored.Add(item);
                }
                catch (UnauthorizedAccessException) { skipped.Add((item, "Access denied — " + ExplorerRestores)); }
                catch (Exception ex) { skipped.Add((item, ex.Message)); }
            }
        }

        if (!asShellUser) MoveAll();
        else if (!UnelevatedLaunch.RunAsShellUser(MoveAll))   // fail closed: no shell token, no move
            skipped.AddRange(moves.Select(m => (m.Item, "Couldn't act as the signed-in user — " + ExplorerRestores)));
        return new PutBackReport(restored, skipped);
    }

    /// <summary>
    /// The bin's own record of <paramref name="trashed"/> when it is this user's
    /// Recycle Bin copy of <paramref name="original"/>: a $R entry directly in
    /// &lt;original's drive&gt;\$Recycle.Bin\&lt;user SID&gt; whose $I sibling names
    /// that same path. Null for anything else.
    /// </summary>
    internal static RecycleBinStore.Entry? BinCopyOf(string trashed, string original, string sid)
    {
        string full = Path.GetFullPath(trashed);
        string name = Path.GetFileName(full);
        string? root = Path.GetPathRoot(original);
        if (sid.Length == 0 || string.IsNullOrEmpty(root) || name.Length <= 2 || !name.StartsWith("$R", StringComparison.Ordinal))
            return null;
        string bin = Path.Combine(root, "$Recycle.Bin", sid);
        if (!string.Equals(Path.GetDirectoryName(full), bin, StringComparison.OrdinalIgnoreCase)) return null;
        var entry = RecycleBinStore.ReadInfoFile(Path.Combine(bin, "$I" + name[2..]));
        return entry is not null && RecycleBinStore.Normalize(entry.OriginalPath).Equals(original, StringComparison.OrdinalIgnoreCase)
            ? entry : null;
    }
}
