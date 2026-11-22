namespace DiskMap.Core;

/// <summary>
/// Ranked node ids for the Top Sizes list. Index 0 (the scan root) is
/// omitted. Directories already carry subtree totals; files carry their
/// own size.
/// </summary>
public static class TopSizes
{
    public static List<int> Ranked(long[] totals, int limit = 500) =>
        Largest(1, totals.Length, limit, id => totals[id], id => totals[id] > 0);

    /// <summary>
    /// The <paramref name="limit"/> largest ids in [from, to) passing
    /// <paramref name="include"/>, largest first: a bounded min-heap, so a
    /// multi-million-node scan costs O(n log limit) instead of a full sort.
    /// </summary>
    public static List<int> Largest(int from, int to, int limit, Func<int, long> size, Func<int, bool> include)
    {
        var result = new List<int>();
        if (limit <= 0) return result;
        var heap = new PriorityQueue<int, long>(limit + 1);
        for (int id = from; id < to; id++)
        {
            if (!include(id)) continue;
            long s = size(id);
            if (heap.Count < limit) heap.Enqueue(id, s);
            else if (heap.TryPeek(out _, out long smallest) && s > smallest) heap.EnqueueDequeue(id, s);
        }
        while (heap.Count > 0) result.Add(heap.Dequeue());
        result.Reverse();
        return result;
    }
}

public enum AgeBucket
{
    Under30,
    Days30To90,
    Days90To365,
    OneToTwoYears,
    OverTwoYears,
    Unknown,
}

public static class AgeMap
{
    public static string Title(AgeBucket bucket) => bucket switch
    {
        AgeBucket.Under30 => "Under 30 days",
        AgeBucket.Days30To90 => "30–90 days",
        AgeBucket.Days90To365 => "90 days–1 year",
        AgeBucket.OneToTwoYears => "1–2 years",
        AgeBucket.OverTwoYears => "Over 2 years",
        _ => "Unknown",
    };

    public static int Today(DateTimeOffset? date = null) =>
        (int)((date ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 86400);

    /// <summary>modifiedDay is days since epoch. 0 means the scan had no date.</summary>
    public static AgeBucket Bucket(int modifiedDay, int today)
    {
        if (modifiedDay <= 0) return AgeBucket.Unknown;
        int age = today - modifiedDay;
        if (age < 30) return AgeBucket.Under30;
        if (age < 90) return AgeBucket.Days30To90;
        if (age < 365) return AgeBucket.Days90To365;
        if (age < 730) return AgeBucket.OneToTwoYears;
        return AgeBucket.OverTwoYears;
    }

    /// <summary>
    /// File ids older than a year, largest first, capped. Directories
    /// and unknown dates are excluded.
    /// </summary>
    public static List<int> Untouched(FileTree tree, long[] totals, int today, int limit = 100)
    {
        if (tree.Count != totals.Length) return [];
        return TopSizes.Largest(1, tree.Count, limit, id => totals[id], id =>
        {
            if (tree.IsDirectory[id]) return false;
            int day = tree.ModifiedDay[id];
            return day > 0 && today - day > 365 && totals[id] > 0;
        });
    }

    /// <summary>Newest file date anywhere under <paramref name="id"/> (itself, for a file). 0 = no dates.</summary>
    public static int NewestDay(FileTree tree, int id)
    {
        if (id < 0 || id >= tree.Count) return 0;
        if (!tree.IsDirectory[id]) return tree.ModifiedDay[id];
        int newest = 0;
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            for (int child = tree.FirstChild[stack.Pop()]; child != -1; child = tree.NextSibling[child])
            {
                if (tree.IsDirectory[child]) stack.Push(child);
                else newest = Math.Max(newest, tree.ModifiedDay[child]);
            }
        }
        return newest;
    }

    public static Dictionary<AgeBucket, long> BucketSizes(FileTree tree, long[] totals, int today)
    {
        var sizes = new Dictionary<AgeBucket, long>();
        if (tree.Count != totals.Length) return sizes;
        for (int id = 1; id < tree.Count; id++)
        {
            if (tree.IsDirectory[id]) continue;
            var bucket = Bucket(tree.ModifiedDay[id], today);
            sizes[bucket] = sizes.GetValueOrDefault(bucket) + totals[id];
        }
        return sizes;
    }

    /// <summary>
    /// The bucket holding the most file bytes inside a node — what an age-coloured chart slice
    /// shows; a file is its own bucket. Null for a folder whose files hold no bytes; a tie goes
    /// to the newer bucket. Charts ask this for every slice on every paint, so the answer for
    /// every folder comes from one pass over the tree, made the first time it is asked for this
    /// totals array and day.
    /// </summary>
    public static AgeBucket? DominantBucket(FileTree tree, long[] totals, int nodeId, int today)
    {
        if (nodeId < 0 || nodeId >= tree.Count) return null;
        if (!tree.IsDirectory[nodeId]) return Bucket(tree.ModifiedDay[nodeId], today);
        if (totals.Length != tree.Count) return null;
        byte bucket = DominantBucketsOf(tree, totals, today).Buckets[nodeId];
        return bucket == NoBytes ? null : (AgeBucket)bucket;
    }

    private const byte NoBytes = byte.MaxValue;

    /// <summary>Each node's dominant bucket, or <see cref="NoBytes"/>, for one tree and day.</summary>
    private sealed record DominantBuckets(FileTree Tree, int Today, byte[] Buckets);

    /// <summary>
    /// Keyed by the totals array — a new one comes with every scan and size basis — and weak,
    /// so the cache never keeps a finished scan's tree alive. A new day builds it again.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<long[], DominantBuckets> DominantByTotals = new();

    private static DominantBuckets DominantBucketsOf(FileTree tree, long[] totals, int today)
    {
        if (DominantByTotals.TryGetValue(totals, out var cached) && Fits(cached)) return cached;
        lock (DominantByTotals)
        {
            if (DominantByTotals.TryGetValue(totals, out cached) && Fits(cached)) return cached;
            var built = new DominantBuckets(tree, today, BuildDominantBuckets(tree, totals, today));
            DominantByTotals.AddOrUpdate(totals, built);
            return built;
        }

        bool Fits(DominantBuckets c) => ReferenceEquals(c.Tree, tree) && c.Today == today && c.Buckets.Length == tree.Count;
    }

    /// <summary>
    /// One depth-first pass: running byte totals per bucket, snapshotted as each folder is
    /// entered; when it is left, what the totals gained is that folder's breakdown.
    /// </summary>
    private static byte[] BuildDominantBuckets(FileTree tree, long[] totals, int today)
    {
        const int k = (int)AgeBucket.Unknown + 1;
        var buckets = new byte[tree.Count];
        var running = new long[k];
        var entered = new List<long>();   // k running totals per open folder
        var stack = new Stack<int>();
        for (int root = 0; root < tree.Count; root++)
        {
            if (tree.Parent[root] >= 0) continue;
            stack.Push(root);
            while (stack.TryPop(out int id))
            {
                if (id < 0)
                {
                    int at = entered.Count - k;
                    byte best = NoBytes;
                    long bestBytes = 0;
                    for (int i = 0; i < k; i++)
                    {
                        long bytes = running[i] - entered[at + i];
                        if (bytes > bestBytes) { bestBytes = bytes; best = (byte)i; }
                    }
                    entered.RemoveRange(at, k);
                    buckets[~id] = best;
                }
                else if (tree.IsDirectory[id])
                {
                    entered.AddRange(running);
                    stack.Push(~id);   // leave it after its children
                    for (int child = tree.FirstChild[id]; child != -1; child = tree.NextSibling[child])
                        stack.Push(child);
                }
                else
                {
                    var bucket = Bucket(tree.ModifiedDay[id], today);
                    buckets[id] = (byte)bucket;
                    running[(int)bucket] += Math.Max(0, totals[id]);
                }
            }
        }
        return buckets;
    }
}
