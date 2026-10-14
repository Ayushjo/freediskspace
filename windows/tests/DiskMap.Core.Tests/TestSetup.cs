using System.Runtime.CompilerServices;
using DiskMap.Core;

namespace DiskMap.Core.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Commit tests recycle real temp files; their receipt goes to a temp
    /// file instead of %LOCALAPPDATA%\DiskMap\last-cleanup.json — a test run
    /// must never replace the receipt the user's own Put Back relies on.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateCleanupReceipt() =>
        CleanupRecord.PathOverride = Path.Combine(Path.GetTempPath(), $"dm-tests-last-cleanup-{Environment.ProcessId}.json");
}
