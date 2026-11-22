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
    /// file on the volume, so code running as the plain user must not read
    /// it — nor write one the elevated app would then trust. Unelevated
    /// runs (tests, the render harness) keep the default inherited ACL and
    /// read any cache file, as before.
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
                ReadTrusted(MarkerPath(rootPath), AdminOnly),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            // Any scan backend's baseline works — what matters is that the
            // tree carries file ids (a non-NTFS walk's marker simply won't
            // exist, since the journal query fails there first).
            if (marker is null || marker.RootPath != rootPath)
                return null;
            var snapshot = SnapshotCodec.Decode(ReadTrusted(TreePath(rootPath), AdminOnly));
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
    /// A new file. With <paramref name="adminOnly"/> its security is set at
    /// creation (see <see cref="AdminOnlySecurity"/>), so the bytes are
    /// never readable by anyone else.
    /// </summary>
    internal static FileStream CreateNew(string path, bool adminOnly) => adminOnly
        ? new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None,
            4096, FileOptions.None, AdminOnlySecurity())
        : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

    /// <summary>
    /// Owned by Administrators, with a protected DACL granting only SYSTEM
    /// and Administrators. The owner is set, not left to the default: an
    /// owner may always rewrite the DACL, and a machine whose policy makes
    /// the creator the owner would hand that to the plain user.
    /// </summary>
    internal static FileSecurity AdminOnlySecurity()
    {
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    /// <summary>
    /// True when nobody but SYSTEM and Administrators could have written
    /// the file: both own it, and every allowing ACE is theirs (.NET reads
    /// a missing DACL as Everyone: full control, so that fails too).
    /// </summary>
    internal static bool IsAdminOnly(FileSystemSecurity security)
    {
        static bool Trusted(IdentityReference? identity) => identity is SecurityIdentifier sid
            && (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        return Trusted(security.GetOwner(typeof(SecurityIdentifier)))
            && security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .All(rule => rule.AccessControlType == AccessControlType.Deny || Trusted(rule.IdentityReference));
    }

    /// <summary>
    /// A cache file's bytes. With <paramref name="adminOnly"/> only from a
    /// file <see cref="IsAdminOnly"/> accepts — checked on the handle the
    /// bytes are read from, so a file a plain-user process wrote, or swapped
    /// in after the check, is never trusted by the elevated app.
    /// </summary>
    internal static byte[] ReadTrusted(string path, bool adminOnly)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (adminOnly && !IsAdminOnly(stream.GetAccessControl()))
            throw new UnauthorizedAccessException($"{path} could have been written without elevation");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
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
