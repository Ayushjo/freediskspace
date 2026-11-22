using DiskMap.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core;

/// <summary>
/// Incremental rescan (WIN-031): replay the USN journal since the
/// baseline's marker and apply just those records to the cached tree —
/// the Windows counterpart of the macOS FSEvents rescan (TASK-061).
///
/// A rescan is a three-part question: did the journal wrap, is the
/// change set sane, and do untouched folders still check out. Any "no"
/// returns null with a reason and the caller does a full walk instead —
/// the journal is an accelerator, never an authority.
///
/// Fallbacks: journal id changed (recreated), the window after the
/// marker was overwritten before we read it, more than <see cref=
/// "ChangeFlood"/> records, a baseline without file ids (walk backend),
/// a changed record that can't be read, or a spot check that disagreed
/// or verified too few folders. All are silent wins for correctness.
/// </summary>
internal static class IncrementalScan
{
    /// <summary>Beyond this many touched records a replay costs more than a walk.</summary>
    private const int ChangeFlood = 100_000;
    private const int SpotCheckFolders = 64;
    /// <summary>Fewer verified folders than this (or than the tree has) and the replay isn't trusted.</summary>
    private const int SpotCheckMinimum = 16;

    public static WalkResult? TryRescan(
        string root, IProgress<ScanEngine.ScanProgress>? progress,
        CancellationToken cancellationToken, out string? whyNot)
    {
        whyNot = "no baseline";
        var baseline = ScanCache.Load(root);
        if (baseline is null) return null;
        var baseTree = baseline.Snapshot.Tree;

        string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(root));
        whyNot = "not a local drive";
        if (volumeRoot is null || volumeRoot.Length < 3 || volumeRoot[1] != ':') return null;
        string volumePath = @"\\?\" + char.ToUpperInvariant(volumeRoot[0]) + ":";
        // Same rule as the MFT scan: this volume's records can't describe a folder behind a link.
        whyNot = "scan root is, or is under, a link or mount point";
        if (MftScanner.CrossesLink(root)) return null;

        whyNot = "needs administrator";
        if (!Win32.EnableBackupPrivileges()) return null;
        using var volume = MftScanner.OpenVolume(volumePath);
        if (volume.IsInvalid) return null;

        whyNot = "no USN journal";
        if (UsnJournal.Query(volume) is not { } current) return null;
        whyNot = "journal recreated";
        if (current.JournalId != baseline.Marker.JournalId) return null;

        whyNot = "not NTFS";
        if (!MftScanner.GetVolumeData(volume, out var data)) return null;
        whyNot = "unsupported MFT geometry";
        if (MftScanner.GeometryOf(data) is not { } geometry) return null;
        int recordSize = geometry.RecordSize;
        long clusterSize = geometry.ClusterSize;
        whyNot = "$MFT layout unreadable";
        var extents = MftScanner.ReadMftExtents(
            volume, recordSize, clusterSize, data.MftStartLcn, data.MftValidDataLength);
        if (extents is null) return null;

        whyNot = "journal unreadable";
        if (UsnJournal.ReadAll(volume, baseline.Marker, cancellationToken) is not { } changes)
            return null;
        whyNot = "journal wrapped";
        if (changes.Wrapped) return null;
        var changedFrns = changes.Entries.Select(e => e.Frn).ToHashSet();
        whyNot = "change flood";
        if (changedFrns.Count > ChangeFlood) return null;

        var started = System.Diagnostics.Stopwatch.StartNew();
        progress?.Report(new ScanEngine.ScanProgress(0, 0, 0,
            $"Applying {changedFrns.Count:N0} changes…", []));

        // Only a few hundred records changed, so everything here is sized
        // by the change set; the 8M unchanged nodes are copied in a single
        // walk of the baseline tree (per-node dictionaries, spec lists and
        // name sets made this slower than a full MFT read).
        // Scanners store the root node without a file id, so ask the disk:
        // without it, changes directly under the root can't be placed.
        long rootFrn = baseTree.FileId[0] & Win32.UsnRecordMask;
        if (rootFrn == 0)
            rootFrn = (StorageSharing.FactsOfPublic(root)?.FileId ?? 0) & Win32.UsnRecordMask;
        whyNot = "scan root unreadable";
        if (rootFrn == 0) return null;
        long FrnOf(int node) => node == 0 ? rootFrn : baseTree.FileId[node] & Win32.UsnRecordMask;

        // Re-read every touched record: parses live → its current state is
        // authoritative; a free or reused slot means delete. USN reasons
        // only say which FRNs to look at.
        var updates = new Dictionary<long, MftScanner.EntryInfo>();
        var dead = new HashSet<long>();
        foreach (long frn in changedFrns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frn == rootFrn) continue;
            // Torn, unreadable, past the $MFT extents read above, or live
            // but unparseable: the journal says it changed, so calling it
            // deleted would be a guess — and the marker would move past it.
            whyNot = $"changed record {frn} unreadable";
            var rec = MftScanner.ReadRecordAt(volume, extents, recordSize, clusterSize, frn);
            if (rec is null) return null;
            if (MftScanner.ParseEntry(rec, geometry, out var info)) updates[frn] = info;
            else if (MftScanner.IsUnused(rec)) dead.Add(frn);
            else return null;
        }

        // frn → baseline nodes, only for the FRNs this replay asks about:
        // the changed ones and the parents of their current names. Hard
        // links share a frn, hence a list.
        var interesting = new HashSet<long>(changedFrns);
        foreach (var info in updates.Values)
        {
            interesting.Add(info.ParentFrn);
            foreach (var (lp, _) in info.Links) interesting.Add(lp);
        }
        var nodesByFrn = new Dictionary<long, List<int>>();
        for (int i = 0; i < baseTree.Count; i++)
        {
            long frn = baseTree.FileId[i] & Win32.UsnRecordMask;
            if (frn == 0 || !interesting.Contains(frn)) continue;
            (nodesByFrn.TryGetValue(frn, out var l) ? l : nodesByFrn[frn] = []).Add(i);
        }
        var removedFrns = dead.Where(nodesByFrn.ContainsKey).ToHashSet();

        // Resolve "in the scanned subtree": every surviving baseline frn is
        // (its parents decide reachability during the rebuild); a frn new to
        // the tree resolves once its parent does. Iterate until stable — a
        // moved-in dir makes its own parent resolvable, then its contents'.
        var resolvedNew = new HashSet<long>();
        bool Resolved(long frn) => frn == rootFrn || resolvedNew.Contains(frn)
            || (nodesByFrn.ContainsKey(frn) && !removedFrns.Contains(frn));
        bool grew;
        do
        {
            grew = false;
            foreach (var (frn, info) in updates)
            {
                if (Resolved(frn) || !Resolved(info.ParentFrn)) continue;
                resolvedNew.Add(frn);
                grew = true;
            }
        } while (grew);
        // Anything resolved-away still updates; unresolved = out of scope.
        foreach (var frn in updates.Keys.ToList())
        {
            if (Resolved(frn)) continue;
            updates.Remove(frn);
            if (nodesByFrn.ContainsKey(frn)) removedFrns.Add(frn);
        }

        // Specs for changed records only, keyed by parent frn. Unchanged
        // baseline children are copied straight from the baseline tree
        // during the rebuild; removal is implicit (removed nodes are
        // skipped, and a removed dir is never visited).
        var childrenOf = new Dictionary<long, List<ChildSpec>>();
        void Emit(long parentFrn, ChildSpec spec)
        {
            // Same rule as a scan: a name that wouldn't join to its own path never enters the tree.
            if (!Resolved(parentFrn) || !FileTree.IsSafeName(spec.Name)) return;
            (childrenOf.TryGetValue(parentFrn, out var l) ? l : childrenOf[parentFrn] = []).Add(spec);
        }
        foreach (var (frn, info) in updates)
        {
            // Names an existing baseline node still holds — whatever is
            // left of the file's name set is created/moved-in.
            var claimed = new HashSet<(long ParentFrn, string Name)>();
            ChildSpec Spec(string name, bool isDir) => new(frn, name, isDir,
                info.Logical, info.Allocated, info.Day, info.CreatedDay,
                info.Attributes, HardLinked: info.Links.Count > 0);
            foreach (int i in nodesByFrn.GetValueOrDefault(frn) ?? [])
            {
                int parentId = baseTree.Parent[i];
                long parentFrn = parentId >= 0 ? FrnOf(parentId) : 0;
                string name = baseTree.NameOf(i);
                // This node survives only if its (parent, name) is still
                // one of the file's names — primary or a link.
                bool stillThere = (info.ParentFrn == parentFrn && info.Name == name)
                    || info.Links.Any(l => l.ParentFrn == parentFrn && l.Name == name);
                if (!stillThere || !claimed.Add((parentFrn, name))) continue;
                Emit(parentFrn, Spec(name, baseTree.IsDirectory[i]));
            }
            if (!claimed.Contains((info.ParentFrn, info.Name)))
                Emit(info.ParentFrn, Spec(info.Name, info.IsDirectory));
            foreach (var (lp, ln) in info.Links)
                if (!claimed.Contains((lp, ln))) Emit(lp, Spec(ln, false));
        }

        // The baseline node of a directory frn — where its unchanged
        // children live. Directories have exactly one name.
        int BaselineDir(long frn) =>
            nodesByFrn.TryGetValue(frn, out var l) && baseTree.IsDirectory[l[0]] ? l[0] : -1;
        bool Unchanged(long frn) => frn != 0 && !removedFrns.Contains(frn) && !updates.ContainsKey(frn);

        var tree = new FileTree(Math.Max(baseTree.Count, 16));
        var (rootModified, rootCreated) = Win32Scanner.RootDays(root);
        int rootId = tree.AddNode(Win32Scanner.RootName(root).AsSpan(), -1, true, 0, 0, rootModified,
            createdDaysSinceEpoch: rootCreated);
        var queue = new Queue<(long Frn, int NewId, int OldId)>();
        queue.Enqueue((rootFrn, rootId, 0));
        int itemCount = 0, notDownloaded = 0;

        while (queue.TryDequeue(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.OldId >= 0)
            {
                // Unchanged: copy the stored row verbatim — the baseline
                // already decided inclusion and flags for this node.
                for (int c = baseTree.FirstChild[frame.OldId]; c != -1; c = baseTree.NextSibling[c])
                {
                    long frn = baseTree.FileId[c] & Win32.UsnRecordMask;
                    if (!Unchanged(frn)) continue;
                    bool isDir = baseTree.IsDirectory[c];
                    byte flags = baseTree.Flags[c];
                    int id = tree.AddNode(baseTree.NameOf(c), frame.NewId, isDir,
                        baseTree.LogicalSize[c], baseTree.AllocatedSize[c], baseTree.ModifiedDay[c],
                        flags: flags, createdDaysSinceEpoch: baseTree.CreatedDay[c], fileId: frn);
                    itemCount++;
                    if (isDir && (flags & NodeFlags.NotDownloaded) == 0)
                        queue.Enqueue((frn, id, c));
                    if (itemCount % 65_536 == 0)
                        progress?.Report(new ScanEngine.ScanProgress(itemCount, 0, 0, "Rebuilding tree…", []));
                }
            }
            if (!childrenOf.TryGetValue(frame.Frn, out var specs)) continue;
            foreach (var spec in specs)
            {
                // Changed/new/moved: the same include/flag rules as a scan.
                var decision = ScanEngine.DecideFromAttributes(
                    spec.Attributes | (spec.IsDirectory ? Win32.FILE_ATTRIBUTE_DIRECTORY : 0),
                    spec.Logical, spec.Allocated);
                if (!decision.Include) continue;
                byte flags = 0;
                if (decision.NotDownloaded) { flags |= NodeFlags.NotDownloaded; notDownloaded++; }
                if (spec.HardLinked) flags |= NodeFlags.HardLink;
                int id = tree.AddNode(spec.Name, frame.NewId, spec.IsDirectory,
                    decision.LogicalSize, decision.AllocatedSize, spec.Day,
                    flags: flags, createdDaysSinceEpoch: spec.CreatedDay, fileId: spec.Frn);
                itemCount++;
                if (spec.IsDirectory && !decision.SkipDescendants)
                    queue.Enqueue((spec.Frn, id, BaselineDir(spec.Frn)));
            }
        }

        if (!SpotCheck(baseTree, root, rootFrn, frn => !changedFrns.Contains(frn) && Unchanged(frn),
                Unchanged, frn => childrenOf.GetValueOrDefault(frn)?.Select(s => s.Name) ?? [], out whyNot))
            return null;

        progress?.Report(new ScanEngine.ScanProgress(itemCount, 0, 0, "", []));
        // The rescan's own tree + end-of-replay cursor are the next baseline
        // (ScanEngine saves it in the background once the tree is final).
        return new WalkResult
        {
            Tree = tree,
            ItemCount = itemCount,
            NotDownloadedCount = notDownloaded,
            Backend = "usn",
            FallbackReason = null,
            ChangedRecords = changedFrns.Count,
            ScanMarker = changes.Marker,
            // 0-denied can't be told from a journal replay — the baseline's
            // number would be a lie about the new tree; leave empty and let
            // the next full walk recount.
        };
    }

    /// <summary>
    /// Untouched folders must still have the children the rebuilt tree
    /// claims — a miss means the journal lost something. Folders are
    /// sampled with a fresh seed (a fixed one let a planted baseline know
    /// which get looked at). One that can't be listed isn't evidence, and
    /// it isn't a pass either: too few verified folders fail the replay.
    /// </summary>
    /// <param name="checkable">Whether a baseline folder (by frn) is untouched, so its listing must match.</param>
    /// <param name="childStays">Whether a baseline child (by frn) is copied into the rebuilt tree.</param>
    /// <param name="addedNames">Names the replay adds under a folder (by frn).</param>
    internal static bool SpotCheck(
        FileTree baseTree, string root, long rootFrn,
        Func<long, bool> checkable, Func<long, bool> childStays,
        Func<long, IEnumerable<string>> addedNames, out string? whyNot)
    {
        whyNot = null;
        var sampled = new HashSet<int>();
        int eligible = 0, verified = 0;
        for (int tries = 0; verified < SpotCheckFolders && tries < SpotCheckFolders * 64; tries++)
        {
            int node = Random.Shared.Next(1, Math.Max(2, baseTree.Count));
            if (node >= baseTree.Count || !baseTree.IsDirectory[node]) continue;
            long frn = baseTree.FileId[node] & Win32.UsnRecordMask;
            if (frn == rootFrn || !checkable(frn)
                || (baseTree.Flags[node] & NodeFlags.NotDownloaded) != 0
                || !sampled.Add(node))
                continue;
            eligible++;
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int c = baseTree.FirstChild[node]; c != -1; c = baseTree.NextSibling[c])
                if (childStays(baseTree.FileId[c] & Win32.UsnRecordMask)) expected.Add(baseTree.NameOf(c));
            expected.UnionWith(addedNames(frn));

            string path = baseTree.PathOf(node, root);
            var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Attributes from the listing itself, as the Win32 scanner
                // reads them (File.GetAttributes never reports
                // RECALL_ON_OPEN), so both apply the same cloud test.
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
                {
                    // Filter like the scanner does: non-cloud reparse
                    // points never enter the tree, so they can't be
                    // evidence either.
                    uint attrs = (uint)entry.Attributes;
                    if ((attrs & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0
                        && !ScanEngine.IsCloudPlaceholder(attrs, fromDirectoryEnumeration: true))
                        continue;
                    onDisk.Add(entry.Name);
                }
            }
            catch { continue; }
            if (!onDisk.SetEquals(expected))
            {
                whyNot = $"spot check disagreed at {path}";
                return false;
            }
            verified++;
        }
        // At least one, and every folder there was when there were few.
        if (verified >= Math.Max(1, Math.Min(SpotCheckMinimum, eligible))) return true;
        whyNot = $"spot check verified {verified} of {eligible} folders";
        return false;
    }

    /// <summary>
    /// One changed/new/moved row of the rebuilt tree: carries the record's
    /// attributes so the scan's include/flag rules re-run on it.
    /// </summary>
    private readonly record struct ChildSpec(
        long Frn, string Name, bool IsDirectory,
        long Logical, long Allocated, int Day, int CreatedDay,
        uint Attributes, bool HardLinked);
}
