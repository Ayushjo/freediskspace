using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMap.Core;

/// <summary>
/// The rescan baseline (WIN-031): the last scan's tree, plus the journal
/// cursor captured BEFORE that scan ran, persisted so a rescan replays
/// only what changed — same contract as the macOS FSEvents baseline.
/// Files live under `%LOCALAPPDATA%\DiskMap\ScanCache\<fnv-of-root>`:
/// `.dmap` (snapshot codec — shared binary format) + `.marker.json`.
/// </summary>
public static class ScanCache
{
    public sealed record Baseline(
        DiskSnapshot Snapshot,
        UsnJournal.Marker Marker,
        string Backend);

    private sealed class MarkerFile
    {
        [JsonPropertyName("rootPath")] public string RootPath { get; set; } = "";
        [JsonPropertyName("backend")] public string Backend { get; set; } = "";
        [JsonPropertyName("journalId")] public long JournalId { get; set; }
        [JsonPropertyName("usn")] public long Usn { get; set; }
        [JsonPropertyName("savedUtc")] public long SavedUtc { get; set; }
    }

    public static string DirectoryPath => Path.Combine(UserFolders.StateDirectory, "ScanCache");

    private static string Key(string rootPath) => StorageHistory.Hash(rootPath);
    internal static string TreePath(string rootPath) =>
        Path.Combine(DirectoryPath, Key(rootPath) + ".dmap");
    internal static string MarkerPath(string rootPath) =>
        Path.Combine(DirectoryPath, Key(rootPath) + ".marker.json");

    // Saves run in the background, one after another: the 500 MB encode +
    // write of a full-volume tree cost ~2 s on every scan's critical path.
    // Load and Remove wait for them, so a rescan reads the newest baseline
    // and a forced full rescan (Remove) can't be undone by a late save.
    private static readonly object PendingGate = new();
    private static Task _pending = Task.CompletedTask;
    /// <summary>Roots whose baseline Remove dropped: never loaded again until a Save replaces it, even if a file couldn't be deleted.</summary>
    private static readonly HashSet<string> Dropped = [];

    /// <summary>
    /// An elevated run keeps its cache admin-only: the tree indexes every
    /// file on the volume, and code running as the plain user must not
    /// read it. Unelevated runs (tests, the render harness) keep the
    /// default inherited ACL, as before.
    /// </summary>
    private static readonly bool AdminOnly = IsElevated();

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Queues <see cref="Save"/> off the caller's thread. The tree must not
    /// be mutated afterwards — ScanEngine hands it over only once final.
    /// </summary>
    public static void SaveInBackground(string rootPath, FileTree tree, UsnJournal.Marker marker)
    {
        lock (PendingGate)
            _pending = _pending.ContinueWith(_ => Save(rootPath, tree, marker), TaskScheduler.Default);
    }

    /// <summary>Blocks until every queued background save has landed.</summary>
    public static void WaitForPendingSaves()
    {
        Task pending;
        lock (PendingGate) pending = _pending;
        pending.Wait();
    }

    public static Baseline? Load(string rootPath)
    {
        WaitForPendingSaves();
        lock (PendingGate)
            if (Dropped.Contains(rootPath)) return null;
        try
        {
            var marker = JsonSerializer.Deserialize<MarkerFile>(
                File.ReadAllText(MarkerPath(rootPath)),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            // Any scan backend's baseline works — what matters is that the
            // tree carries file ids (a non-NTFS walk's marker simply won't
            // exist, since the journal query fails there first).
            if (marker is null || marker.RootPath != rootPath)
                return null;
            var snapshot = SnapshotStore.Load(TreePath(rootPath));
            // A tree with no file ids can't map journal FRNs — treat as absent.
            if (snapshot.Tree.Count == 0 || snapshot.Tree.FileId.All(id => id == 0))
                return null;
            return new Baseline(snapshot, new UsnJournal.Marker(marker.JournalId, marker.Usn), marker.Backend);
        }
        catch { return null; }
    }

    /// <summary>
    /// Written whole and atomically: tree first, marker second — a crash
    /// between them leaves the OLD marker replaying changes the new tree
    /// already contains, which the FRN re-read absorbs idempotently.
    /// </summary>
    public static void Save(string rootPath, FileTree tree, UsnJournal.Marker marker, string backend = "mft")
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            WriteReplacing(TreePath(rootPath), SnapshotCodec.Encode(
                new DiskSnapshot(rootPath, DateTimeOffset.Now, tree)), AdminOnly);
            var file = new MarkerFile
            {
                RootPath = rootPath,
                Backend = backend,
                JournalId = marker.JournalId,
                Usn = marker.NextUsn,
                SavedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            WriteReplacing(MarkerPath(rootPath), JsonSerializer.SerializeToUtf8Bytes(file), AdminOnly);
            lock (PendingGate) Dropped.Remove(rootPath);
        }
        catch { /* a cache write failure only costs the next scan's speed */ }
    }

    /// <summary>Writes a temp file beside <paramref name="path"/>, then moves it over — a same-volume move keeps the temp file's own ACL.</summary>
    private static void WriteReplacing(string path, byte[] bytes, bool adminOnly)
    {
        string tmp = path + ".tmp";
        // CreateNew below: an existing file would keep whatever ACL it has.
        File.Delete(tmp);
        using (var stream = CreateNew(tmp, adminOnly)) stream.Write(bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// A new file. With <paramref name="adminOnly"/> its ACL is protected
    /// and grants only SYSTEM and Administrators, set at creation so the
    /// bytes are never readable by anyone else.
    /// </summary>
    internal static FileStream CreateNew(string path, bool adminOnly) => adminOnly
        ? new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
            4096, FileOptions.None, AdminOnlySecurity())
        : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    private static FileSecurity AdminOnlySecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>
    /// Drops the baseline — the "full rescan" guarantee (Ctrl+Shift+R).
    /// Fails safe: Load ignores this root until the next Save even when a
    /// file is held open and won't delete, and the marker goes first
    /// (Load needs both files) so a later process can't replay it either.
    /// </summary>
    public static void Remove(string rootPath)
    {
        WaitForPendingSaves();
        lock (PendingGate) Dropped.Add(rootPath);
        try { File.Delete(MarkerPath(rootPath)); } catch { }
        try { File.Delete(TreePath(rootPath)); } catch { }
    }
}
