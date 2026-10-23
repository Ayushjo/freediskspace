using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskMap.Core;

namespace DiskMap.App;

/// <summary>
/// User settings (WIN-058/059/060): appearance + text scale, persisted
/// to %LOCALAPPDATA%\DiskMap\settings.json. Deliberately tiny — anything
/// bigger graduates to a real settings surface.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("appearance")] public string Appearance { get; set; } = "system"; // system|light|dark
    [JsonPropertyName("textScale")] public double TextScale { get; set; } = 1.0;        // 0.9 / 1.0 / 1.15 / 1.3
    /// <summary>Record a history entry after each scan (the "what grew" comparisons).</summary>
    [JsonPropertyName("keepHistory")] public bool KeepHistory { get; set; } = true;
    /// <summary>WIN-066: after each scan on a ReFS volume, map extents and count block clones once.</summary>
    [JsonPropertyName("cloneAccounting")] public bool CloneAccounting { get; set; }
    [JsonPropertyName("showDusty")] public bool ShowDusty { get; set; } = true;
    /// <summary>Clean up ticks what rebuilds by itself for you (Mac: CleanUp.TickRebuilds); off = tick everything yourself.</summary>
    [JsonPropertyName("tickRebuilds")] public bool TickRebuilds { get; set; } = true;
    /// <summary>The notification-area icon with free space and the last scan (Mac: ShowMenuBarExtra).</summary>
    [JsonPropertyName("showTray")] public bool ShowTray { get; set; } = true;
    /// <summary>Which copy Duplicates keeps (Mac: Duplicates.Keep): oldest | newest | shortestPath.</summary>
    [JsonPropertyName("duplicatesKeep")] public string DuplicatesKeep { get; set; } = "oldest";
    /// <summary>Onboarding: what the person does (profiles.json role ids) — empty = skipped.</summary>
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = [];
    /// <summary>Onboarding: the tools they picked (profiles.json tool ids).</summary>
    [JsonPropertyName("tools")] public List<string> Tools { get; set; } = [];
    /// <summary>True once onboarding was finished or skipped — it shows on first launch only.</summary>
    [JsonPropertyName("onboarded")] public bool Onboarded { get; set; }

    /// <summary>
    /// Whether Tools &amp; Projects belongs in the sidebar: always without a
    /// profile, otherwise only when the profile brings any tool ecosystem
    /// (an everyday user who picked nothing doesn't get a developer page).
    /// </summary>
    public bool ShowsToolsPage => UserProfiles.EcosystemsFor(Roles, Tools) is not { Count: 0 };

    private static string Path0 => System.IO.Path.Combine(DiskMap.Core.UserFolders.StateDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path0)) ?? new AppSettings();
            if (Math.Abs(settings.TextScale - 1.1) < 0.001) settings.TextScale = 1.15;
            if (Math.Abs(settings.TextScale - 1.2) < 0.001) settings.TextScale = 1.3;
            return settings;
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path0)!);
            File.WriteAllText(Path0, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    /// <summary>Resolved dark flag for the palette — "system" follows the OS.</summary>
    public bool WantsDark => Appearance switch
    {
        "dark" => true,
        "light" => false,
        _ => Theme.IsDark,
    };
}
