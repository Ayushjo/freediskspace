using System.Text.Json;

namespace DiskMap.Core;

/// <summary>
/// Storage Windows and developer tools fill by themselves, listed where it lives (Mac:
/// SystemStorageCatalog.swift). Two data files share this loader (AGENTS.md: patterns are
/// data):
/// <list type="bullet">
/// <item>developer-storage.json — WSL distributions, Docker's disk, Android emulators and
/// system images, side-by-side SDKs, package caches (the Developer page's WSL &amp; SDKs tab;
/// Mac: developer-storage.json, the Xcode tab). Sized from the scan only
/// (<see cref="MeasureTree"/>): an entry outside the scanned folder is left out.</item>
/// <item>system-storage-windows.json — Windows.old, update downloads, Recall, WinSxS, the
/// Installer cache ("What Windows keeps" in Where's the rest?; Mac: system-storage.json,
/// "What macOS keeps"). Sized from the scan when it covers the path, else by a bounded,
/// read-only walk of the path itself (<see cref="Measure"/>), like the Mac.</item>
/// </list>
/// Only <c>trash</c> entries may be offered for the basket; the rest show a command to copy
/// (never run), the place in the tool, or Windows' own Settings page where it is removed.
/// Research and sources: docs/STORAGE-RESEARCH-PLAN.md §2 and §3.
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
        bool KeepNewest = false,
        /// <summary>A Windows Settings page (ms-settings: only), opened by the shell. Mac: settingsPane.</summary>
        string? SettingsUri = null,
        /// <summary>False: shown but never sized (WinSxS, where any folder walk over-counts).</summary>
        bool Measured = true);

    /// <summary>One path found for an entry: its scan node (-1 when measured off the scan) and its bytes on disk.</summary>
    public sealed record Found(string Path, int NodeId, long Bytes);

    /// <summary>
    /// An entry with what was found on this PC. <see cref="IsPartial"/>: some folder inside couldn't be
    /// read, so <see cref="Bytes"/> is a lower bound, never shown as complete. <see cref="SizeUnknown"/>:
    /// nothing could be measured (the entry isn't sized, or the walk ran out of time).
    /// </summary>
    public sealed record Item(Entry Entry, IReadOnlyList<Found> FoundPaths, bool IsPartial, IReadOnlySet<string> Keep,
        bool SizeUnknown = false)
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
        public string? SettingsUri { get; set; }
        public bool? Measure { get; set; }
    }

    private static readonly Lazy<List<Entry>> Bundled = new(() => LoadEntries());
    private static readonly Lazy<List<Entry>> BundledSystem = new(() => LoadEntries(resource: SystemResource));

    /// <summary>The bundled developer-storage.json entries.</summary>
    public static IReadOnlyList<Entry> DeveloperEntries => Bundled.Value;

    public const string SystemResource = "system-storage-windows.json";

    /// <summary>The bundled system-storage-windows.json entries ("What Windows keeps").</summary>
    public static IReadOnlyList<Entry> SystemEntries => BundledSystem.Value;

    /// <summary>
    /// Only a Windows Settings page may be opened from the data file: anything else (a web
    /// address, a program) is dropped rather than handed to the shell.
    /// </summary>
    public static bool IsSettingsUri(string? uri) =>
        uri is { Length: > 12 } && uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
        && uri.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_' or '.');

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
                raw.Command, raw.PerChild ?? false, raw.KeepNewest ?? false,
                IsSettingsUri(raw.SettingsUri) ? raw.SettingsUri : null, raw.Measure ?? true));
        }
        return output;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Measures every entry from the scan only: <paramref name="expand"/> turns a pattern into an
    /// absolute path (%LOCALAPPDATA%, ~), a trailing "*" in the last component matches that
    /// folder's children by prefix. Entries with nothing in the scan are left out; biggest first.
    /// Nothing touches the disk.
    /// </summary>
    public static List<Item> MeasureTree(IEnumerable<Entry> entries, FileTree tree, string rootPath, long[] totals,
        Func<string, string>? expand = null)
    {
        if (tree.Count == 0 || totals.Length != tree.Count) return [];
        return Measure(entries, tree, rootPath, totals, expand: expand);
    }

    /// <summary>Size on disk of one path, whether all of it could be read, and whether it could be sized at all.</summary>
    public readonly record struct Measurement(long Bytes, bool Complete, bool Measured = true);

    /// <summary>Sizes one path that the scan doesn't hold. Null when the path doesn't exist.</summary>
    public delegate Measurement? Sizer(string path);

    /// <summary>
    /// Measures every entry: from the scan where it covers the path (allocated
    /// <paramref name="totals"/>; a denied folder inside marks the item partial), else with
    /// <paramref name="sizer"/> on the path itself (Mac: measure, which always walks). A null
    /// sizer means scan only. Missing paths are skipped and an entry with nothing found is left
    /// out; a sizer that throws counts as an unreadable path, never as an error. An entry with
    /// <see cref="Entry.Measured"/> false is only looked up, never walked. Biggest first.
    /// </summary>
    public static List<Item> Measure(IEnumerable<Entry> entries, FileTree? tree, string? rootPath, long[]? totals,
        IReadOnlyCollection<int>? deniedDirectoryIds = null, Func<string, string>? expand = null, Sizer? sizer = null,
        Func<string, IEnumerable<string>?>? listFolders = null)
    {
        expand ??= UserFolders.Expand;
        listFolders ??= ListFolders;
        bool useTree = tree is { Count: > 0 } && totals is not null && totals.Length == tree.Count && rootPath is not null;
        var deniedInside = useTree && deniedDirectoryIds is { Count: > 0 } ? WithDeniedInside(tree!, deniedDirectoryIds) : null;
        var items = new List<Item>();
        foreach (var entry in entries)
        {
            var found = new List<Found>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool partial = false, anyMeasured = false;
            void KeepNewest(List<string> measured)
            {
                if (!entry.KeepNewest) return;
                // No readable version: suggest nothing rather than everything.
                if (NewestVersion(measured) is { } newest) keep.Add(newest);
                else keep.UnionWith(measured);
            }
            foreach (var pattern in entry.Paths)
            {
                string path = expand(pattern);
                var folders = useTree ? Resolve(path, tree!, rootPath!) : [];
                if (folders.Count > 0)
                {
                    foreach (int folder in folders)
                    {
                        var nodes = entry.PerChild
                            ? Children(tree!, folder).Where(c => tree!.IsDirectory[c] && !tree.NameOf(c).StartsWith('.'))
                                .OrderBy(c => tree!.NameOf(c), StringComparer.OrdinalIgnoreCase).ToList()
                            : [folder];
                        var measured = new List<string>();
                        foreach (int node in nodes)
                        {
                            string nodePath = tree!.PathOf(node, rootPath!);
                            if (!seen.Add(nodePath)) continue;
                            found.Add(new Found(nodePath, node, entry.Measured ? Math.Max(0, totals![node]) : 0));
                            measured.Add(nodePath);
                            if (!entry.Measured) continue;
                            anyMeasured = true;
                            if (deniedInside?.Contains(node) == true) partial = true;
                        }
                        KeepNewest(measured);
                    }
                }
                else if (sizer is not null)
                {
                    foreach (string folder in ExpandLive(path, listFolders))
                    {
                        var paths = entry.PerChild
                            ? (listFolders(folder) ?? []).Where(n => !n.StartsWith('.'))
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => Path.Join(folder, n)).ToList()
                            : [folder];
                        var measured = new List<string>();
                        foreach (string candidate in paths)
                        {
                            if (seen.Contains(candidate)) continue;
                            Measurement? size = entry.Measured ? SafeSize(sizer, candidate)
                                : Exists(candidate) ? new Measurement(0, Complete: true, Measured: false) : null;
                            if (size is not { } m) continue;
                            seen.Add(candidate);
                            found.Add(new Found(candidate, -1, Math.Max(0, m.Bytes)));
                            measured.Add(candidate);
                            if (!m.Measured) continue;
                            anyMeasured = true;
                            if (!m.Complete) partial = true;
                        }
                        KeepNewest(measured);
                    }
                }
            }
            if (found.Count == 0) continue;
            items.Add(new Item(entry, found, partial, keep, SizeUnknown: !anyMeasured));
        }
        return items.OrderByDescending(i => i.Bytes).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    private static Measurement? SafeSize(Sizer sizer, string path)
    {
        try { return sizer(path); }
        catch { return new Measurement(0, Complete: false); }   // unreadable, not an error
    }

    private static bool Exists(string path)
    {
        try { return Path.Exists(path); }
        catch { return false; }
    }

    /// <summary>A pattern's paths on disk: itself, or the parent's children matching a trailing "*".</summary>
    internal static List<string> ExpandLive(string path, Func<string, IEnumerable<string>?> listFolders)
    {
        string trimmed = path.TrimEnd('\\', '/');
        if (!trimmed.EndsWith('*')) return [path];
        int cut = trimmed.LastIndexOfAny(['\\', '/']);
        if (cut <= 0) return [];
        string parent = trimmed[..cut];
        string prefix = trimmed[(cut + 1)..^1];
        return (listFolders(parent) ?? [])
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => parent + trimmed[cut] + n).ToList();
    }

    /// <summary>A folder's subfolder names; null when it can't be listed.</summary>
    private static IEnumerable<string>? ListFolders(string path)
    {
        try { return Directory.EnumerateDirectories(path).Select(p => Path.GetFileName(p)).ToList(); }
        catch { return null; }
    }

    /// <summary>Every node with a denied folder at or below it.</summary>
    private static HashSet<int> WithDeniedInside(FileTree tree, IReadOnlyCollection<int> denied)
    {
        var marked = new HashSet<int>();
        foreach (int start in denied)
            for (int id = start; id >= 0 && id < tree.Count && marked.Add(id); id = tree.Parent[id]) { }
        return marked;
    }

    /// <summary>How long one path's walk may take before its size is given up as unknown.</summary>
    public static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(20);

    /// <summary>The default <see cref="Sizer"/>: <see cref="WalkSize"/> with <see cref="WalkBudget"/>.</summary>
    public static Measurement? WalkSizer(string path) => WalkSize(path, WalkBudget);

    /// <summary>
    /// Sizes one path read-only, without administrator rights, the way a scan does (Mac: walkSizer
    /// with BulkScan): allocated bytes, hard links counted once, reparse points not followed. A
    /// folder Windows won't list makes the size a lower bound (Complete false); running out of
    /// <paramref name="budget"/> makes it unknown. Null when the path doesn't exist. Never throws.
    /// </summary>
    public static Measurement? WalkSize(string path, TimeSpan budget)
    {
        try
        {
            if (File.Exists(path)) return new Measurement(AppLeftoverFinder.AllocatedSize(path), Complete: true);
            if (!Directory.Exists(path)) return null;
        }
        catch { return null; }
        // The scan engine lists with Windows' own directory query (NtQueryDirectoryFile). Off
        // Windows (the test host on a Mac) there is nothing to call, so the size stays unknown.
        if (!OperatingSystem.IsWindows()) return new Measurement(0, Complete: false, Measured: false);
        using var deadline = new CancellationTokenSource(budget);
        try
        {
            var walked = Win32Scanner.Walk(path, progress: null, headStart: null, deadline.Token);
            if (walked is null) return new Measurement(0, Complete: false, Measured: false);
            var totals = walked.Tree.RollUpSizes(SizeBasis.Allocated);
            bool complete = walked.DeniedDirectoryIds.Count == 0 && walked.FailedDirectoryCount == 0;
            return new Measurement(totals.Length > 0 ? Math.Max(0, totals[0]) : 0, complete);
        }
        catch (OperationCanceledException) { return new Measurement(0, Complete: false, Measured: false); }
        catch { return new Measurement(0, Complete: false); }
    }

    /// <summary>
    /// What "What Windows keeps" may add to the basket: only <c>trash</c> entries, never a kept
    /// version, never a path on CleanupQueue's excluded list (which would refuse it anyway).
    /// </summary>
    public static List<Found> StageTargets(Item item) =>
        item.Entry.Action != Action.Trash
            ? []
            : item.FoundPaths.Where(f => !item.Keep.Contains(f.Path) && !CleanupQueue.IsExcludedPath(f.Path)).ToList();

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
