using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskMap.Core.Native;

namespace DiskMap.Core;

/// <summary>
/// Where the elevated app keeps its own files: %ProgramData%\freedisk.space\&lt;user SID&gt;.
///
/// The app runs elevated, so whatever it writes, moves or deletes in its state folder it does as
/// administrator. Under %LOCALAPPDATA%, a folder the signed-in user owns, any program running
/// without admin rights could swap DiskMap, or a folder inside it, for a junction and send those
/// writes and deletes anywhere: a standard way to gain administrator rights. Here only SYSTEM and
/// Administrators may write. The folder is created with a protected access list and checked on
/// every start; a folder someone else made first, or a link, is never used (<see cref="Problem"/>).
///
/// The first time the folder is made, the settings, history, Put Back record, saved searches and
/// snapshots are copied over from %LOCALAPPDATA%\DiskMap, and the old scan cache is deleted with
/// the signed-in user's own rights (an elevated load never trusts it anyway).
/// </summary>
public static class AdminState
{
    public const string FolderName = "freedisk.space";

    private static readonly Lazy<(string? Folder, string? Problem)> State = new(() =>
    {
        try
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("no user SID");
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMap");
            return (Prepare(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), sid, legacy), null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    });

    /// <summary>Why the elevated state folder can't be used (the app then refuses to start), or null.</summary>
    public static string? Problem => State.Value.Problem;

    /// <summary>The checked state folder; throws when <see cref="Problem"/> is set.</summary>
    internal static string Folder => State.Value.Folder ?? throw new InvalidOperationException(State.Value.Problem);

    /// <summary>Creates or checks <c>&lt;programData&gt;\freedisk.space\&lt;sid&gt;</c>; imports <paramref name="legacy"/> into a new one.</summary>
    internal static string Prepare(string programData, string sid, string legacy)
    {
        // Some policies make the creating account the owner of what an administrator creates, and an
        // owner can always rewrite an access list; from here on this process creates as Administrators.
        if (!OwnNewObjectsAsAdministrators())
            throw new InvalidOperationException("Windows wouldn't let it create files owned by Administrators");
        string root = Path.Combine(programData, FolderName);
        CreateProtected(root);
        Require(root);
        string mine = Path.Combine(root, sid);
        bool fresh = !Directory.Exists(mine);
        CreateProtected(mine);
        Require(mine);
        if (fresh) ImportLegacy(legacy, mine);
        return mine;
    }

    /// <summary>Sets this process token's default owner to Administrators (an elevated token may: the group can own).</summary>
    private static bool OwnNewObjectsAsAdministrators()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_DEFAULT | TOKEN_QUERY, out var token)) return false;
        var sid = new byte[Administrators.BinaryLength];
        Administrators.GetBinaryForm(sid, 0);
        var pinned = GCHandle.Alloc(sid, GCHandleType.Pinned);
        try
        {
            var owner = new TOKEN_OWNER { Owner = pinned.AddrOfPinnedObject() };
            return SetTokenInformation(token, TokenOwnerClass, ref owner, Marshal.SizeOf<TOKEN_OWNER>());
        }
        finally
        {
            pinned.Free();
            CloseHandle(token);
        }
    }

    private const uint TOKEN_QUERY = 0x0008, TOKEN_ADJUST_DEFAULT = 0x0080;
    private const int TokenOwnerClass = 4;

    private struct TOKEN_OWNER { public IntPtr Owner; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref TOKEN_OWNER info, int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>SYSTEM and Administrators full control, nothing inherited from %ProgramData% (whose list lets Users create).</summary>
    internal static DirectorySecurity ProtectedSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in TrustedSids)
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, both,
                PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(Administrators);
        return security;
    }

    private static void CreateProtected(string path)
    {
        if (Directory.Exists(path) || File.Exists(path)) return;
        // The list is applied as the folder is created, so there is no moment it is open to Users.
        try { new DirectoryInfo(path).Create(ProtectedSecurity()); }
        catch (IOException) { /* created meanwhile by someone else: Require decides */ }
    }

    private static void Require(string path)
    {
        if (Untrusted(path) is { } why)
            throw new InvalidOperationException(
                $"freedisk.space won't use {path}: {why}. Delete that folder as an administrator, then start freedisk.space again.");
    }

    /// <summary>Why <paramref name="path"/> is not a folder only SYSTEM and Administrators control, or null.</summary>
    internal static string? Untrusted(string path)
    {
        var attributes = File.GetAttributes(path);   // the entry itself: a junction reports ReparsePoint
        if ((attributes & FileAttributes.ReparsePoint) != 0) return "it is a link to somewhere else";
        if ((attributes & FileAttributes.Directory) == 0) return "it is a file, not a folder";
        return UntrustedSecurity(new DirectoryInfo(path).GetAccessControl());
    }

    /// <summary>The access-list half of <see cref="Untrusted"/>: owner and every allowing entry that can change anything.</summary>
    internal static string? UntrustedSecurity(DirectorySecurity security)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
            return "another account owns it";
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & ChangeRights) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier who || !IsTrusted(who))
                return $"{Name(rule.IdentityReference)} may change it";
        }
        return null;
    }

    private static string Name(IdentityReference identity)
    {
        try { return identity.Translate(typeof(NTAccount)).Value; }
        catch { return identity.Value; }
    }

    /// <summary>Rights that let someone change, replace or redirect what is in the folder.</summary>
    private const FileSystemRights ChangeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier[] TrustedSids = [LocalSystem, Administrators];

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid == LocalSystem || sid == Administrators || sid == TrustedInstaller;

    /// <summary>Files copied from the old per-user folder, by path relative to it; ScanCache is left out.</summary>
    private static readonly string[] LegacyFiles =
        ["settings.json", "cleanup-history.json", "last-cleanup.json", "saved-searches.json"];

    private static readonly string[] LegacyFolders = ["History", "snapshots"];

    /// <summary>
    /// Copies the old state in (read only: nothing there is moved or changed), skipping anything
    /// that is a link. The loaders still treat what they read as untrusted. Then the old scan cache
    /// goes, deleted as the signed-in user so a link there can't aim the delete anywhere else.
    /// </summary>
    internal static void ImportLegacy(string legacy, string target)
    {
        if (!Directory.Exists(legacy) || (File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0) return;
        foreach (string name in LegacyFiles)
            CopyPlainFile(Path.Combine(legacy, name), Path.Combine(target, name));
        foreach (string folder in LegacyFolders)
        {
            string from = Path.Combine(legacy, folder);
            if (!Directory.Exists(from) || (File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0) continue;
            Directory.CreateDirectory(Path.Combine(target, folder));
            foreach (var file in new DirectoryInfo(from).EnumerateFiles("*", PlainEntries))
                CopyPlainFile(file.FullName, Path.Combine(target, folder, file.Name));
        }
        UnelevatedLaunch.RunAsShellUser(() => DeleteLegacyCache(Path.Combine(legacy, "ScanCache")));
    }

    private static readonly EnumerationOptions PlainEntries = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static void CopyPlainFile(string from, string to)
    {
        try
        {
            if (!File.Exists(from) || (File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0) return;
            File.Copy(from, to, overwrite: false);
        }
        catch (IOException) { /* one unreadable file never stops the start */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The old cache's files (the app's own; never followed through a link). The empty folder stays.</summary>
    internal static void DeleteLegacyCache(string cache)
    {
        try
        {
            if (!Directory.Exists(cache) || (File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in new DirectoryInfo(cache).EnumerateFiles("*", PlainEntries))
            {
                try { file.Delete(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
