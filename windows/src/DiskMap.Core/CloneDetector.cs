using System.Buffers;
using DiskMap.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core;

/// <summary>
/// Detects storage-sharing file copies by comparing physical extent maps —
/// the Windows equivalent of the macOS fcntl(F_LOG2PHYS_EXT) check.
///
/// Windows has no high-level "is this a clone of that" API either. The real
/// technique is FSCTL_GET_RETRIEVAL_POINTERS, which returns the file's
/// VCN→LCN map: two files whose logical offsets land on the same physical
/// clusters share backing storage. On Windows that's ReFS block clones
/// (FSCTL_DUPLICATE_EXTENTS_TO_FILE — what copy does on a Dev Drive volume)
/// and hardlinks. NTFS has no block cloning, so on NTFS this only fires for
/// hardlink-like shared extents.
///
/// A clone overwritten in the middle diverges in its extent map, so the
/// full map is compared — same rule as the macOS build. Extents are
/// comparable only within one volume, so the volume serial is checked
/// first; names of one file record (hard links) need no map at all.
/// </summary>
public static class CloneDetector
{
    internal readonly record struct Extent(long FileOffset, long DeviceOffset, long LengthBytes);

    /// <summary>One file's storage: its volume, its file record and how many names it has (hard links share one), and its physical map.</summary>
    internal readonly record struct Storage(uint Volume, ulong Record, uint Links, List<Extent>? Extents);

    /// <summary>
    /// The largest map fetched, 65,535 extents: the buffer used to grow with the file's length,
    /// so two equal sparse terabyte files asked for gigabytes. A map that doesn't fit isn't compared.
    /// </summary>
    private const int MaxMapBytes = 1 << 20;

    private static SafeFileHandle Open(string path) => Win32.CreateFileW(
        Win32.ExtendedPath(path), Win32.FILE_READ_ATTRIBUTES,
        Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
        IntPtr.Zero, Win32.OPEN_EXISTING,
        Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_FLAG_OPEN_REPARSE_POINT,
        IntPtr.Zero);

    /// <summary>
    /// Full physical map, or null if the file can't be mapped (missing,
    /// empty, or a filesystem that doesn't answer FSCTL_GET_RETRIEVAL_POINTERS
    /// — e.g. exFAT, network shares). Shared with the block-clone
    /// profiler (WIN-066), which maps every candidate file once.
    /// </summary>
    internal static List<Extent>? ExtentMapOf(string path)
    {
        using var handle = Open(path);
        return handle.IsInvalid ? null : MapOf(handle, path);
    }

    /// <summary>Volume, file record and map from one open; null when the file can't be opened.</summary>
    internal static Storage? StorageOf(string path)
    {
        using var handle = Open(path);
        if (handle.IsInvalid || !Win32.GetFileInformationByHandle(handle, out var info)) return null;
        return new Storage(info.dwVolumeSerialNumber, (ulong)info.nFileIndexHigh << 32 | info.nFileIndexLow,
            info.nNumberOfLinks, MapOf(handle, path));
    }

    /// <summary>
    /// Room for ~8 clusters per extent, capped. ERROR_MORE_DATA (more extents than fit)
    /// leaves the file unmapped, so never compared.
    /// </summary>
    internal static int MapBufferSize(long endOfFile) =>
        (int)Math.Clamp(endOfFile / 4096 / 8 * 16 + 64, 4096, MaxMapBytes);

    private static unsafe List<Extent>? MapOf(SafeFileHandle handle, string path)
    {
        if (!Win32.GetFileInformationByHandleEx(handle, Win32.FileStandardInfo, out var info,
                sizeof(Win32.FILE_STANDARD_INFO)) || info.EndOfFile <= 0)
        {
            return null;
        }

        if (!Win32.GetVolumeInformationByHandleW(handle, IntPtr.Zero, 0, out uint serial,
                out _, out _, IntPtr.Zero, 0))
        {
            return null;
        }

        int size = MapBufferSize(info.EndOfFile);
        var input = new long[1]; // STARTING_VCN_INPUT_BUFFER
        byte[] outBuf = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            int bytesReturned;
            bool ok;
            fixed (void* inPtr = input, outPtr = outBuf)
            {
                ok = Win32.DeviceIoControl(
                    handle, Win32.FSCTL_GET_RETRIEVAL_POINTERS,
                    inPtr, sizeof(long), outPtr, size, out bytesReturned, IntPtr.Zero);
            }
            if (!ok || bytesReturned < 16) return null;

            int extentCount = Math.Min(BitConverter.ToInt32(outBuf, 0), (bytesReturned - 16) / 16);
            long startingVcn = BitConverter.ToInt64(outBuf, 8);
            long clusterSize = ClusterSize(path);
            var extents = new List<Extent>(extentCount);
            long prevVcn = startingVcn;
            for (int i = 0; i < extentCount; i++)
            {
                int off = 16 + i * 16;
                long nextVcn = BitConverter.ToInt64(outBuf, off);
                long lcn = BitConverter.ToInt64(outBuf, off + 8);
                long length = (nextVcn - prevVcn) * clusterSize;
                // LCN -1 means a sparse (unallocated) run — encode as -1 so two
                // sparse runs never falsely "match" physical storage.
                long device = lcn == -1 ? -1 : lcn * clusterSize;
                extents.Add(new Extent(prevVcn * clusterSize, device, length));
                prevVcn = nextVcn;
            }
            return extents;
        }
        finally { ArrayPool<byte>.Shared.Return(outBuf); }
    }

    private static long ClusterSize(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is not null
            && Win32.GetDiskFreeSpaceW(root, out uint sectors, out uint bytes, out _, out _)
            && sectors > 0 && bytes > 0)
        {
            return (long)sectors * bytes;
        }
        return 4096;
    }

    /// <summary>
    /// True only when both files share all their storage: one file record (hard links), or
    /// the same physical extents across their whole length. A first-block match is not
    /// enough: a clone overwritten in the middle still shares the head extent and is not
    /// a duplicate.
    /// </summary>
    public static bool AreLikelyClones(string pathA, string pathB) =>
        !string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase)
        && Families([pathA, pathB], p => p).Families.Count == 1;

    /// <summary>
    /// Splits files into clone families and files with storage of their own, opening each
    /// file once. Names of one file record are one family whatever their map can show
    /// (compressed, sparse or too fragmented to fetch); different records are one family only
    /// when their whole maps match, with no holes, on one volume (ReFS block clones).
    /// </summary>
    internal static (List<List<T>> Families, List<T> Alone) Families<T>(IEnumerable<T> files, Func<T, string> pathOf,
        CancellationToken cancellationToken = default)
    {
        // One unit per file record: its names, and one map for them all.
        var units = new List<(uint Volume, List<Extent>? Map, List<T> Names)>();
        var byRecord = new Dictionary<(uint Volume, ulong Record), List<T>>();
        var alone = new List<T>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (StorageOf(pathOf(file)) is not { } storage) { alone.Add(file); continue; }
            // A hard link counts two or more names. Without that, a shared id proves nothing:
            // FAT and some shares give every file the same one.
            var record = (storage.Volume, storage.Record);
            if (storage.Links > 1 && byRecord.TryGetValue(record, out var names)) { names.Add(file); continue; }
            List<T> unit = [file];
            if (storage.Links > 1) byRecord[record] = unit;
            units.Add((storage.Volume, storage.Extents, unit));
        }

        var families = new List<List<T>>();
        var byMap = new Dictionary<(uint Volume, List<Extent> Map), List<T>>(SameMap.Instance);
        foreach (var (volume, map, names) in units)
        {
            if (map is { Count: > 0 } && map.TrueForAll(e => e.DeviceOffset != -1))
            {
                if (byMap.TryGetValue((volume, map), out var family)) family.AddRange(names);
                else byMap[(volume, map)] = names;
            }
            else if (names.Count > 1) families.Add(names);
            else alone.Add(names[0]);
        }
        foreach (var family in byMap.Values)
        {
            if (family.Count > 1) families.Add(family);
            else alone.Add(family[0]);
        }
        return (families, alone);
    }

    /// <summary>Same volume, and every extent the same: file offset, device offset, length.</summary>
    private sealed class SameMap : IEqualityComparer<(uint Volume, List<Extent> Map)>
    {
        public static readonly SameMap Instance = new();

        public bool Equals((uint Volume, List<Extent> Map) a, (uint Volume, List<Extent> Map) b) =>
            a.Volume == b.Volume && a.Map.SequenceEqual(b.Map);

        public int GetHashCode((uint Volume, List<Extent> Map) key) =>
            HashCode.Combine(key.Volume, key.Map.Count, key.Map[0], key.Map[^1]);
    }
}
