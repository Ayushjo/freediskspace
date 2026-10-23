using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>The cleanup history (Mac: CleanupHistory): numbering, put-back marks, states and totals.</summary>
public class CleanupHistoryTests
{
    private static CleanupRecord Record(DateTimeOffset date, params (string Name, long Bytes)[] items) =>
        new(date, items.Select(i => new CleanupRecord.Item($@"C:\u\{i.Name}", $@"C:\$Recycle.Bin\S-1\$R{i.Name}", i.Bytes)).ToList());

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"history-{Guid.NewGuid():N}.json");

    [Fact]
    public void CleanupsAreNumberedNewestFirst()
    {
        var h = new CleanupHistory();
        h.Append(Record(DateTimeOffset.Now.AddDays(-2), ("a", 10)));
        h.Append(Record(DateTimeOffset.Now, ("b", 20), ("c", 30)));
        h.Append(Record(DateTimeOffset.Now, []));    // empty records are not history
        Assert.Equal([2, 1], h.Entries.Select(e => e.Number));
        Assert.Equal(60, h.FreedBytes);
    }

    [Fact]
    public void TheOldestDropOffPastTheLimit()
    {
        var h = new CleanupHistory();
        for (int i = 0; i < CleanupHistory.Limit + 5; i++) h.Append(Record(DateTimeOffset.Now, ($"f{i}", 1)));
        Assert.Equal(CleanupHistory.Limit, h.Entries.Count);
        Assert.Equal(CleanupHistory.Limit + 5, h.Entries[0].Number);
        Assert.Equal(6, h.Entries[^1].Number);
    }

    [Fact]
    public void PutBackWinsThenTheRecycleBinDecides()
    {
        var h = new CleanupHistory();
        h.Append(Record(DateTimeOffset.Now, ("a", 10), ("b", 20), ("c", 30)));
        var entry = h.Entries[0];
        var items = entry.Record.Items;
        h.MarkRestored([items[0]], entry.Number);
        h.MarkRestored([items[0]], entry.Number);   // marking twice is harmless
        bool exists(string path) => path.EndsWith("$Rb");
        Assert.Equal(CleanupHistory.ItemState.PutBack, CleanupHistory.State(items[0], entry, exists));
        Assert.Equal(CleanupHistory.ItemState.InTrash, CleanupHistory.State(items[1], entry, exists));
        Assert.Equal(CleanupHistory.ItemState.TrashEmptied, CleanupHistory.State(items[2], entry, exists));
        Assert.Equal(50, h.FreedBytes);              // what came back is not freed
        Assert.Equal(20, h.BytesInTrash(exists));
        Assert.Single(entry.Restored);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        string path = TempFile();
        try
        {
            var h = new CleanupHistory { DeletedPermanentlyCount = 2, DeletedPermanentlyBytes = 99 };
            var date = new DateTimeOffset(2026, 10, 23, 1, 2, 3, TimeSpan.FromHours(5.5));
            h.Append(Record(date, ("a", 10)));
            h.MarkRestored(h.Entries[0].Record.Items, 1);
            h.Save(path);
            var back = CleanupHistory.Load(path);
            Assert.NotNull(back);
            Assert.Equal(date, back!.Entries[0].Record.Date);
            Assert.Equal(h.Entries[0].Record.Items, back.Entries[0].Record.Items);
            Assert.Equal(h.Entries[0].Restored, back.Entries[0].Restored);
            Assert.Equal(2, back.DeletedPermanentlyCount);
            Assert.Equal(99, back.DeletedPermanentlyBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CommitsAndPermanentDeletesAreRecorded()
    {
        string path = TempFile();
        try
        {
            CleanupHistory.RecordCommit(Record(DateTimeOffset.Now, ("a", 10)), path);
            CleanupHistory.RecordCommit(Record(DateTimeOffset.Now, ("b", 20)), path);
            CleanupHistory.RecordPermanentDelete(3, 300, path);
            CleanupHistory.RecordPermanentDelete(0, 50, path);   // nothing deleted: nothing counted
            var h = CleanupHistory.Load(path)!;
            Assert.Equal([2, 1], h.Entries.Select(e => e.Number));
            Assert.Equal(3, h.DeletedPermanentlyCount);
            Assert.Equal(300, h.DeletedPermanentlyBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMissingOrBrokenFileLoadsAsNothing()
    {
        Assert.Null(CleanupHistory.Load(TempFile()));
        string path = TempFile();
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Null(CleanupHistory.Load(path));
        }
        finally { File.Delete(path); }
        Assert.Single(CleanupHistory.Seeded(Record(DateTimeOffset.Now, ("a", 1))).Entries);
        Assert.Empty(CleanupHistory.Seeded(null).Entries);
    }
}
