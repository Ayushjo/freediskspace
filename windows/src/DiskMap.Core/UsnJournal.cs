using System.Buffers.Binary;
using DiskMap.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core;

/// <summary>
/// The NTFS change journal (WIN-031): the durable record of every create,
/// delete, rename, and size change since a baseline scan. One
/// FSCTL_QUERY_USN_JOURNAL gives the marker a scan bookmarks; replaying
/// FSCTL_READ_USN_JOURNAL from it lists every touched file record — the
/// Windows counterpart of the macOS FSEvents incremental rescan.
///
/// Fallbacks (caller decides): journal id changed (deleted/recreated),
/// requested start already overwritten (first record's USN beyond our
/// marker), non-NTFS volume, or a change flood.
/// </summary>
public static class UsnJournal
{
    /// <summary>A journal cursor: id + where the next change lands.</summary>
    public readonly record struct Marker(long JournalId, long NextUsn);

    /// <summary>One changed record from the journal.</summary>
    public readonly record struct Entry(
        long Frn, long ParentFrn, string Name, uint Reason, long Usn, uint Attributes);

    public sealed record Changes(
        /// <summary>Every record read, in USN order.</summary>
        List<Entry> Entries,
        /// <summary>The journal cursor the replay ended at.</summary>
        Marker Marker,
        /// <summary>True when entries between the marker and the first read were overwritten — must not be trusted.</summary>
        bool Wrapped);

    /// <summary>The journal's identity + current cursor; null when the device has none.</summary>
    public static unsafe Marker? Query(SafeFileHandle volume)
    {
        var data = new Win32.USN_JOURNAL_DATA_V0();
        void* outBuf = &data;
        if (!Win32.DeviceIoControl(volume, Win32.FSCTL_QUERY_USN_JOURNAL,
                null, 0, outBuf, sizeof(Win32.USN_JOURNAL_DATA_V0), out _, IntPtr.Zero))
            return null;
        return new Marker(data.UsnJournalID, data.NextUsn);
    }

    /// <summary>
    /// Reads every journal record strictly after <paramref name="from"/>
    /// on journal <paramref name="journalId"/>. Wrapped when the window
    /// [from, firstReturned) was overwritten before we could read it; null
    /// when a read fails or returns a record <see cref="ParsePage"/> rejects.
    /// </summary>
    public static unsafe Changes? ReadAll(
        SafeFileHandle volume, Marker from, CancellationToken ct, int maxEntries = 2_000_000)
    {
        var input = new Win32.READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = from.NextUsn,
            ReasonMask = Win32.USN_REASON_ALL,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = (ulong)from.JournalId,
        };
        var entries = new List<Entry>();
        var buffer = new byte[64 * 1024];
        bool wrapped = false;
        long cursor = from.NextUsn;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            input.StartUsn = cursor;
            int bytes;
            fixed (byte* outBuf = buffer)
            {
                if (!Win32.DeviceIoControl(volume, Win32.FSCTL_READ_USN_JOURNAL,
                        &input, sizeof(Win32.READ_USN_JOURNAL_DATA_V0),
                        outBuf, buffer.Length, out bytes, IntPtr.Zero))
                    return null;   // call failed — let the caller decide
            }
            if (bytes <= 8) break;   // just the continuation USN — journal empty
            cursor = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8));
            int before = entries.Count;
            // A record we can't read is a change the replay would miss.
            if (!ParsePage(buffer.AsSpan(8, bytes - 8), entries)) return null;
            // The first record we get back carries a USN newer than our
            // marker when the journal overwrote what we meant to replay.
            if (before == 0 && entries.Count > 0 && entries[0].Usn > from.NextUsn + 1) wrapped = true;
            if (entries.Count >= maxEntries)
                return new Changes(entries, new Marker(from.JournalId, entries[^1].Usn), Wrapped: true);
            // A tail too short for a record means the read returned none.
            if (entries.Count == before) break;
        }
        return new Changes(entries, new Marker(from.JournalId, cursor), wrapped);
    }

    /// <summary>
    /// The USN_RECORD_V2s of one FSCTL_READ_USN_JOURNAL page (after its
    /// 8-byte continuation USN). False on any record shorter than the V2
    /// header, running past the page, or of another version — the replay
    /// must fail rather than skip it. A tail under 8 bytes is padding.
    /// </summary>
    internal static bool ParsePage(ReadOnlySpan<byte> page, List<Entry> entries)
    {
        int pos = 0;
        while (pos + 8 <= page.Length)
        {
            int recordLength = BinaryPrimitives.ReadInt32LittleEndian(page.Slice(pos));
            if (recordLength < Win32.UsnRecordMinLength || recordLength > page.Length - pos) return false;
            var rec = page.Slice(pos, recordLength);
            if (BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(4)) != Win32.UsnRecordVersion2) return false;
            long frn = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(8)) & Win32.UsnRecordMask;
            long parent = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(16)) & Win32.UsnRecordMask;
            long usn = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(24));
            uint reason = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(40));
            uint attrs = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(52));
            int nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(56));
            int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(58));
            string name = nameOffset + nameBytes <= rec.Length && nameBytes > 0
                ? System.Text.Encoding.Unicode.GetString(rec.Slice(nameOffset, nameBytes))
                : "";
            entries.Add(new Entry(frn, parent, name, reason, usn, attrs));
            pos += recordLength;
        }
        return true;
    }
}
