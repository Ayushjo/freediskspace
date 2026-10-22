using System.Text.Json;

namespace DiskMap.Core;

/// <summary>
/// Storage tools fill by themselves — WSL distributions, Docker's disk, Android emulators and
/// system images, side-by-side SDKs, package caches — listed where it lives (Mac:
/// SystemStorageCatalog.swift with developer-storage.json, the Developer page's Xcode tab).
/// Entries are data (developer-storage.json, AGENTS.md: patterns are data). Only <c>trash</c>
/// entries may be offered for Cleanup; the rest show a command to copy (never run) or the
/// place in the tool where it is removed.
/// <para>
/// The Mac walks each path on disk; here the sizes come from the scan, so an entry outside
/// the scanned folder is left out. Nothing touches the disk.
/// </para>
/// </summary>
public static class SystemStorageCatalog
{
    public enum Action
    {
        /// <summary>The OS or the tool manages it; nothing to do.</summary>
        Never,
        /// <summary>Freed through the tool's own command or settings.</summary>
        Guide,
        /// <summary>Safe to add to Cleanup (goes to the Recycle Bin).</summary>
        Trash,
    }

    public sealed record Entry(
        string Id,
        string Title,
        IReadOnlyList<string> Paths,
        Action Action,
        string Why,
        IReadOnlyList<string> Steps,
        bool Regrows,
        /// <summary>A tool command to copy (never run by the app).</summary>
        string? Command = null,
        /// <summary>List each child of the matched folders as its own row.</summary>
        bool PerChild = false,
        /// <summary>With <see cref="PerChild"/>: suggest keeping the highest version in each folder.</summary>
        bool KeepNewest = false);

    public sealed record Found(string Path, int NodeId, long Bytes);

    public sealed record Item(Entry Entry, IReadOnlyList<Found> FoundPaths, bool IsPartial, IReadOnlySet<string> Keep)
    {
        public string Id => Entry.Id;
        public long Bytes => FoundPaths.Sum(f => f.Bytes);
        /// <summary>What removing every suggested path would free.</summary>
        public long SuggestedBytes => FoundPaths.Where(f => !Keep.Contains(f.Path)).Sum(f => f.Bytes);
    }

    private sealed class RawFile
    {
        public List<Raw>? Entries { get; set; }
    }

    private sealed class Raw
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public List<string> Paths { get; set; } = [];
        public string Action { get; set; } = "";
        public string Why { get; set; } = "";
        public List<string> Steps { get; set; } = [];
        public bool? Regrows { get; set; }
        public string? Command { get; set; }
        public bool? PerChild { get; set; }
        public bool? KeepNewest { get; set; }
    }

    private static readonly Lazy<List<Entry>> Bundled = new(() => LoadEntries());

    /// <summary>The bundled developer-storage.json entries.</summary>
    public static IReadOnlyList<Entry> DeveloperEntries => Bundled.Value;

    /// <summary>
    /// A malformed file yields no entries; an entry with an unknown action or no paths is
    /// dropped rather than guessed. The suite checks the bundled file loads.
    /// </summary>
    public static List<Entry> LoadEntries(byte[]? data = null, string resource = "developer-storage.json")
    {
        RawFile? file;
        try
        {
            if (data is not null)
            {
                file = JsonSerializer.Deserialize<RawFile>(data, Options);
            }
            else
            {
                var assembly = typeof(SystemStorageCatalog).Assembly;
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(resource, StringComparison.OrdinalIgnoreCase));
                if (name is null) return [];
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null) return [];
                file = JsonSerializer.Deserialize<RawFile>(stream, Options);
            }
        }
        catch (JsonException) { return []; }
        if (file?.Entries is not { } entries) return [];
        var output = new List<Entry>();
        foreach (var raw in entries)
        {
            if (raw.Paths.Count == 0 || raw.Id.Length == 0) continue;
            Action? action = raw.Action switch
            {
                "never" => Action.Never,
                "guide" => Action.Guide,
                "trash" => Action.Trash,
                _ => null,
            };
            if (action is not { } known) continue;
            output.Add(new Entry(raw.Id, raw.Title, raw.Paths, known, raw.Why, raw.Steps, raw.Regrows ?? false,
                raw.Command, raw.PerChild ?? false, raw.KeepNewest ?? false));
        }
        return output;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Measures every entry from the scan: <paramref name="expand"/> turns a pattern into an
    /// absolute path (%LOCALAPPDATA%, ~), a trailing "*" in the last component matches that
    /// folder's children by prefix. Entries with nothing in the scan are left out; biggest first.
    /// </summary>
    public static List<Item> MeasureTree(IEnumerable<Entry> entries, FileTree tree, string rootPath, long[] totals,
        Func<string, string>? expand = null)
    {
        expand ??= UserFolders.Expand;
        var items = new List<Item>();
        if (tree.Count == 0 || totals.Length != tree.Count) return items;
        foreach (var entry in entries)
        {
            var found = new List<Found>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in entry.Paths)
            {
                foreach (int folder in Resolve(expand(pattern), tree, rootPath))
                {
                    var nodes = entry.PerChild
                        ? Children(tree, folder).Where(c => tree.IsDirectory[c] && !tree.NameOf(c).StartsWith('.'))
                            .OrderBy(c => tree.NameOf(c), StringComparer.OrdinalIgnoreCase).ToList()
                        : [folder];
                    var measured = new List<string>();
                    foreach (int node in nodes)
                    {
                        string path = tree.PathOf(node, rootPath);
                        if (!seen.Add(path)) continue;
                        found.Add(new Found(path, node, Math.Max(0, totals[node])));
                        measured.Add(path);
                    }
                    if (entry.KeepNewest)
                    {
                        // No readable version: suggest nothing rather than everything.
                        if (NewestVersion(measured) is { } newest) keep.Add(newest);
                        else keep.UnionWith(measured);
                    }
                }
            }
            if (found.Count == 0) continue;
            items.Add(new Item(entry, found, IsPartial: false, keep));
        }
        return items.OrderByDescending(i => i.Bytes).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Tree nodes for an absolute path under the scan root; a trailing "*" matches by prefix.</summary>
    internal static List<int> Resolve(string absolutePath, FileTree tree, string rootPath)
    {
        string root = Normalize(rootPath);
        string path = Normalize(absolutePath);
        string rest;
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) rest = "";
        else if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) rest = path[(root.Length + 1)..];
        else if (root.EndsWith('\\') && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) rest = path[root.Length..];
        else return [];
        var current = new List<int> { 0 };
        if (rest.Length == 0) return current;
        var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            bool prefix = i == parts.Length - 1 && part.EndsWith('*');
            string want = prefix ? part[..^1] : part;
            var next = new List<int>();
            foreach (int node in current)
            {
                foreach (int child in Children(tree, node))
                {
                    string name = tree.NameOf(child);
                    if (prefix ? name.StartsWith(want, StringComparison.OrdinalIgnoreCase)
                               : name.Equals(want, StringComparison.OrdinalIgnoreCase))
                        next.Add(child);
                }
            }
            if (next.Count == 0) return [];
            current = next.OrderBy(n => tree.NameOf(n), StringComparer.OrdinalIgnoreCase).ToList();
        }
        return current;
    }

    private static IEnumerable<int> Children(FileTree tree, int id)
    {
        for (int child = tree.FirstChild[id]; child != -1; child = tree.NextSibling[child])
            yield return child;
    }

    private static string Normalize(string path)
    {
        string p = path.Replace('/', '\\');
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    /// <summary>
    /// The version in a folder name: the first token made only of digits and dots, splitting on
    /// spaces and hyphens ("8.0.401" → 8.0.401, "android-35" → 35, "17.4 (21E219)" → 17.4).
    /// Null when there is none.
    /// </summary>
    public static int[]? Version(string name)
    {
        foreach (var token in name.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('.');
            if (parts.Length == 0 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) continue;
            var numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], out numbers[i])) return null;
            return numbers;
        }
        return null;
    }

    /// <summary>The path whose last component has the highest version; null when none has one.</summary>
    internal static string? NewestVersion(IEnumerable<string> paths)
    {
        string? best = null;
        int[]? bestVersion = null;
        foreach (var path in paths)
        {
            string name = path.TrimEnd('\\', '/');
            int cut = name.LastIndexOfAny(['\\', '/']);
            if (Version(cut >= 0 ? name[(cut + 1)..] : name) is not { } version) continue;
            if (bestVersion is null || Compare(version, bestVersion) > 0)
            {
                best = path;
                bestVersion = version;
            }
        }
        return best;
    }

    private static int Compare(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }
}
