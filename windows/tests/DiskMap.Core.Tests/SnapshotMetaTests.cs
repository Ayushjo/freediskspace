using System.Globalization;
using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// WIN-110: saved scans carry the Mac's sidecar notes (SnapshotMeta.swift) — name, note,
/// favourite, volume and scan figures — and list newest first for What changed.
/// </summary>
public class SnapshotMetaTests
{
    private static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");

    /// <summary>ICU puts a narrow no-break space before "PM"; Windows NLS a plain one.</summary>
    private static string Plain(string text) => text.Replace('\u202F', ' ').Replace('\u00A0', ' ');

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "freedisk-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DiskSnapshot Snap(string root, DateTimeOffset at)
    {
        var tree = new FileTree();
        int r = tree.AddNode("scan", -1, true, 0, 0, 0);
        tree.AddNode("a.bin", r, false, 1_000, 4_096, 1);
        return new DiskSnapshot(root, at, tree);
    }

    [Fact]
    public void MetaSitsBesideTheSnapshot()
    {
        string snapshot = Path.Combine("state", "snapshots", "1760000000-abc.snapshot");
        Assert.Equal(Path.Combine("state", "snapshots", "1760000000-abc.meta.json"), SnapshotStore.MetaPath(snapshot));
    }

    [Fact]
    public void SaveWritesNotesAndRecordsListNewestFirstForThatRoot()
    {
        string dir = TempDir();
        try
        {
            var now = DateTimeOffset.Now;
            string older = SnapshotStore.Save(Snap("C:\\scan", now.AddDays(-9)),
                new SnapshotMeta { Name = "Before Docker", Note = "before cleaning", ScannedBytes = 4_096, UsedBytes = 300 }, dir);
            string newer = SnapshotStore.Save(Snap("C:\\scan", now.AddDays(-1)),
                new SnapshotMeta { Name = "Yesterday", Favorite = true }, dir);
            SnapshotStore.Save(Snap("D:\\other", now), new SnapshotMeta { Name = "Other root" }, dir);

            Assert.True(File.Exists(SnapshotStore.MetaPath(older)));
            var records = SnapshotStore.Records(dir, "C:\\scan");
            Assert.Equal([newer, older], records.Select(r => r.Path).ToArray());
            Assert.Equal("Yesterday", records[0].Meta.Name);
            Assert.True(records[0].Meta.Favorite);
            Assert.Equal("before cleaning", records[1].Meta.Note);
            Assert.Equal(300, records[1].UsedBytes);
            Assert.All(records, r => Assert.False(r.IsCurrent));
            Assert.Equal(older, records[1].Id);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FavouriteRoundTripsThroughSaveMeta()
    {
        string dir = TempDir();
        try
        {
            string path = SnapshotStore.Save(Snap("C:\\scan", DateTimeOffset.Now), new SnapshotMeta { Name = "x" }, dir);
            var meta = SnapshotStore.LoadMeta(path);
            SnapshotStore.SaveMeta(meta with { Favorite = !meta.Favorite }, path);
            Assert.True(SnapshotStore.LoadMeta(path).Favorite);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingOrBrokenNotesFallBackToTheDefaultName()
    {
        string dir = TempDir();
        try
        {
            var at = DateTimeOffset.Now.AddDays(-30);
            string path = SnapshotStore.Save(Snap("C:\\scan", at), dir);
            Assert.Equal(SnapshotMeta.DefaultName(DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds())),
                SnapshotStore.LoadMeta(path).Name);
            File.WriteAllText(SnapshotStore.MetaPath(path), "{ not json");
            Assert.False(string.IsNullOrEmpty(SnapshotStore.LoadMeta(path).Name));
            Assert.Equal("", SnapshotStore.LoadMeta(path).Note);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadsAndWritesTheMacKeys()
    {
        // As the Mac's JSONEncoder writes it: camelCase keys, nil optionals left out.
        const string mac = """
            {"name":"Before cleaning","note":"Docker","favorite":true,"volumeName":"Macintosh HD",
             "totalBytes":494384795648,"freeBytes":120000000000,"usedBytes":374384795648,
             "scannedBytes":184000000000,"fileCount":1200000,"folderCount":300000,"scanSeconds":12.5,
             "diskMapVersion":"freedisk.space"}
            """;
        var meta = System.Text.Json.JsonSerializer.Deserialize<SnapshotMeta>(mac, SnapshotMeta.Json)!;
        Assert.Equal("Before cleaning", meta.Name);
        Assert.Equal(374_384_795_648UL, meta.UsedBytes);
        Assert.Equal(1_200_000, meta.FileCount);
        Assert.Equal(12.5, meta.ScanSeconds);

        string written = System.Text.Json.JsonSerializer.Serialize(new SnapshotMeta { Name = "n", ScannedBytes = 5 }, SnapshotMeta.Json);
        Assert.Contains("\"name\":\"n\"", written);
        Assert.Contains("\"note\":\"\"", written);
        Assert.Contains("\"favorite\":false", written);
        Assert.Contains("\"scannedBytes\":5", written);
        Assert.DoesNotContain("volumeName", written);
    }

    [Fact]
    public void DefaultNameSaysTodayOnlyOnTheDay()
    {
        var now = new DateTimeOffset(new DateTime(2026, 10, 23, 20, 51, 0, DateTimeKind.Local));
        Assert.Equal("Today — 8:51 PM", Plain(SnapshotMeta.DefaultName(now, now, US)));
        Assert.Equal("Oct 10, 2026 at 8:51 PM", Plain(SnapshotMeta.DefaultName(now.AddDays(-13), now, US)));
    }

    [Fact]
    public void DisplayNameReDerivesAStaleTodayName()
    {
        var captured = new DateTimeOffset(new DateTime(2026, 10, 10, 20, 51, 0, DateTimeKind.Local));
        var now = captured.AddDays(13);
        var header = new SnapshotHeader("C:\\scan", captured);
        var stale = new SnapshotRecord("a.snapshot", header, new SnapshotMeta { Name = "Today — 8:51 PM" }, false);
        Assert.Equal("Oct 10, 2026 at 8:51 PM", Plain(stale.DisplayNameAt(now, US)));
        var named = stale with { Meta = new SnapshotMeta { Name = "Before Docker" } };
        Assert.Equal("Before Docker", named.DisplayNameAt(now, US));
        var unnamed = stale with { Meta = new SnapshotMeta() };
        Assert.Equal("Oct 10, 2026 at 8:51 PM", Plain(unnamed.DisplayNameAt(now, US)));
    }

    [Fact]
    public void PickerLabelClipsLongNamesAndShowsUsedBytes()
    {
        var header = new SnapshotHeader("C:\\scan", DateTimeOffset.Now);
        var record = new SnapshotRecord("a.snapshot", header,
            new SnapshotMeta { Name = "A rather long scan name here", UsedBytes = 380_000_000_000 }, false);
        Assert.Equal("A rather long scan n… · 380 GB", record.PickerLabelAt(DateTimeOffset.Now, US));
        var current = new SnapshotRecord("", header, new SnapshotMeta { Name = "Today", ScannedBytes = 184_000_000_000 }, true);
        Assert.Equal("Current · 184 GB", current.PickerLabelAt(DateTimeOffset.Now, US));
        Assert.Equal(SnapshotRecord.CurrentId, current.Id);
    }

    [Fact]
    public void TotalFallsBackToUsedPlusFree()
    {
        var header = new SnapshotHeader("C:\\scan", DateTimeOffset.Now);
        var record = new SnapshotRecord("a", header, new SnapshotMeta { UsedBytes = 300, FreeBytes = 200 }, false);
        Assert.Equal(500, record.TotalBytes);
        Assert.Equal(200, record.FreeBytes);
        Assert.Equal(900, (record with { Meta = record.Meta with { TotalBytes = 900 } }).TotalBytes);
    }
}
