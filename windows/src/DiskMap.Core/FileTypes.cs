using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DiskMap.Core;

/// <summary>
/// File-kind categories — the data behind "kind" badges, the "What's
/// inside?" breakdown, and type-colored tiles. The category list is data
/// (file-type-categories.json, embedded resource) matching the macOS
/// build's file-type-categories.json, extended with the disk-image and
/// application kinds the Windows file set produces (.vhdx, .exe…).
/// </summary>
public static class FileTypes
{
    public sealed record Category(
        string Id,
        string Label,
        string Color,
        string BadgeBackground,
        string BadgeForeground,
        string[] Extensions);

    public const string FolderId = "folder";
    public const string OtherId = "other";

    private static readonly Lazy<(List<Category> categories, Dictionary<string, Category> byExtension)> Data =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<Category> Categories => Ensure().categories;

    private static (List<Category> categories, Dictionary<string, Category> byExtension) Ensure() => Data.Value;

    private static (List<Category> categories, Dictionary<string, Category> byExtension) Load()
    {
        var categories = new List<Category>();
        var assembly = typeof(FileTypes).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("file-type-categories.json", StringComparison.OrdinalIgnoreCase));
        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("categories", out var list))
                {
                    foreach (var c in list.EnumerateArray())
                    {
                        categories.Add(new Category(
                            Id: c.GetProperty("id").GetString() ?? OtherId,
                            Label: c.GetProperty("label").GetString() ?? "Other",
                            Color: c.TryGetProperty("color", out var col) ? col.GetString() ?? "#9AA3AF" : "#9AA3AF",
                            BadgeBackground: c.TryGetProperty("badgeBackground", out var bb) ? bb.GetString() ?? "#F1F5F9" : "#F1F5F9",
                            BadgeForeground: c.TryGetProperty("badgeForeground", out var bf) ? bf.GetString() ?? "#475569" : "#475569",
                            Extensions: c.GetProperty("extensions").EnumerateArray()
                                .Select(e => e.GetString() ?? "").Where(e => e.Length > 0).ToArray()));
                    }
                }
            }
        }
        var byExtension = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in categories)
            foreach (var ext in cat.Extensions)
                byExtension.TryAdd(ext, cat);
        return (categories, byExtension);
    }

    /// <summary>The category id for a file name's extension; "other" when unknown.</summary>
    public static string KindOfFile(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return OtherId;
        // Span lookup: the whole-tree passes below would otherwise allocate a string per file.
        var byExtension = Ensure().byExtension.GetAlternateLookup<ReadOnlySpan<char>>();
        return byExtension.TryGetValue(name.AsSpan(dot + 1), out var cat) ? cat.Id : OtherId;
    }

    public static string KindOf(FileTree tree, int id) =>
        tree.IsDirectory[id] ? FolderId : KindOfFile(tree.NameOf(id));

    public static string LabelOf(string kindId)
    {
        if (kindId == FolderId) return "Folder";
        if (kindId == OtherId) return "Other";
        var (categories, _) = Ensure();
        return categories.FirstOrDefault(c => c.Id == kindId)?.Label ?? "Other";
    }

    public static string TileColorOf(string kindId)
    {
        var (categories, _) = Ensure();
        return categories.FirstOrDefault(c => c.Id == kindId)?.Color ?? "#A9B0BC";
    }

    /// <summary>The saturated badge accent for a kind — bars, dots, icon fills.</summary>
    public static string BadgeForegroundOf(string kindId)
    {
        var (categories, _) = Ensure();
        return categories.FirstOrDefault(c => c.Id == kindId)?.BadgeForeground ?? "#6B7280";
    }

    /// <summary>
    /// Bytes per category over the files inside <paramref name="nodeId"/>
    /// (its whole subtree for a directory). Returns id → bytes, plus a
    /// bucket named <see cref="OtherId"/> for everything unclassified.
    /// </summary>
    public static Dictionary<string, long> TypeBreakdown(FileTree tree, long[] totals, int nodeId)
    {
        var result = new Dictionary<string, long>();
        if (tree.Count == 0 || totals.Length != tree.Count) return result;
        var stack = new Stack<int>();
        stack.Push(nodeId);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (tree.IsDirectory[id])
            {
                int child = tree.FirstChild[id];
                while (child != -1) { stack.Push(child); child = tree.NextSibling[child]; }
            }
            else
            {
                string kind = KindOfFile(tree.NameOf(id));
                result[kind] = result.GetValueOrDefault(kind) + Math.Max(0, totals[id]);
            }
        }
        return result;
    }

    /// <summary>
    /// The category holding the most bytes inside a node — what a
    /// type-colored tile shows. Directories with no file bytes return
    /// <see cref="OtherId"/>; a tie goes to the category listed first.
    /// Charts ask this for every slice on every paint, so the answer for
    /// every folder comes from one pass over the tree, made the first time
    /// it is asked for this totals array.
    /// </summary>
    public static string DominantKind(FileTree tree, long[] totals, int nodeId)
    {
        if (!tree.IsDirectory[nodeId]) return KindOfFile(tree.NameOf(nodeId));
        if (tree.Count == 0 || totals.Length != tree.Count) return OtherId;
        var cache = DominantKindsOf(tree, totals);
        return nodeId < cache.Kinds.Length ? cache.Ids[cache.Kinds[nodeId]] : OtherId;
    }

    /// <summary>Each node's dominant kind as an index into <see cref="Ids"/>.</summary>
    private sealed record DominantKinds(FileTree Tree, string[] Ids, byte[] Kinds);

    /// <summary>
    /// Keyed by the totals array — a new one comes with every scan and size basis — and weak,
    /// so the cache never keeps a finished scan's tree alive.
    /// </summary>
    private static readonly ConditionalWeakTable<long[], DominantKinds> DominantByTotals = new();

    private static DominantKinds DominantKindsOf(FileTree tree, long[] totals)
    {
        if (DominantByTotals.TryGetValue(totals, out var cached) && Fits(cached)) return cached;
        lock (DominantByTotals)
        {
            if (DominantByTotals.TryGetValue(totals, out cached) && Fits(cached)) return cached;
            var built = BuildDominantKinds(tree, totals);
            DominantByTotals.AddOrUpdate(totals, built);
            return built;
        }

        bool Fits(DominantKinds c) => ReferenceEquals(c.Tree, tree) && c.Kinds.Length == tree.Count;
    }

    /// <summary>
    /// One depth-first pass: running byte totals per kind, snapshotted as each folder is
    /// entered; when it is left, what the totals gained is that folder's breakdown.
    /// </summary>
    private static DominantKinds BuildDominantKinds(FileTree tree, long[] totals)
    {
        var categories = Ensure().categories;
        // The category list is fixed data with a handful of entries, so an index fits a byte.
        string[] ids = [.. categories.Select(c => c.Id).Append(OtherId).Distinct()];
        var indexOf = new Dictionary<string, byte>(ids.Length);
        for (int i = 0; i < ids.Length; i++) indexOf[ids[i]] = (byte)i;
        byte other = indexOf[OtherId];
        int k = ids.Length;

        var kinds = new byte[tree.Count];
        Array.Fill(kinds, other);
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
                    byte best = other;
                    long bestBytes = 0;
                    for (int i = 0; i < k; i++)
                    {
                        long bytes = running[i] - entered[at + i];
                        if (bytes > bestBytes) { bestBytes = bytes; best = (byte)i; }
                    }
                    entered.RemoveRange(at, k);
                    kinds[~id] = best;
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
                    byte kind = indexOf.GetValueOrDefault(KindOfFile(tree.NameOf(id)), other);
                    kinds[id] = kind;
                    running[kind] += Math.Max(0, totals[id]);
                }
            }
        }
        return new DominantKinds(tree, ids, kinds);
    }
}
