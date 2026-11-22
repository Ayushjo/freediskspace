using System.Globalization;

namespace DiskMap.Core;

/// <summary>Which copy of a duplicate group stays (Mac: DuplicatesView.KeepPolicy).</summary>
public enum DuplicateKeepPolicy { Oldest, Newest, ShortestPath }

/// <summary>The Duplicates page's kind filter (Mac: DuplicatesView.DupKind).</summary>
public enum DuplicateKind { All, Videos, Photos, Archives, Audio, Documents, Other }

/// <summary>What one tick-box click did to a group (Mac: DuplicatesView.toggle).</summary>
public sealed record DuplicateToggle(HashSet<int> Checked, int? NewKeeper, bool KeepOneRefused);

/// <summary>
/// The Duplicates page's review rules, headless so they can be tested (Mac: the static
/// helpers on DuplicatesView). Which copy stays, which copies start ticked, swapping the
/// kept copy, never ticking every copy of a file, and the page's wording for kinds,
/// places and dates.
/// </summary>
public static class DuplicateReview
{
    // ---- Keep policy ----

    public static string Title(this DuplicateKeepPolicy policy) => policy switch
    {
        DuplicateKeepPolicy.Oldest => "Oldest",
        DuplicateKeepPolicy.Newest => "Newest",
        _ => "Shortest path",
    };

    /// <summary>The group card's tag: "keeps the oldest".</summary>
    public static string Tag(this DuplicateKeepPolicy policy) => policy switch
    {
        DuplicateKeepPolicy.Oldest => "keeps the oldest",
        DuplicateKeepPolicy.Newest => "keeps the newest",
        _ => "keeps the shortest path",
    };

    /// <summary>The stored value (Mac: the rawValue under "Duplicates.Keep").</summary>
    public static string Key(this DuplicateKeepPolicy policy) => policy switch
    {
        DuplicateKeepPolicy.Oldest => "oldest",
        DuplicateKeepPolicy.Newest => "newest",
        _ => "shortestPath",
    };

    /// <summary>A stored value back to a policy; anything unknown keeps the oldest.</summary>
    public static DuplicateKeepPolicy ParsePolicy(string? key) => key switch
    {
        "newest" => DuplicateKeepPolicy.Newest,
        "shortestPath" => DuplicateKeepPolicy.ShortestPath,
        _ => DuplicateKeepPolicy.Oldest,
    };

    /// <summary>The header's "We keep the … and tick the rest" phrase.</summary>
    public static string KeepPhrase(this DuplicateKeepPolicy policy) =>
        policy == DuplicateKeepPolicy.ShortestPath ? "copy with the shortest path" : policy.Title().ToLowerInvariant() + " copy";

    /// <summary>
    /// Which copy stays under a policy. Ties go to the oldest, then the lowest id, so it
    /// never flickers. While some copy can stay, a copy <paramref name="isCovered"/> says
    /// goes with the basket (in it, or inside a folder in it) never does, nor does the
    /// person's <paramref name="chosen"/> copy if it is one of those.
    /// </summary>
    public static int? Keeper(DuplicateGroup group, DuplicateKeepPolicy policy, Func<int, int> day, Func<int, int> pathLength,
        Func<int, bool>? isCovered = null, int? chosen = null)
    {
        var ids = group.FileIDs;
        if (isCovered is not null && ids.Where(id => !isCovered(id)).ToList() is { Count: > 0 } free) ids = free;
        if (ids.Count == 0) return null;
        if (chosen is { } pick && ids.Contains(pick)) return pick;
        return policy switch
        {
            DuplicateKeepPolicy.Oldest => ids.MinBy(id => (day(id), id)),
            DuplicateKeepPolicy.Newest => ids.MinBy(id => (-(long)day(id), id)),
            _ => ids.MinBy(id => (pathLength(id), day(id), id)),
        };
    }

    /// <summary>The kept copy first, then the rest as found.</summary>
    public static List<int> Ordered(DuplicateGroup group, int? keeper) =>
        keeper is { } k && group.FileIDs.Contains(k)
            ? [k, .. group.FileIDs.Where(id => id != k)]
            : [.. group.FileIDs];

    /// <summary>
    /// Ticking the kept copy swaps: the first other unticked copy (else the first other) stays
    /// instead. A copy already in the basket can't stay; with no other copy left, no swap.
    /// </summary>
    public static (int Keeper, HashSet<int> Checked)? Swap(DuplicateGroup group, int keeper, IReadOnlySet<int> isChecked,
        Func<int, bool>? isStaged = null)
    {
        var others = group.FileIDs.Where(id => id != keeper && isStaged?.Invoke(id) != true).ToList();
        if (others.Count == 0) return null;
        int next = others.FirstOrDefault(id => !isChecked.Contains(id), others[0]);
        var result = new HashSet<int>(isChecked) { keeper };
        result.Remove(next);
        return (next, result);
    }

    /// <summary>
    /// One click on a copy's box: untick it, tick it, or (on the kept copy) swap which stays.
    /// Never every copy of a file: a click that would tick the last one is undone and says so.
    /// </summary>
    public static DuplicateToggle Toggle(DuplicateGroup group, int id, bool isKeeper, IReadOnlySet<int> isChecked,
        Func<int, bool>? isStaged = null)
    {
        var next = new HashSet<int>(isChecked);
        int? newKeeper = null;
        if (next.Contains(id))
        {
            next.Remove(id);
        }
        else if (isKeeper)
        {
            if (Swap(group, id, isChecked, isStaged) is not { } swapped) return new DuplicateToggle(next, null, true);
            newKeeper = swapped.Keeper;
            next = swapped.Checked;
        }
        else
        {
            next.Add(id);
        }
        bool refused = false;
        // Ticked or already in the basket: either way that copy goes.
        if (group.FileIDs.All(other => next.Contains(other) || isStaged?.Invoke(other) == true))
        {
            next.Remove(id);
            refused = true;
        }
        return new DuplicateToggle(next, newKeeper, refused);
    }

    /// <summary>
    /// What Duplicates ticks for you: every copy but the kept one in groups that free
    /// something. Copies already in the basket (or in a folder that is) stay as they are;
    /// copies <see cref="DuplicatePreTick.NeverPreTicked"/> rules out are never pre-ticked
    /// (Mac: selectOtherCopies).
    /// </summary>
    public static HashSet<int> PreTicked(IEnumerable<DuplicateGroup> groups, Func<DuplicateGroup, int?> keeperOf,
        Func<int, string> pathOf, Func<string, bool> isStaged)
    {
        var next = new HashSet<int>();
        foreach (var group in groups)
        {
            if (group.SharesStorage) continue;
            int? keep = keeperOf(group);
            foreach (int id in group.FileIDs)
            {
                if (id == keep) continue;
                string path = pathOf(id);
                if (!isStaged(path) && !DuplicatePreTick.NeverPreTicked(path)) next.Add(id);
            }
        }
        return next;
    }

    /// <summary>
    /// The search's answer checked against the disk again, just before ticked copies go in the
    /// basket: hashes are trusted only while each copy keeps the length and last write it had
    /// when it was read. <c>Changed</c> is the ticked copies that no longer match (leave them
    /// out); <c>KeptCopyIntact</c> is false when no copy that stays (unticked, not in the
    /// basket) still matches, and then nothing of the group may go in.
    /// </summary>
    public static (bool KeptCopyIntact, HashSet<int> Changed) Recheck(DuplicateGroup group, IReadOnlySet<int> ticked,
        Func<int, bool> isCovered, Func<int, string> pathOf, Func<string, FileStamp?>? stampOf = null)
    {
        stampOf ??= FileStamp.Of;
        bool same(int id) => group.Stamps is { } stamps && stamps.TryGetValue(id, out var then)
                             && stampOf(pathOf(id)) == then;
        var changed = group.FileIDs.Where(id => ticked.Contains(id) && !same(id)).ToHashSet();
        bool kept = group.FileIDs.Any(id => !ticked.Contains(id) && !isCovered(id) && same(id));
        return (kept, changed);
    }

    /// <summary>A stable key for a group: clone-only groups all hash "shared-extents".</summary>
    public static string GroupKey(DuplicateGroup group) =>
        group.Hash + "#" + (group.FileIDs.Count > 0 ? group.FileIDs.Min() : -1).ToString(CultureInfo.InvariantCulture);

    // ---- Kinds ----

    public static string Title(this DuplicateKind kind) => kind switch
    {
        DuplicateKind.All => "All",
        DuplicateKind.Videos => "Videos",
        DuplicateKind.Photos => "Photos",
        DuplicateKind.Archives => "Archives",
        DuplicateKind.Audio => "Audio",
        DuplicateKind.Documents => "Documents",
        _ => "Other",
    };

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "heic", "heif", "png", "gif", "tif", "tiff", "dng", "raw", "cr2", "cr3", "nef", "arw", "orf", "webp",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "aif", "aiff", "m4a", "flac", "aac", "ogg", "caf",
    };

    /// <summary>Which filter a file falls under, by its name (Mac: DuplicatesView.kind(of:)).</summary>
    public static DuplicateKind KindOf(string name)
    {
        int dot = name.LastIndexOf('.');
        string ext = dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : "";
        if (PhotoExtensions.Contains(ext)) return DuplicateKind.Photos;
        if (AudioExtensions.Contains(ext)) return DuplicateKind.Audio;
        return FileKinds.Classify(name, name) switch
        {
            FileKind.Video => DuplicateKind.Videos,
            FileKind.Archive or FileKind.DiskImage => DuplicateKind.Archives,
            FileKind.Document => DuplicateKind.Documents,
            _ => DuplicateKind.Other,
        };
    }

    // ---- Words ----

    /// <summary>
    /// "~\Movies\Holiday.mov" under the home folder; otherwise from the scanned folder
    /// ("dupdemo\Movies\Holiday.mov"); otherwise the full path.
    /// </summary>
    public static string Place(string path, string root, string? home = null)
    {
        string display = CanonicalPath.DisplayPath(path, home);
        if (display.StartsWith('~')) return display;
        string root0 = root.TrimEnd('\\', '/');
        if (root0.Length == 0 || path.Length <= root0.Length + 1
            || !path.StartsWith(root0, StringComparison.OrdinalIgnoreCase)
            || path[root0.Length] is not ('\\' or '/'))
            return display;
        int cut = root0.LastIndexOfAny(['\\', '/']);
        string name = cut >= 0 ? root0[(cut + 1)..] : root0;
        if (name.Length == 0) name = root0;
        return name + path[root0.Length..];
    }

    /// <summary>"12 Jan 2024", from days since 1970; "—" when unknown.</summary>
    public static string DateText(int day) =>
        day <= 0 ? "—"
            : DateTime.UnixEpoch.AddDays(day).ToString("dd MMM yyyy", CultureInfo.GetCultureInfo("en-GB"));
}

/// <summary>
/// Which duplicate copies Duplicates ticks for you. A copy inside a project's
/// dependencies, build output, version control or an app's own folder belongs to a tool:
/// recycling it breaks that project or app, so it is never pre-ticked (the person can
/// still tick it by hand). Mac: DuplicatePreTick.
/// </summary>
public static class DuplicatePreTick
{
    private static readonly HashSet<string> ToolFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", ".git", ".svn", ".hg", ".build", "build", "dist", "out",
        ".next", ".nuxt", ".turbo", ".parcel-cache", ".svelte-kit", ".expo", "pods", "deriveddata",
        "vendor", "target", ".venv", "venv", "site-packages", "__pycache__", ".gradle", ".dart_tool",
        // Windows: Visual Studio's state folder and .NET's intermediate build output.
        ".vs", "obj",
    };

    private static readonly string[] BundleSuffixes =
        [".app", ".framework", ".bundle", ".plugin", ".photoslibrary", ".xcodeproj", ".xcworkspace"];

    /// <summary>Program files: identical ones are two programs' own copies of a library (UnityPlayer.dll, libcef.dll).</summary>
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".dll", ".sys" };

    /// <summary>
    /// Never ticked for you: a copy a tool or app owns, a program file, or a copy in a cloud
    /// sync folder (recycling that one deletes it from the cloud and every synced PC too).
    /// The person can still tick any of them by hand.
    /// </summary>
    public static bool NeverPreTicked(string path) =>
        IsToolOwned(path)
        || ProgramExtensions.Contains(Path.GetExtension(path))
        // The folder, not the file: a file rule (.iso, .zip) would win over the cloud area.
        || Path.GetDirectoryName(path) is { Length: > 0 } folder
           && StorageClassifier.Classify(folder, isDirectory: true).Class == StorageClassifier.Cloud;

    /// <summary>True when a folder above the file (not the file itself) belongs to a tool. Either separator.</summary>
    public static bool IsToolOwned(string path)
    {
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string name = parts[i];
            if (ToolFolders.Contains(name)) return true;
            foreach (string suffix in BundleSuffixes)
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
