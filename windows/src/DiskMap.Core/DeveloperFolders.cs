namespace DiskMap.Core;

/// <summary>
/// Developer storage by folder: every folder that holds some, how much, and how much of it
/// can go — for Developer's "Where it lives" (Mac: DeveloperCatalog.swift DeveloperFolders,
/// MAC-FIXES-FROM-WINDOWS §4.3). Keyed by tree node.
/// </summary>
public sealed class DeveloperFolders
{
    public sealed class Folder
    {
        public int Id { get; init; }
        /// <summary>Developer bytes at or under this folder.</summary>
        public long Bytes { get; set; }
        public int ItemCount { get; set; }
        /// <summary>Bytes of the items here that can go to Cleanup.</summary>
        public long RemovableBytes { get; set; }
        /// <summary>The developer item this folder <em>is</em>, when it is one.</summary>
        public string? ItemId { get; set; }
        /// <summary>Folders below that hold developer storage, largest first.</summary>
        public List<int> Children { get; set; } = [];
        /// <summary>Ids of every item at or under this folder.</summary>
        public List<string> ItemIds { get; } = [];
        public List<string> RemovableItemIds { get; } = [];
    }

    public IReadOnlyDictionary<int, Folder> Folders { get; }

    public DeveloperFolders(Dictionary<int, Folder> folders) => Folders = folders;

    public static readonly DeveloperFolders Empty = new([]);

    /// <summary>
    /// Where to open: from the scan root, step into the largest folder while it holds at least
    /// 80% of its parent's developer storage, so the first view is a real choice rather than
    /// C:\ → Users → you. Never steps into a folder that is itself an item.
    /// </summary>
    public int AutoStart(int root = 0)
    {
        int current = root;
        int steps = 0;
        while (Folders.TryGetValue(current, out var folder) && folder.Children.Count > 0
               && Folders.TryGetValue(folder.Children[0], out var child)
               && child.ItemId is null && child.Bytes >= folder.Bytes * 0.8 && steps < 64)
        {
            current = folder.Children[0];
            steps++;
        }
        return current;
    }
}

public static partial class DeveloperCatalog
{
    /// <summary>
    /// Each item's bytes credited to every folder from it up to the scan root. Items never
    /// nest (outer hits win in <see cref="DeveloperCatalog.Build"/>), so nothing counts twice.
    /// <paramref name="removable"/> decides what "Clean" may add.
    /// </summary>
    public static DeveloperFolders FolderRollup(DeveloperCatalogResult result, FileTree tree,
        Func<DeveloperItem, bool> removable)
    {
        var folders = new Dictionary<int, DeveloperFolders.Folder>();
        var childSets = new Dictionary<int, HashSet<int>>();
        foreach (var item in result.Items)
        {
            bool canGo = removable(item);
            int node = item.NodeID;
            int? child = null;
            int steps = 0;
            while (node >= 0 && node < tree.Count && steps < 4096)
            {
                if (!folders.TryGetValue(node, out var folder))
                {
                    folder = new DeveloperFolders.Folder { Id = node };
                    folders[node] = folder;
                }
                folder.Bytes += item.Bytes;
                folder.ItemCount += 1;
                folder.ItemIds.Add(item.Id);
                if (canGo)
                {
                    folder.RemovableBytes += item.Bytes;
                    folder.RemovableItemIds.Add(item.Id);
                }
                if (node == item.NodeID) folder.ItemId = item.Id;
                if (child is { } below)
                {
                    if (!childSets.TryGetValue(node, out var set)) childSets[node] = set = [];
                    set.Add(below);
                }
                child = node;
                int up = tree.Parent[node];
                if (up == node) break;
                node = up;
                steps++;
            }
        }
        foreach (var (id, children) in childSets)
        {
            folders[id].Children = children
                .OrderByDescending(c => folders.TryGetValue(c, out var f) ? f.Bytes : 0)
                .ThenBy(c => c)
                .ToList();
        }
        return new DeveloperFolders(folders);
    }
}
