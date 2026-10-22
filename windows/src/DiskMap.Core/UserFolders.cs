namespace DiskMap.Core;

/// <summary>
/// The user folders the catalogs read: the profile, %LOCALAPPDATA% and
/// %APPDATA%. Normally the real known folders. Setting DISKMAP_HOME points all
/// three at a stand-in profile (DISKMAP_HOME, DISKMAP_HOME\AppData\Local,
/// DISKMAP_HOME\AppData\Roaming) so a demo folder classifies exactly like a
/// real profile. Used by the render harness and tests only.
/// The safety lists in CleanupQueue never use this: they always read the real
/// folders, so an override can't loosen what may be staged.
/// </summary>
public static class UserFolders
{
    public const string OverrideVariable = "DISKMAP_HOME";

    /// <summary>The stand-in profile root, or null when none is set.</summary>
    public static string? Override
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(OverrideVariable);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value).TrimEnd('\\', '/');
        }
    }

    public static string Home =>
        Override ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string LocalAppData =>
        Override is { } root ? Path.Combine(root, "AppData", "Local")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string RoamingAppData =>
        Override is { } root ? Path.Combine(root, "AppData", "Roaming")
            : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// Expands "~/x", "%USERPROFILE%", "%LOCALAPPDATA%" and "%APPDATA%" against
    /// these folders (so an override applies), then any other variable from the
    /// environment.
    /// </summary>
    public static string Expand(string path)
    {
        if (path.StartsWith("~/") || path.StartsWith(@"~\"))
            path = Path.Combine(Home, path[2..]);
        path = ReplaceVariable(path, "%USERPROFILE%", Home);
        path = ReplaceVariable(path, "%LOCALAPPDATA%", LocalAppData);
        path = ReplaceVariable(path, "%APPDATA%", RoamingAppData);
        return Environment.ExpandEnvironmentVariables(path);
    }

    private static string ReplaceVariable(string path, string variable, string value) =>
        path.Replace(variable, value, StringComparison.OrdinalIgnoreCase);
}
