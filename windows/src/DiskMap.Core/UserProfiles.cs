using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMap.Core;

/// <summary>One "what do you do" choice in onboarding.</summary>
public sealed record ProfileRole(string Id, string Title, string Blurb);

/// <summary>One tool a role uses, and the developer ecosystems it brings.</summary>
public sealed record ProfileTool(
    string Id, string Title, IReadOnlyList<string> Roles,
    IReadOnlyList<DeveloperEcosystem> Ecosystems, IReadOnlyList<string> Detect);

/// <summary>
/// The onboarding catalog (profiles.json): roles, the tools each role
/// picks from, and what a choice means for Developer. Detection
/// only asks whether a path exists — nothing is read, nothing leaves the PC.
/// </summary>
public static class UserProfiles
{
    private sealed class RoleEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("blurb")] public string Blurb { get; set; } = "";
    }

    private sealed class ToolEntry
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("roles")] public List<string> Roles { get; set; } = [];
        [JsonPropertyName("ecosystems")] public List<string> Ecosystems { get; set; } = [];
        [JsonPropertyName("detect")] public List<string> Detect { get; set; } = [];
    }

    private sealed class CatalogFile
    {
        [JsonPropertyName("roles")] public List<RoleEntry> Roles { get; set; } = [];
        [JsonPropertyName("tools")] public List<ToolEntry> Tools { get; set; } = [];
    }

    private static readonly Lazy<(List<ProfileRole> Roles, List<ProfileTool> Tools)> Catalog = new(Load);

    public static IReadOnlyList<ProfileRole> Roles => Catalog.Value.Roles;
    public static IReadOnlyList<ProfileTool> Tools => Catalog.Value.Tools;

    private static (List<ProfileRole>, List<ProfileTool>) Load()
    {
        var assembly = typeof(UserProfiles).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("profiles.json", StringComparison.OrdinalIgnoreCase));
        using var stream = name is null ? null : assembly.GetManifestResourceStream(name);
        if (stream is null) return ([], []);
        var file = JsonSerializer.Deserialize<CatalogFile>(stream,
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip }) ?? new CatalogFile();
        var roles = file.Roles.Select(r => new ProfileRole(r.Id, r.Title, r.Blurb)).ToList();
        var tools = file.Tools.Select(t => new ProfileTool(
            t.Id, t.Title, t.Roles,
            t.Ecosystems.Select(e => Enum.TryParse<DeveloperEcosystem>(e, ignoreCase: true, out var eco) ? eco : DeveloperEcosystem.Other)
                .Distinct().ToList(),
            t.Detect)).ToList();
        return (roles, tools);
    }

    /// <summary>The tools offered for these roles, in catalog order.</summary>
    public static List<ProfileTool> ToolsFor(IEnumerable<string> roleIds)
    {
        var roles = roleIds.ToHashSet(StringComparer.Ordinal);
        return Tools.Where(t => t.Roles.Any(roles.Contains)).ToList();
    }

    /// <summary>Tools with a detect path that exists on this PC.</summary>
    public static HashSet<string> Detected(Func<string, bool>? exists = null)
    {
        exists ??= PathExists;
        return Tools.Where(t => t.Detect.Any(p => exists(Environment.ExpandEnvironmentVariables(p))))
            .Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A file or folder at <paramref name="path"/>; a trailing * in the last segment matches by prefix.</summary>
    internal static bool PathExists(string path)
    {
        try
        {
            if (!path.EndsWith('*')) return Directory.Exists(path) || File.Exists(path);
            string? parent = Path.GetDirectoryName(path);
            return parent is not null && Directory.Exists(parent)
                && Directory.EnumerateFileSystemEntries(parent, Path.GetFileName(path)).Any();
        }
        catch { return false; }
    }

    /// <summary>
    /// What Developer shows for a profile: null = everything (no
    /// profile yet, or the person skipped onboarding). Picked tools decide;
    /// roles without picked tools fall back to every tool of those roles.
    /// An empty set means nothing developer-shaped (an everyday user).
    /// </summary>
    public static HashSet<DeveloperEcosystem>? EcosystemsFor(IReadOnlyCollection<string>? roles, IReadOnlyCollection<string>? tools)
    {
        if (roles is null || roles.Count == 0) return null;
        var picked = tools is { Count: > 0 }
            ? Tools.Where(t => tools.Contains(t.Id))
            : ToolsFor(roles);
        return picked.SelectMany(t => t.Ecosystems).ToHashSet();
    }
}
