using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// FileTypes.DominantKind answers every folder from one cached pass per totals array (the charts
/// ask for every slice on every paint); the answers must be what TypeBreakdown says.
/// </summary>
public class FileTypesCacheTests
{
    private static readonly string[] Names =
        ["clip.mp4", "song.mp3", "photo.jpg", "report.pdf", "main.cs", "backup.zip", "disk.vhdx", "setup.exe", "notes", "data.xyz"];

    /// <summary>A deterministic tree: nested folders, every kind, zero-byte files and empty folders.</summary>
    private static (FileTree Tree, long[] Logical, long[] Allocated) RandomTree(int seed, int nodes)
    {
        var random = new Random(seed);
        var tree = new FileTree();
        var folders = new List<int> { tree.AddNode("root", -1, true, 0, 0, 0) };
        for (int i = 0; i < nodes; i++)
        {
            int parent = folders[random.Next(folders.Count)];
            if (random.Next(4) == 0) folders.Add(tree.AddNode($"dir{i}", parent, true, 0, 0, 0));
            else
            {
                long size = random.Next(5) == 0 ? 0 : random.Next(1, 1_000_000);
                long onDisk = random.Next(3) == 0 ? 0 : size + random.Next(0, 8192);
                tree.AddNode($"{i}-{Names[random.Next(Names.Length)]}", parent, false, size, onDisk, 0);
            }
        }
        return (tree, tree.RollUpSizes(SizeBasis.Logical), tree.RollUpSizes(SizeBasis.Allocated));
    }

    /// <summary>TypeBreakdown's biggest bucket; a tie goes to the category listed first, nothing at all is "other".</summary>
    private static string Expected(FileTree tree, long[] totals, int id)
    {
        var breakdown = FileTypes.TypeBreakdown(tree, totals, id);
        long max = breakdown.Count == 0 ? 0 : breakdown.Values.Max();
        if (max <= 0) return FileTypes.OtherId;
        return FileTypes.Categories.Select(c => c.Id).Append(FileTypes.OtherId)
            .First(kind => breakdown.GetValueOrDefault(kind) == max);
    }

    [Theory]
    [InlineData(1, 400)]
    [InlineData(7, 3_000)]
    public void EveryFolderMatchesTypeBreakdown(int seed, int nodes)
    {
        var (tree, logical, allocated) = RandomTree(seed, nodes);
        foreach (var totals in new[] { logical, allocated })
            for (int id = 0; id < tree.Count; id++)
                if (tree.IsDirectory[id])
                    Assert.Equal(Expected(tree, totals, id), FileTypes.DominantKind(tree, totals, id));
    }

    [Fact]
    public void EachTotalsArrayHasItsOwnAnswer()
    {
        var tree = new FileTree();
        int root = tree.AddNode("root", -1, true, 0, 0, 0);
        int folder = tree.AddNode("Mixed", root, true, 0, 0, 0);
        tree.AddNode("a.mp4", folder, false, 100, 10, 0);
        tree.AddNode("b.pdf", folder, false, 50, 60, 0);
        var logical = tree.RollUpSizes(SizeBasis.Logical);
        var allocated = tree.RollUpSizes(SizeBasis.Allocated);
        Assert.Equal("video", FileTypes.DominantKind(tree, logical, folder));
        Assert.Equal("document", FileTypes.DominantKind(tree, allocated, folder));
        Assert.Equal("video", FileTypes.DominantKind(tree, logical, root));   // asked again: still its own answer
    }

    [Fact]
    public void FoldersWithoutBytesAreOtherAndFilesKeepTheirKind()
    {
        var tree = new FileTree();
        int root = tree.AddNode("root", -1, true, 0, 0, 0);
        int empty = tree.AddNode("Empty", root, true, 0, 0, 0);
        int zero = tree.AddNode("Zero", root, true, 0, 0, 0);
        int clip = tree.AddNode("clip.mp4", zero, false, 0, 0, 0);
        var totals = tree.RollUpSizes(SizeBasis.Logical);
        Assert.Equal(FileTypes.OtherId, FileTypes.DominantKind(tree, totals, empty));
        Assert.Equal(FileTypes.OtherId, FileTypes.DominantKind(tree, totals, zero));
        Assert.Equal("video", FileTypes.DominantKind(tree, totals, clip));
        Assert.Equal(FileTypes.OtherId, FileTypes.DominantKind(tree, [1, 2], root));   // totals of another tree
    }

    [Fact]
    public void ADeepChainNeedsNoRecursion()
    {
        var tree = new FileTree();
        int parent = tree.AddNode("root", -1, true, 0, 0, 0);
        var chain = new List<int> { parent };
        for (int depth = 0; depth < 20_000; depth++)
        {
            tree.AddNode($"{depth}.pdf", parent, false, 1, 1, 0);
            parent = tree.AddNode($"d{depth}", parent, true, 0, 0, 0);
            chain.Add(parent);
        }
        tree.AddNode("deep.mp4", parent, false, 50_000, 50_000, 0);
        var totals = tree.RollUpSizes(SizeBasis.Logical);
        Assert.Equal("video", FileTypes.DominantKind(tree, totals, chain[0]));        // 50,000 video beats 20,000 bytes of documents
        Assert.Equal("video", FileTypes.DominantKind(tree, totals, chain[^1]));
        Assert.Equal("video", FileTypes.DominantKind(tree, totals, chain[10_000]));
    }

    [Fact]
    public void ConcurrentCallersAgree()
    {
        var (tree, logical, _) = RandomTree(11, 2_000);
        var folders = Enumerable.Range(0, tree.Count).Where(id => tree.IsDirectory[id]).ToArray();
        var expected = folders.Select(id => Expected(tree, logical, id)).ToArray();
        var seen = new string[folders.Length];
        Parallel.For(0, folders.Length, i => seen[i] = FileTypes.DominantKind(tree, logical, folders[i]));
        Assert.Equal(expected, seen);
    }
}
