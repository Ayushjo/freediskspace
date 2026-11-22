using System.Runtime.InteropServices;
using DiskMap.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core;

/// <summary>
/// Sequential-output scanner: every directory's raw child listing is
/// parsed before its nodes are inserted, keeping memory proportional to
/// the tree plus one listing per in-flight directory.
///
/// Listing itself uses NtQueryDirectoryFile (FileIdBothDirectory-
/// Information, class 37 — the documented-stable class): one syscall per
/// page returns name, attributes, logical AND real on-disk AllocationSize,
/// creation and modification times, and the file id — everything
/// FindFirstFileExW returns plus the identity hard-link accounting needs,
/// with no extra call per file.
///
/// Directories list in parallel — several dozen at once, deliberately
/// unbounded: listing is IO-bound and the handle count stays small. A
/// denied listing keeps the scan going and records the directory id, so
/// the UI can say which totals are short.
///
/// Names of the same file (hard links) are detected by file id. Every
/// name keeps its recorded size in the tree; the rollup suppresses all
/// but the elected name — same rule as the MFT path and macOS.
/// </summary>
internal static class Win32Scanner
{
    /// <summary>Display name for the scan root — the folder's own name, or the drive root verbatim.</summary>
    internal static string RootName(string rootPath)
    {
        string trimmed = rootPath.TrimEnd('\\', '/');
        string? name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>
    /// The scan root's own modified and created days (days since 1970), so the root reads
    /// "2 days ago" like every other folder instead of "—". 0 when Windows won't say.
    /// </summary>
    internal static (int Modified, int Created) RootDays(string rootPath)
    {
        try
        {
            var info = new DirectoryInfo(rootPath);
            if (!info.Exists) return (0, 0);
            return (Win32.FileTimeToModifiedDay(info.LastWriteTimeUtc.ToFileTimeUtc()),
                    Win32.FileTimeToModifiedDay(info.CreationTimeUtc.ToFileTimeUtc()));
        }
        catch { return (0, 0); }
    }

    /// <summary>One parsed directory entry from a query page.</summary>
    internal readonly record struct Entry(
        string Name,
        uint Attributes,
        long LogicalSize,
        long AllocatedSize,
        int ModifiedDay,
        int CreatedDay,
        long FileId);

    /// <summary>A directory to list: node id, path, and its ancestor node directly under the scan root (-1 = this node's children are their own top-level).</summary>
    private readonly record struct Job(int NodeId, string Path, int TopLevelId);

    private sealed class State
    {
        internal readonly FileTree Tree;
        internal readonly Queue<Job> Jobs = new();
        internal int Inflight;
        internal bool Finished;
        internal int ItemCount;
        internal int NotDownloaded;
        internal int NoIdentity;
        internal readonly List<int> DeniedDirectoryIds = [];
        internal int FailedDirectories;
        internal ulong PeakResidentBytes;
        internal readonly long StartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        internal readonly IProgress<ScanEngine.ScanProgress>? Progress;
        internal long LastReportTicks;
        internal readonly CancellationToken Cancellation;
        internal readonly int RootPathLength;
        /// <summary>fileId → first node id; a repeat flags both as HardLink.</summary>
        internal readonly Dictionary<long, int> FirstByFileId = new();
        /// <summary>Running own-bytes per top-level node — progress attribution.</summary>
        internal readonly Dictionary<int, long> TopBytes = new();
        internal string CurrentFolder = "";

        internal State(FileTree tree, IProgress<ScanEngine.ScanProgress>? progress,
            CancellationToken ct, int rootPathLength)
        {
            Tree = tree;
            Progress = progress;
            Cancellation = ct;
            RootPathLength = rootPathLength;
        }

        /// <summary>Emit the closing report even if the throttle window just fired.</summary>
        internal void ReportFinal()
        {
            LastReportTicks = 0;
            MaybeReport();
        }

        /// <summary>Emit a progress report at most every 250 ms.</summary>
        internal void MaybeReport()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - LastReportTicks < System.Diagnostics.Stopwatch.Frequency / 4) return;
            LastReportTicks = now;
            if (ProcessMemory.Current() is { } mem && mem.ResidentBytes > PeakResidentBytes)
                PeakResidentBytes = mem.ResidentBytes;
            if (Progress is null) return;
            var top = TopBytes.OrderByDescending(kv => kv.Value).Take(8)
                .Select(kv => (Name: Tree.NameOf(kv.Key), Bytes: kv.Value)).ToList();
            double seconds = System.Diagnostics.Stopwatch.GetElapsedTime(StartedTicks).TotalSeconds;
            string current = CurrentFolder.Length > RootPathLength + 1
                ? CurrentFolder[(RootPathLength + 1)..] : "";
            Progress.Report(new ScanEngine.ScanProgress(
                ItemCount, top.Sum(t => t.Bytes),
                seconds > 0 ? ItemCount / seconds : 0, current, top));
        }
    }

    /// <summary>
    /// Scans <paramref name="rootPath"/>. Returns null when
    /// <paramref name="headStart"/> runs out before the walk finishes
    /// (the caller then falls back to the MFT scan). A null headStart is
    /// an unbounded walk.
    /// </summary>
    internal static WalkResult? Walk(
        string rootPath, IProgress<ScanEngine.ScanProgress>? progress,
        TimeSpan? headStart, CancellationToken cancellationToken = default)
    {
        var tree = new FileTree();
        var (rootModified, rootCreated) = RootDays(rootPath);
        int rootId = tree.AddNode(RootName(rootPath).AsSpan(), -1, true, 0, 0, rootModified,
            createdDaysSinceEpoch: rootCreated);
        var state = new State(tree, progress, cancellationToken, rootPath.TrimEnd('\\').Length);
        state.Jobs.Enqueue(new Job(rootId, rootPath, -1));
        state.Inflight = 1;

        int workers = Math.Max(4, Environment.ProcessorCount);
        var threads = new Thread[workers];
        var runStart = DateTime.UtcNow;
        for (int i = 0; i < workers; i++)
        {
            threads[i] = new Thread(() => Worker(state))
            {
                IsBackground = true,
                Name = $"diskmap-walk-{i}",
            };
            threads[i].Start();
        }

        // A real head start is just a deadline on the same queue.
        while (true)
        {
            if (cancellationToken.IsCancellationRequested) break;
            // The deadline wins over an already-finished queue: a caller
            // that asked for a head start gets null, not a result — and the
            // workers stop at their next folder, rather than finishing the
            // whole walk in the background against the MFT read that follows.
            if (headStart is { } budget && DateTime.UtcNow - runStart > budget)
            {
                lock (state.Jobs)
                {
                    state.Finished = true;
                    Monitor.PulseAll(state.Jobs);
                }
                return null;
            }
            bool done;
            lock (state.Jobs) done = state.Finished;
            if (done) break;
            Thread.Sleep(2);
        }
        foreach (var t in threads) t.Join();
        if (cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();
        state.ReportFinal();
        return new WalkResult
        {
            Tree = state.Tree,
            ItemCount = state.ItemCount,
            NotDownloadedCount = state.NotDownloaded,
            PeakResidentBytesDuringWalk = state.PeakResidentBytes,
            Backend = "win32",
            DeniedDirectoryIds = state.DeniedDirectoryIds,
            FailedDirectoryCount = state.FailedDirectories,
            NoIdentityCount = state.NoIdentity,
        };
    }

    private static void Worker(State state)
    {
        var buffer = new byte[128 * 1024];
        while (true)
        {
            Job job;
            lock (state.Jobs)
            {
                if (state.Finished || state.Cancellation.IsCancellationRequested)
                {
                    state.Finished = true;
                    Monitor.PulseAll(state.Jobs);
                    return;
                }
                if (state.Jobs.Count == 0)
                {
                    // Poll so a cancelled scan doesn't park workers forever.
                    Monitor.Wait(state.Jobs, 100);
                    continue;
                }
                job = state.Jobs.Dequeue();
            }
            // A throw costs this folder, never the process (an unhandled
            // exception on a raw thread ends the app), and the job settles
            // exactly once — twice would end the walk early, never would hang it.
            Listing listing;
            try { listing = ScanDirectory(job, state, buffer); }
            catch (Exception) { listing = new Listing([], Failed: true); }
            try { state.Settle(job, listing); }
            catch (Exception) { }   // Settle's accounting runs in a finally
        }
    }

    /// <summary>How one directory's listing ended; Unopened carries the Win32 error or NTSTATUS that stopped it.</summary>
    private readonly record struct Listing(
        List<Entry> Entries, bool Failed, bool Unopened = false, int Error = 0, int NtStatus = 0);

    // FILE_ATTRIBUTE_TAG_INFO via GetFileInformationByHandleEx(FileAttributeTagInfo = 9):
    // one cheap query per folder for "is this handle a link".
    private const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile, int FileInformationClass, out FILE_ATTRIBUTE_TAG_INFO lpFileInformation, int dwBufferSize);

    /// <summary>True when the handle is itself a reparse point (opened with FILE_FLAG_OPEN_REPARSE_POINT).</summary>
    internal static unsafe bool IsLink(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var tag, sizeof(FILE_ATTRIBUTE_TAG_INFO))
        && (tag.FileAttributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    /// <summary>
    /// Field offsets inside one FILE_ID_BOTH_DIR_INFORMATION record —
    /// the common head (NextEntryOffset … FileNameLength) sits at the
    /// same place in every directory-information class; the FileId and
    /// FileName tail are what vary. Verified live; see Win32.cs.
    /// </summary>
    private const int NameLengthOffset = 60;
    private const int FileIdOffset = 96;
    private const int FileNameOffset = 104;

    private static unsafe Listing ScanDirectory(Job job, State state, byte[] buffer)
    {
        string dirPath = Win32.ExtendedPath(job.Path);
        // Below the root every folder was listed as a plain directory, so
        // a link there now was swapped in mid-scan: open it without
        // following, and skip it. The root may be a link the user chose.
        bool belowRoot = job.NodeId != 0;
        using var handle = Win32.CreateFileW(
            dirPath,
            Win32.FILE_LIST_DIRECTORY | Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
            IntPtr.Zero, Win32.OPEN_EXISTING,
            Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_SYNCHRONOUS_IO_NONALERT
                | (belowRoot ? Win32.FILE_FLAG_OPEN_REPARSE_POINT : 0),
            IntPtr.Zero);
        if (handle.IsInvalid)
            return new Listing([], Failed: false, Unopened: true, Error: Marshal.GetLastWin32Error());
        if (belowRoot && IsLink(handle)) return new Listing([], Failed: true);
        lock (state.Jobs) state.CurrentFolder = job.Path;

        var entries = new List<Entry>(256);
        bool failed = false;
        fixed (byte* p = buffer)
        {
            bool restart = true;
            while (true)
            {
                int status = Win32.NtQueryDirectoryFile(
                    handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    out _, p, buffer.Length, Win32.FileIdBothDirectoryInformation,
                    ReturnSingleEntry: false, IntPtr.Zero, RestartScan: restart);
                restart = false;
                if (status == Win32.STATUS_ACCESS_DENIED)
                    return new Listing([], Failed: false, Unopened: true, NtStatus: status);
                if (status == Win32.STATUS_NO_MORE_FILES || status == Win32.STATUS_NO_SUCH_FILE
                    || status == Win32.STATUS_OBJECT_NAME_NOT_FOUND)
                {
                    break;  // end of listing (or an empty directory)
                }
                if (status != Win32.STATUS_SUCCESS)
                {
                    failed = true;
                    break;
                }

                int offset = 0;
                bool sane = true;
                while (true)
                {
                    // Offsets and lengths come from the page (a network
                    // server's, say): bound each before reading, so a bad
                    // one costs this folder rather than a read past the buffer.
                    if (buffer.Length - offset < FileNameOffset)
                    {
                        sane = false;
                        break;
                    }
                    var rec = new ReadOnlySpan<byte>(p + offset, buffer.Length - offset);
                    int next = I32(rec, 0);
                    int nameLength = I32(rec, NameLengthOffset);
                    if (nameLength < 0 || nameLength > rec.Length - FileNameOffset || next < 0 || next > rec.Length)
                    {
                        sane = false;
                        break;  // malformed page — stop trusting it
                    }
                    string name = System.Text.Encoding.Unicode.GetString(
                        rec.Slice(FileNameOffset, nameLength));
                    if (FileTree.IsSafeName(name))
                    {
                        entries.Add(new Entry(
                            Name: name,
                            Attributes: (uint)I32(rec, 56),
                            LogicalSize: Math.Max(0, I64(rec, 40)),
                            AllocatedSize: Math.Max(0, I64(rec, 48)),
                            ModifiedDay: Win32.FileTimeToModifiedDay(I64(rec, 24)),
                            CreatedDay: Win32.FileTimeToModifiedDay(I64(rec, 8)),
                            FileId: I64(rec, FileIdOffset)));
                    }
                    // A name that wouldn't join to its own path is dropped, so this folder's total is short.
                    else if (name is not ("." or "..")) failed = true;
                    if (next == 0) break;
                    offset += next;
                }
                if (!sane) { failed = true; break; }
            }
        }
        return new Listing(entries, failed);

        static int I32(ReadOnlySpan<byte> rec, int at) =>
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(rec[at..]);
        static long I64(ReadOnlySpan<byte> rec, int at) =>
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(rec[at..]);
    }

    /// <summary>
    /// Settles one job: inserts a completed listing's nodes and enqueues
    /// the child directories — always inside the same lock, so a dequeued-
    /// but-unpublished batch can never look like termination. The job's
    /// accounting runs in a finally: a lost decrement would hang the walk.
    /// </summary>
    private static void Settle(this State state, Job job, Listing listing)
    {
        lock (state.Jobs)
        {
            bool failed = listing.Failed;
            try
            {
                if (listing.Unopened) failed = state.NoteUnopened(job, listing.Error, listing.NtStatus);
                else state.Insert(job, listing.Entries);
            }
            catch { failed = true; throw; }
            finally
            {
                state.Inflight--;
                if (failed) state.FailedDirectories++;
                if (state.Inflight == 0 && state.Jobs.Count == 0) state.Finished = true;
                Monitor.PulseAll(state.Jobs);
            }
            if (!listing.Unopened) state.MaybeReport();
        }
    }

    private static void Insert(this State state, Job job, List<Entry> entries)
    {
        foreach (var e in entries)
        {
            var decision = ScanEngine.DecideFromAttributes(
                e.Attributes, e.LogicalSize, e.AllocatedSize);
            if (!decision.Include) continue;
            byte flags = 0;
            if (decision.NotDownloaded)
            {
                flags |= NodeFlags.NotDownloaded;
                state.NotDownloaded++;
            }
            bool isDir = (e.Attributes & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0;
            if (!isDir && e.FileId == 0) state.NoIdentity++;
            int nodeId = state.Tree.AddNode(
                e.Name, job.NodeId, isDir,
                decision.LogicalSize, decision.AllocatedSize,
                e.ModifiedDay, flags, e.CreatedDay, e.FileId);
            state.ItemCount++;
            int top = job.TopLevelId == -1 ? nodeId : job.TopLevelId;
            state.TopBytes[top] = state.TopBytes.GetValueOrDefault(top) + decision.AllocatedSize;
            if (isDir && !decision.SkipDescendants)
            {
                // Inflight counts enqueued-but-unpublished jobs; a
                // publish can only ever be one job down.
                state.Jobs.Enqueue(new Job(nodeId, job.Path + '\\' + e.Name, top));
                state.Inflight++;
            }
            else if (!isDir && e.FileId != 0)
            {
                if (state.FirstByFileId.TryGetValue(e.FileId, out int first))
                {
                    state.Tree.AddFlags(first, NodeFlags.HardLink);
                    state.Tree.AddFlags(nodeId, NodeFlags.HardLink);
                }
                else
                {
                    state.FirstByFileId[e.FileId] = nodeId;
                }
            }
        }
    }

    /// <summary>
    /// A directory whose listing never ran — the node is already in the
    /// tree, so access-denied cases are reported against it while other
    /// failures are only counted. True when this one counts as failed.
    /// </summary>
    private static bool NoteUnopened(this State state, Job job, int error, int ntStatus)
    {
        bool denied = error is Win32.ERROR_ACCESS_DENIED or Win32.ERROR_NETWORK_ACCESS_DENIED
            || ntStatus == Win32.STATUS_ACCESS_DENIED;
        if (denied)
        {
            state.DeniedDirectoryIds.Add(job.NodeId);
            return false;
        }
        // ERROR_FILE_NOT_FOUND / PATH_NOT_FOUND = raced deletion —
        // transient, not a failure worth reporting.
        return error is not (Win32.ERROR_FILE_NOT_FOUND or Win32.ERROR_PATH_NOT_FOUND)
            && ntStatus is not (Win32.STATUS_NO_SUCH_FILE or Win32.STATUS_OBJECT_NAME_NOT_FOUND);
    }
}
