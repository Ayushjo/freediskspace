using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskMap.Core;
using DiskMap.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Tests;

/// <summary>
/// The basket at scale (a bulk add of tens of thousands must not cost items²) and the
/// launcher that opens things with the signed-in user's rights instead of the app's.
/// </summary>
public class BasketFoundationTests
{
    private static CleanupQueue.StagedItem Item(string path) => new() { Path = path, Size = 1, Reason = "test" };

    /// <summary>The pairwise rule CoveredItemIds replaced, kept as the oracle.</summary>
    private static HashSet<Guid> PairwiseCovered(IReadOnlyList<CleanupQueue.StagedItem> items)
    {
        var folders = items.Select(i => i.Path.TrimEnd('\\', '/') + '\\').ToArray();
        var covered = new HashSet<Guid>();
        for (int i = 0; i < items.Count; i++)
            for (int j = 0; j < folders.Length; j++)
                if (i != j && items[i].Path.StartsWith(folders[j], StringComparison.OrdinalIgnoreCase))
                {
                    covered.Add(items[i].Id);
                    break;
                }
        return covered;
    }

    [Fact]
    public void CoveredItemsMatchThePairwiseRule()
    {
        var items = new List<CleanupQueue.StagedItem>
        {
            Item(@"C:\work\app"),
            Item(@"C:\work\app\node_modules"),
            Item(@"C:\work\app\node_modules\left-pad\index.js"),
            Item(@"C:\work\application"),            // a sibling sharing the prefix is not inside
            Item(@"C:\WORK\APP\dist"),               // case differs: still inside
            Item(@"D:\other\x.bin"),
            Item(@"\\nas\share\builds"),
            Item(@"\\nas\share\builds\old.zip"),
        };
        var expected = PairwiseCovered(items);
        Assert.Equal(expected.OrderBy(g => g), CleanupQueue.CoveredItemIds(items).OrderBy(g => g));
        Assert.Equal(4, expected.Count);
    }

    [Fact]
    public void CoveredItemsScaleToTensOfThousands()
    {
        var items = new List<CleanupQueue.StagedItem>();
        for (int i = 0; i < 40_000; i++)
            items.Add(Item($@"C:\Users\demo\projects\p{i / 100:D4}\app{i % 100:D2}\node_modules"));
        items.Add(Item(@"C:\Users\demo\projects\p0001"));   // covers 100 of them
        var clock = Stopwatch.StartNew();
        var covered = CleanupQueue.CoveredItemIds(items);
        clock.Stop();
        Assert.Equal(100, covered.Count);
        // Pairwise this was 1.6 billion comparisons (seconds); the walk is items × depth.
        Assert.True(clock.ElapsedMilliseconds < 1_000, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ARemovedPathCanBeStagedAgainAndADuplicateCannot()
    {
        string root = Path.Combine(Path.GetTempPath(), "dm-basket-" + Guid.NewGuid().ToString("N")[..8]);
        string path = Path.Combine(root, "not-there", "cache");
        var queue = new CleanupQueue();
        Assert.True(queue.Stage(path, 1, "test"));
        Assert.False(queue.Stage(path.ToUpperInvariant(), 1, "test"));   // same path, other case
        queue.Unstage(queue.AllItems().Single().Id);
        Assert.True(queue.Stage(path, 1, "test"));
        Assert.Single(queue.AllItems());
    }

    // ---- UnelevatedLaunch ----

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        ref ProcessBasicInformation info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);

    [Fact]
    public void ShellChildRunsUnderTheShellNotThisProcess()
    {
        uint shell = UnelevatedLaunch.ShellProcessId();
        if (shell == 0) return;   // no desktop shell (a service or CI session): nothing to verify
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var child = UnelevatedLaunch.StartAsShellChild(cmd, "/c exit 7", null, UnelevatedLaunch.CREATE_NO_WINDOW);
        Assert.NotNull(child);
        var info = new ProcessBasicInformation();
        Assert.Equal(0, NtQueryInformationProcess(child!, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _));
        // The documented PARENT_PROCESS attribute: the shell, whose token the child inherits.
        Assert.Equal((long)shell, info.InheritedFromUniqueProcessId.ToInt64());
        Assert.Equal(0u, WaitForSingleObject(child!, 10_000));
        Assert.True(GetExitCodeProcess(child!, out uint code));
        Assert.Equal(7u, code);
    }
}
