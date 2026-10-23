using System.Runtime.CompilerServices;
using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// Points the app's own files (settings, history, scan cache, the last cleanup's Put Back
/// record) at a throwaway folder before any test runs. Without this a test that commits a
/// cleanup replaced the user's real Put Back record.
/// </summary>
internal static class TestState
{
    public static readonly string Directory =
        Path.Combine(Path.GetTempPath(), $"freedisk-tests-{Environment.ProcessId}");

    [ModuleInitializer]
    internal static void Isolate() => Environment.SetEnvironmentVariable(UserFolders.StateVariable, Directory);
}

// Runs alone (the collection disables parallelization): one test briefly clears the variable,
// and nothing else may write state while it's unset.
[Collection("ProcessEnvironment")]
public class TestStateTests
{
    [Fact]
    public void TestsNeverWriteTheUsersState()
    {
        Assert.Equal(Path.GetFullPath(TestState.Directory), UserFolders.StateDirectory);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), CleanupRecord.DefaultPath());
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), StorageHistory.DefaultDirectory());
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), ScanCache.DirectoryPath);
    }

    [Fact]
    public void WithoutTheVariableStateIsUnderLocalAppData()
    {
        string? previous = Environment.GetEnvironmentVariable(UserFolders.StateVariable);
        try
        {
            Environment.SetEnvironmentVariable(UserFolders.StateVariable, null);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskMap"),
                UserFolders.StateDirectory);
        }
        finally { Environment.SetEnvironmentVariable(UserFolders.StateVariable, previous); }
    }
}
