namespace DiskMap.Core;

/// <summary>
/// The user folders the catalogs read: the profile, %LOCALAPPDATA% and
/// %APPDATA%. Normally the real known folders. Setting DISKMAP_HOME points all
/// three at a stand-in profile (DISKMAP_HOME, DISKMAP_HOME\AppData\Local,
/// DISKMAP_HOME\AppData\Roaming) so a demo folder classifies exactly like a
/// real profile. Used by the render harness and tests only.
/// CleanupQueue's excluded prefixes always read the real folders, but
/// StorageClassifier reads a path under the override as C:\Users\demo\…, so its
/// profile and container rules — and with them IsExcludedPath's container check —
/// follow the stand-in. An override moves what counts as the profile, which is why
/// the app drops DISKMAP_* when it runs elevated.
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

    /// <summary>Set by the test suite and the render harness so they never touch the user's state.</summary>
    public const string StateVariable = "DISKMAP_STATE_DIR";

    /// <summary>
    /// Where the app keeps its own files: settings, scan history, the scan cache, snapshots,
    /// saved searches and the last cleanup's Put Back record. %LOCALAPPDATA%\DiskMap (the
    /// real one — DISKMAP_HOME never moves it) unless DISKMAP_STATE_DIR points elsewhere.
    /// </summary>
    public static string StateDirectory =>
        Environment.GetEnvironmentVariable(StateVariable) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMap");

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
