using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMap.Core;

/// <summary>
/// Regenerable directories found in an existing scan. The pattern list is
/// data (quick-wins-patterns.json, embedded resource) so it can grow
/// without an app release. Hits are for review — nothing here deletes.
///
/// Patterns can optionally carry a display category ("Development
/// dependencies", "System caches", …) so the UI can group hits, like the
/// macOS app's categories row. Unmatched patterns fall back to
/// "Regenerable folders".
/// </summary>
public static class QuickWins
{
    public sealed record Patterns(
        List<string> DirectoryNames,
        List<string> PathSuffixes,
        Dictionary<string, string>? Categories = null,
        List<CategoryNote>? CategoryNotes = null);

    /// <summary>A category's order and its one-line note (Mac: Category.title / note), as data.</summary>
    public sealed record CategoryNote(string Title, string Note);

    public sealed record Hit(int Id, string Name, string Category);

    public const string DefaultCategory = "Regenerable folders";

    /// <summary>
    /// The categories that have hits, in the file's order (Developer › By tool's sections),
    /// each with its note; a category the notes don't list follows, alphabetically, with none.
    /// </summary>
    public static List<CategoryNote> Sections(Patterns patterns, IEnumerable<Hit> hits)
    {
        var present = hits.Select(h => h.Category).ToHashSet(StringComparer.Ordinal);
        var output = new List<CategoryNote>();
        foreach (var note in patterns.CategoryNotes ?? [])
        {
            if (present.Remove(note.Title)) output.Add(note);
        }
        output.AddRange(present.OrderBy(t => t, StringComparer.Ordinal).Select(t => new CategoryNote(t, "")));
        return output;
    }

    public static Patterns BundledPatterns()
    {
        var assembly = typeof(QuickWins).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("quick-wins-patterns.json", StringComparison.OrdinalIgnoreCase));
        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is not null)
            {
                var decoded = JsonSerializer.Deserialize<Patterns>(stream, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                });
                if (decoded is not null) return decoded;
            }
        }
        return new Patterns(
            DirectoryNames: ["node_modules", ".venv", "venv", "target", ".next", "dist", "build", "__pycache__", ".gradle"],
            PathSuffixes: [@"AppData\Local\Temp", @"AppData\Local\npm-cache", @"AppData\Local\pip\cache"]);
    }

    /// <summary>
    /// Directories whose name or path matches patterns. A match swallows
    /// its descendants so `dist` inside `node_modules` is not counted twice.
    /// </summary>
    public static List<Hit> Find(FileTree tree, string root, Patterns patterns)
    {
        var hits = new List<Hit>();
        if (tree.Count == 0) return hits;
        var names = new HashSet<string>(patterns.DirectoryNames, StringComparer.OrdinalIgnoreCase);
        var suffixTails = new HashSet<string>(
            patterns.PathSuffixes.Select(SuffixTail), StringComparer.OrdinalIgnoreCase);
        var locator = DeveloperCatalog.Locator(tree);
        var gitignores = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            // Never look inside the Recycle Bin or restore points.
            if (tree.IsDirectory[id] && tree.Parent[id] == 0
                && StorageClassifier.IsSystemHolding("\\" + tree.NameOf(id)))
                continue;
            // A bare name is not proof (a Flutter SDK's bin, a tool's dist):
            // name hits must pass the developer rules' evidence. Path-suffix
            // hits (Temp, npm-cache) are exact locations.
            if (id != 0 && tree.IsDirectory[id] && Match(id, names, suffixTails, patterns, root, tree) is { } matched
                && (!names.Contains(matched) || DeveloperCatalog.IsVerifiedOutput(tree, id, root, locator, gitignores)))
            {
                hits.Add(new Hit(id, tree.NameOf(id), CategoryOf(matched, patterns)));
                continue; // swallow descendants
            }
            int child = tree.FirstChild[id];
            while (child != -1)
            {
                stack.Push(child);
                child = tree.NextSibling[child];
            }
        }
        return hits;
    }

    private static string CategoryOf(string matchedPattern, Patterns patterns)
    {
        if (patterns.Categories is { } categories
            && categories.TryGetValue(matchedPattern, out string? category)
            && category.Length > 0)
        {
            return category;
        }
        return DefaultCategory;
    }

    /// <summary>Returns the pattern that matched, or null.</summary>
    private static string? Match(
        int id,
        HashSet<string> names,
        HashSet<string> suffixTails,
        Patterns patterns,
        string root,
        FileTree tree)
    {
        string name = tree.NameOf(id);
        if (names.Contains(name))
        {
            return patterns.DirectoryNames.First(n =>
                n.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        if (!suffixTails.Contains(name)) return null;
        string path = tree.PathOf(id, root);
        foreach (var suffix in patterns.PathSuffixes)
        {
            string expanded = ExpandPath(suffix);
            if (path.Equals(expanded, StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("\\" + expanded, StringComparison.OrdinalIgnoreCase))
                return suffix;
            // A suffix relative to the scan root (e.g. "AppData\Local\Temp").
            string relative = expanded;
            if (relative.StartsWith(@"~\") || relative.StartsWith("~/"))
                relative = relative[2..];
            if (path.EndsWith("\\" + relative, StringComparison.OrdinalIgnoreCase))
                return suffix;
        }
        return null;
    }

    private static string ExpandPath(string path)
    {
        // Support both "~/X" (user profile) and "%VAR%" environment syntax.
        return UserFolders.Expand(path);
    }

    private static string SuffixTail(string suffix)
    {
        string expanded = ExpandPath(suffix).TrimEnd('\\', '/');
        int sep = expanded.LastIndexOfAny(['\\', '/']);
        return sep < 0 ? expanded : expanded[(sep + 1)..];
    }
}
