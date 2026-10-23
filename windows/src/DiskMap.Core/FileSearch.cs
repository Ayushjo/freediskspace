namespace DiskMap.Core;

/// <summary>
/// Find-as-you-type name search — the Windows counterpart of the macOS
/// FileSearchIndex. Substring matching runs once per interned name (a few
/// hundred thousand strings, not millions of nodes), then one tree walk
/// collects node ids carrying a matching name, ranked by size through a
/// bounded heap — same approach as <see cref="TopSizes"/>.
/// </summary>
public static class FileSearch
{
    /// <summary>
    /// Nodes whose name contains <paramref name="query"/>, largest first.
    /// <paramref name="foldersOnly"/> restricts to directories,
    /// <paramref name="filesOnly"/> to files.
    /// </summary>
    public static List<int> Search(
        FileTree tree, long[] totals, string query, int limit = 200,
        bool filesOnly = false, bool foldersOnly = false) =>
        SearchCounted(tree, totals, query, limit, filesOnly, foldersOnly).Ids;

    /// <summary>
    /// <see cref="Search"/> plus how many nodes matched in all (Mac:
    /// FileSearchIndex.Result.totalMatches), so Find can say "the first 500 of 2,140".
    /// </summary>
    public static (List<int> Ids, int TotalMatches) SearchCounted(
        FileTree tree, long[] totals, string query, int limit = 200,
        bool filesOnly = false, bool foldersOnly = false)
    {
        if (tree.Count == 0 || query.Trim().Length == 0 || totals.Length != tree.Count)
            return ([], 0);

        string needle = query.Trim();
        var matchingNameIds = new HashSet<int>();
        for (int nameId = 0; nameId < tree.NameTable.Count; nameId++)
        {
            if (tree.NameTable[nameId].Contains(needle, StringComparison.OrdinalIgnoreCase))
                matchingNameIds.Add(nameId);
        }
        if (matchingNameIds.Count == 0) return ([], 0);

        int total = 0;
        var ids = TopSizes.Largest(1, tree.Count, limit, id => totals[id], id =>
        {
            if (!matchingNameIds.Contains(tree.NameIndex[id])) return false;
            if (filesOnly && tree.IsDirectory[id]) return false;
            if (foldersOnly && !tree.IsDirectory[id]) return false;
            if (totals[id] <= 0) return false;
            total++;
            return true;
        });
        return (ids, total);
    }
}
