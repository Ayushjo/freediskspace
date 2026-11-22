using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// AgeMap.DominantBucket answers every folder from one cached pass per totals array and day (the
/// charts' age colouring asks for every slice on every paint); the answers must match a walk.
/// </summary>
public class AgeMapCacheTests
{
    private const int Today = 20_000;

    /// <summary>The biggest bucket by bytes under a node, walked directly; a tie goes to the newer bucket, no bytes is null.</summary>
    private static AgeBucket? Walked(FileTree tree, long[] totals, int node, int today)
    {
        if (!tree.IsDirectory[node]) return AgeMap.Bucket(tree.ModifiedDay[node], today);
        var bytes = new Dictionary<AgeBucket, long>();
        var stack = new Stack<int>([node]);
        while (stack.TryPop(out int id))
        {
            if (!tree.IsDirectory[id])
            {
                var bucket = AgeMap.Bucket(tree.ModifiedDay[id], today);
                bytes[bucket] = bytes.GetValueOrDefault(bucket) + totals[id];
            }
            for (int child = tree.FirstChild[id]; child != -1; child = tree.NextSibling[child]) stack.Push(child);
        }
        long max = bytes.Count == 0 ? 0 : bytes.Values.Max();
        return max <= 0 ? null : Enum.GetValues<AgeBucket>().First(b => bytes.GetValueOrDefault(b) == max);
    }

    [Fact]
    public void EveryNodeMatchesADirectWalk()
    {
        var tree = new FileTree();
        int root = tree.AddNode("root", -1, true, 0, 0, 0);
        int photos = tree.AddNode("Photos", root, true, 0, 0, 0);
        tree.AddNode("new.jpg", photos, false, 300, 300, Today - 5);
        tree.AddNode("old.jpg", photos, false, 900, 100, Today - 800);
        int nested = tree.AddNode("2019", photos, true, 0, 0, 0);
        tree.AddNode("a.jpg", nested, false, 50, 700, Today - 400);
        tree.AddNode("undated.bin", nested, false, 40, 40, 0);
        int tie = tree.AddNode("Tie", root, true, 0, 0, 0);
        tree.AddNode("x", tie, false, 10, 10, Today - 100);
        tree.AddNode("y", tie, false, 10, 10, Today - 40);
        int zero = tree.AddNode("Zero", root, true, 0, 0, 0);
        tree.AddNode("empty.txt", zero, false, 0, 0, Today - 1);
        tree.AddNode("Empty", root, true, 0, 0, 0);
        tree.AddNode("loose.log", root, false, 20, 20, Today - 2_000);

        var (logical, allocated) = tree.RollUpBoth();
        foreach (var totals in new[] { logical, allocated })
            for (int id = 0; id < tree.Count; id++)
                Assert.Equal(Walked(tree, totals, id, Today), AgeMap.DominantBucket(tree, totals, id, Today));

        // Each totals array and day has its own answer.
        Assert.Equal(AgeBucket.OverTwoYears, AgeMap.DominantBucket(tree, logical, photos, Today));
        Assert.Equal(AgeBucket.OneToTwoYears, AgeMap.DominantBucket(tree, allocated, photos, Today));
        Assert.Equal(AgeBucket.Days30To90, AgeMap.DominantBucket(tree, logical, tie, Today));   // a tie: the newer one
        Assert.Equal(AgeBucket.Days90To365, AgeMap.DominantBucket(tree, logical, tie, Today + 50));   // both 90+ days old now
        Assert.Null(AgeMap.DominantBucket(tree, logical, zero, Today));
        Assert.Null(AgeMap.DominantBucket(tree, [1, 2], root, Today));   // totals of another tree
    }
}
