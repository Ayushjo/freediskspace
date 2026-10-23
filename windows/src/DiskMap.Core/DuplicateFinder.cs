using System.Security.Cryptography;

namespace DiskMap.Core;

public sealed record DuplicateGroup(
    string Hash,
    List<int> FileIDs,
    long SizeEach,
    bool SharesStorage)
{
    /// <summary>
    /// A group whose members include clone families (Mac: init(hash:fileIDs:sizeEach:cloneSets:)).
    /// Each set is ONE physical copy, two or more names on the same blocks (hard links or
    /// ReFS block clones). The group shares storage when one set covers every member.
    /// </summary>
    public DuplicateGroup(string hash, List<int> fileIDs, long sizeEach, IEnumerable<IEnumerable<int>> cloneSets)
        : this(hash, fileIDs, sizeEach, CoversAll(fileIDs, Normalize(cloneSets)))
    {
        _cloneSets = Normalize(cloneSets);
    }

    private readonly IReadOnlyList<IReadOnlyList<int>>? _cloneSets;

    /// <summary>
    /// Members that share blocks with each other (sorted ids, two or more each, ordered by
    /// first id). A set's blocks stay until every member is gone; files in no set occupy
    /// their own blocks. A group built with SharesStorage and no sets is one set of all.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> CloneSets =>
        _cloneSets ?? (SharesStorage && FileIDs.Count > 1 ? [FileIDs.Order().ToList()] : []);

    private static List<IReadOnlyList<int>> Normalize(IEnumerable<IEnumerable<int>> sets) =>
        sets.Select(s => (IReadOnlyList<int>)s.Distinct().Order().ToList())
            .Where(s => s.Count > 1)
            .OrderBy(s => s[0])
            .ToList();

    private static bool CoversAll(List<int> fileIDs, List<IReadOnlyList<int>> sets) =>
        sets.Count == 1 && sets[0].Count == fileIDs.Distinct().Count();

    /// <summary>The clone set <paramref name="id"/> belongs to, or null when it has its own blocks.</summary>
    public IReadOnlyList<int>? CloneSetOf(int id) => CloneSets.FirstOrDefault(s => s.Contains(id));

    /// <summary>Physical copies: each clone set, then each file on its own.</summary>
    private IEnumerable<IReadOnlyList<int>> PhysicalSets()
    {
        var sets = CloneSets;
        var cloned = sets.SelectMany(s => s).ToHashSet();
        return sets.Concat(FileIDs.Where(id => !cloned.Contains(id)).Select(id => (IReadOnlyList<int>)[id]));
    }

    /// <summary>Distinct sets of blocks among the copies.</summary>
    public int PhysicalCopies => FileIDs.Count - CloneSets.Sum(s => s.Count - 1);

    /// <summary>
    /// Bytes a later confirm would actually free. Content copies each
    /// occupy their own blocks. Shared extents count once, and only when
    /// every member of that clone set is being deleted.
    /// </summary>
    public long ReclaimableBytes(IReadOnlySet<int> deleting)
    {
        if (SizeEach <= 0) return 0;
        return SizeEach * PhysicalSets().Count(set => set.All(deleting.Contains));
    }

    /// <summary>
    /// Same rule on the on-disk basis the treemap and cleanup queue use (Mac
    /// reclaimableBytes(deleting:onDisk:)). SizeEach is the LOGICAL size, the matching key;
    /// what deleting frees is allocated space, which differs for compressed or sparse files.
    /// </summary>
    public long ReclaimableBytes(IReadOnlySet<int> deleting, Func<int, long> onDisk) =>
        PhysicalSets().Where(set => set.All(deleting.Contains))
            .Sum(set => Math.Max(0, set.Max(onDisk)));

    /// <summary>Oldest modified day, then lowest id — the file that stays unchecked.</summary>
    public int? DefaultKeeperId(Func<int, int> modifiedDay) =>
        FileIDs.Count == 0 ? null
            : FileIDs.MinBy(id => (modifiedDay(id), id));
}

/// <summary>Where a duplicate search is (Mac: DuplicateScanPhase).</summary>
public enum DuplicateScanPhase
{
    Idle, Preparing, Collecting, Grouping, Hashing, Assembling, Complete, NoResults, Cancelled, Failed,
}

public static class DuplicateScanPhaseExtensions
{
    public static string Title(this DuplicateScanPhase phase) => phase switch
    {
        DuplicateScanPhase.Idle => "Ready",
        DuplicateScanPhase.Preparing => "Preparing duplicate search…",
        DuplicateScanPhase.Collecting => "Collecting candidate files…",
        DuplicateScanPhase.Grouping => "Grouping by size…",
        DuplicateScanPhase.Hashing => "Hashing colliding files…",
        DuplicateScanPhase.Assembling => "Assembling duplicate groups…",
        DuplicateScanPhase.Complete => "Finished",
        DuplicateScanPhase.NoResults => "No duplicates found",
        DuplicateScanPhase.Cancelled => "Cancelled",
        _ => "Failed",
    };
}

internal sealed record DuplicateScanResult(List<DuplicateGroup> Groups, int FullContentHashCalls);

/// <summary>
/// Three-phase duplicate detection, cheapest checks first:
///
///   1. Group by exact logical size.
///   2. Within a size group, hash only the first 64 KB.
///   3. Only for files that still collide, hash full content —
///      unless CloneDetector.AreLikelyClones says they share a full
///      extent map. A clone overwritten past the 64 KB window still
///      collides on the partial hash and must be hashed.
///
/// A clone family and a plain copy of the same contents are one group (Mac PR #23): one
/// member per family is hashed, and the family is kept as a clone set of that group.
/// </summary>
public static class DuplicateFinder
{
    public static async Task<List<DuplicateGroup>> FindDuplicatesAsync(
        List<(int Id, string Path, long Size)> candidates)
    {
        var result = await ScanAsync(candidates);
        return result.Groups;
    }

    /// <summary>
    /// The search with progress (phase, size groups done, size groups in all) and
    /// cancellation, which throws <see cref="OperationCanceledException"/>. The progress
    /// callback runs on worker threads.
    /// </summary>
    public static async Task<List<DuplicateGroup>> FindDuplicatesAsync(
        List<(int Id, string Path, long Size)> candidates,
        Action<DuplicateScanPhase, int, int>? progress,
        CancellationToken cancellationToken)
    {
        var result = await ScanAsync(candidates, progress, cancellationToken);
        return result.Groups;
    }

    /// <summary>
    /// Regular files under root, skipping directories, empty files, and
    /// cloud placeholders (opening those would recall the content).
    /// </summary>
    public static List<(int Id, string Path, long Size)> Candidates(FileTree tree, string root) =>
        Candidates(tree, root, collidingSizesOnly: false, CancellationToken.None, null);

    /// <summary>
    /// <see cref="Candidates(FileTree, string)"/> restricted to sizes seen
    /// at least twice — the only files that can collide (macOS PR #16).
    /// PathOf is string work per node, so a drive scan of millions of files
    /// skips nearly all of it. Produces the same groups.
    /// </summary>
    public static List<(int Id, string Path, long Size)> SizeCollidingCandidates(FileTree tree, string root) =>
        Candidates(tree, root, collidingSizesOnly: true, CancellationToken.None, null);

    /// <summary>
    /// <see cref="SizeCollidingCandidates(FileTree, string)"/> that can be cancelled and
    /// reports (items examined, candidates so far) as it goes (Mac: cancellableCandidates).
    /// </summary>
    public static List<(int Id, string Path, long Size)> SizeCollidingCandidates(FileTree tree, string root,
        CancellationToken cancellationToken, Action<int, int>? progress = null) =>
        Candidates(tree, root, collidingSizesOnly: true, cancellationToken, progress);

    private static bool IsCandidate(FileTree tree, int id) =>
        !tree.IsDirectory[id]
        && (tree.Flags[id] & NodeFlags.NotDownloaded) == 0
        && tree.LogicalSize[id] > 0;

    private static List<(int Id, string Path, long Size)> Candidates(FileTree tree, string root, bool collidingSizesOnly,
        CancellationToken cancellationToken, Action<int, int>? progress)
    {
        var countsBySize = new Dictionary<long, int>();
        if (collidingSizesOnly)
        {
            for (int id = 1; id < tree.Count; id++)
            {
                if ((id & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (IsCandidate(tree, id))
                    countsBySize[tree.LogicalSize[id]] = countsBySize.GetValueOrDefault(tree.LogicalSize[id]) + 1;
            }
        }
        var result = new List<(int, string, long)>();
        var stack = new Stack<int>();
        if (tree.Count > 0) stack.Push(0);
        int examined = 0;
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (id > 0 && IsCandidate(tree, id)
                && (!collidingSizesOnly || countsBySize.GetValueOrDefault(tree.LogicalSize[id]) > 1))
            {
                result.Add((id, tree.PathOf(id, root), tree.LogicalSize[id]));
            }
            int child = tree.FirstChild[id];
            while (child != -1)
            {
                stack.Push(child);
                child = tree.NextSibling[child];
            }
            examined++;
            if ((examined & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(examined, result.Count);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke(examined, result.Count);
        return result;
    }

    internal static async Task<DuplicateScanResult> ScanAsync(
        List<(int Id, string Path, long Size)> candidates,
        Action<DuplicateScanPhase, int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke(DuplicateScanPhase.Grouping, 0, 0);
        var bySize = new Dictionary<long, List<(int Id, string Path)>>();
        foreach (var c in candidates)
        {
            if (c.Size <= 0) continue;
            if (!bySize.TryGetValue(c.Size, out var list))
                bySize[c.Size] = list = [];
            list.Add((c.Id, c.Path));
        }
        cancellationToken.ThrowIfCancellationRequested();

        var groups = new List<DuplicateGroup>();
        int fullContentHashCalls = 0;

        // Bounded: a task per size group flooded the thread pool with
        // blocking file reads, starving every other background job.
        var bySizeGroups = bySize.Where(kv => kv.Value.Count > 1).ToList();
        int total = bySizeGroups.Count, done = 0;
        progress?.Invoke(DuplicateScanPhase.Hashing, 0, total);
        var partials = new DuplicateScanResult[bySizeGroups.Count];
        try
        {
            await Task.Run(() => Parallel.For(
                0, bySizeGroups.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount,
                    CancellationToken = cancellationToken,
                },
                i =>
                {
                    partials[i] = HashAndGroup(bySizeGroups[i].Value, bySizeGroups[i].Key, cancellationToken);
                    progress?.Invoke(DuplicateScanPhase.Hashing, Interlocked.Increment(ref done), total);
                }), cancellationToken);
        }
        catch (AggregateException e) when (cancellationToken.IsCancellationRequested
                                           && e.Flatten().InnerExceptions.All(x => x is OperationCanceledException))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        foreach (var partial in partials)
        {
            groups.AddRange(partial.Groups);
            fullContentHashCalls += partial.FullContentHashCalls;
        }
        progress?.Invoke(DuplicateScanPhase.Complete, total, total);
        return new DuplicateScanResult(groups, fullContentHashCalls);
    }

    private static DuplicateScanResult HashAndGroup(List<(int Id, string Path)> files, long size,
        CancellationToken cancellationToken)
    {
        var byPartial = new Dictionary<string, List<(int Id, string Path)>>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PartialHash(file.Path, 65_536) is { } partial)
            {
                if (!byPartial.TryGetValue(partial, out var list))
                    byPartial[partial] = list = [];
                list.Add(file);
            }
        }

        var groups = new List<DuplicateGroup>();
        int fullContentHashCalls = 0;
        foreach (var (_, collision) in byPartial)
        {
            if (collision.Count < 2) continue;
            var (clusters, needsFullHash) = PartitionClones(collision);
            // Only one clone family and nothing else: identical by their
            // extent maps, no hashing needed.
            if (needsFullHash.Count == 0 && clusters.Count == 1)
            {
                groups.Add(new DuplicateGroup(
                    Hash: "shared-extents",
                    FileIDs: clusters[0].Select(c => c.Id).OrderBy(x => x).ToList(),
                    SizeEach: size,
                    SharesStorage: true));
                continue;
            }
            // Otherwise hash one member per clone family (they share blocks,
            // so contents) and every other file, and group by contents: a
            // family and a plain copy of the same file are one group.
            var byFull = new Dictionary<string, (List<int> Ids, List<List<int>> Sets)>();
            var units = clusters.Concat(needsFullHash.Select(f => new List<(int Id, string Path)> { f }));
            foreach (var unit in units)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fullContentHashCalls++;
                if (FullHash(unit[0].Path, cancellationToken) is not { } full) continue;
                if (!byFull.TryGetValue(full, out var entry))
                    byFull[full] = entry = ([], []);
                entry.Ids.AddRange(unit.Select(u => u.Id));
                if (unit.Count > 1) entry.Sets.Add(unit.Select(u => u.Id).ToList());
            }
            foreach (var (hash, entry) in byFull)
            {
                if (entry.Ids.Count > 1)
                {
                    groups.Add(new DuplicateGroup(hash, entry.Ids.OrderBy(x => x).ToList(), size, entry.Sets));
                }
            }
        }
        return new DuplicateScanResult(groups, fullContentHashCalls);
    }

    /// <summary>
    /// Full extent-map matches become clone groups and never reach
    /// FullHash. Everyone else, including a clone that has been written
    /// since, is hashed.
    /// </summary>
    private static (List<List<(int Id, string Path)>> Clusters, List<(int Id, string Path)> NeedsFullHash)
        PartitionClones(List<(int Id, string Path)> files)
    {
        var remaining = new List<(int Id, string Path)>(files);
        var clusters = new List<List<(int, string)>>();
        var needsFullHash = new List<(int, string)>();
        while (remaining.Count > 0)
        {
            var seed = remaining[0];
            remaining.RemoveAt(0);
            var cluster = new List<(int, string)> { seed };
            var rest = new List<(int, string)>();
            foreach (var other in remaining)
            {
                if (CloneDetector.AreLikelyClones(seed.Path, other.Path))
                    cluster.Add(other);
                else
                    rest.Add(other);
            }
            remaining = rest;
            if (cluster.Count > 1) clusters.Add(cluster);
            else needsFullHash.Add(seed);
        }
        return (clusters.Select(c => c.ToList()).ToList(), needsFullHash);
    }

    private static string? PartialHash(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[bytes];
            int read = stream.Read(buffer, 0, bytes);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch { return null; }
    }

    /// <summary>Streaming SHA256, a megabyte at a time, checking for cancellation between reads.</summary>
    private static string? FullHash(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
