using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-110: What changed (Mac: SnapshotsView.swift, redesign board 08). freedisk.space's own
/// saved scans as a chart, today against one of them, and the folders that grew and shrank.
/// Saving, favouriting and basket-staging a saved scan work as on the Mac; the Windows page's
/// Open (load a saved scan as the current one), Open a saved scan file… and Export live in the
/// "…" menu.
/// </summary>
public sealed class WhatChangedPage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private List<SnapshotRecord> _records = [];
    private string? _beforeId;
    private string? _afterId;
    private SnapshotComparison? _comparison;
    private List<SnapshotComparison.Entry> _hotspots = [];
    private (string Before, string After, object? Tree, SizeBasis Basis)? _comparedKey, _pendingKey;
    private CancellationTokenSource? _compareCts;
    private bool _isComparing;
    private string? _statusMessage;
    private bool _showAllGrew;
    private DateTimeOffset? _lastCleanup;
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private int _reloadGeneration;

    public WhatChangedPage()
    {
        Background = Ui.Brush("RdPage");
        Content = _scroll;
        ModelEvents.WhileLoaded(this, Reload);
        Unloaded += (_, _) =>
        {
            _compareCts?.Cancel();
            _isComparing = false;
            _pendingKey = null;
        };
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != _class) Rebuild(); };
    }

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    /// <summary>Saved scans live here (under the app's state folder, which a harness run can redirect).</summary>
    private static string StoreDirectory => SnapshotStore.DefaultDirectory();

    // ---- Records ----

    private List<SnapshotRecord> AllRecords
    {
        get
        {
            var list = new List<SnapshotRecord>(_records);
            if (CurrentRecord is { } current) list.Insert(0, current);
            return list;
        }
    }

    /// <summary>The live scan, as a record that isn't a file ("Today").</summary>
    private static SnapshotRecord? CurrentRecord
    {
        get
        {
            if (Model.RootPath is not { } root || Model.Tree is null) return null;
            return new SnapshotRecord("", new SnapshotHeader(root, DateTimeOffset.Now), CurrentMeta("Today", ""), IsCurrent: true);
        }
    }

    private static SnapshotMeta CurrentMeta(string name, string note, string? version = null)
    {
        var vol = Model.Volume;
        var counts = Model.Counts;
        return new SnapshotMeta
        {
            Name = name, Note = note,
            VolumeName = vol?.DriveLabel,
            TotalBytes = vol is { } v1 ? (ulong)Math.Max(0, v1.TotalBytes) : null,
            FreeBytes = vol is { } v2 ? (ulong)Math.Max(0, v2.FreeBytes) : null,
            UsedBytes = vol is { } v3 ? (ulong)Math.Max(0, v3.UsedBytes) : null,
            ScannedBytes = Model.Totals.Length > 0 ? Model.Totals[0] : null,
            FileCount = counts.Files is { Length: > 0 } files ? files[0] : null,
            FolderCount = counts.Folders is { Length: > 0 } folders ? folders[0] : null,
            ScanSeconds = Model.Elapsed,
            DiskMapVersion = version,
        };
    }

    private SnapshotRecord? Record(string? id) => id is null ? null : AllRecords.FirstOrDefault(r => r.Id == id);

    /// <summary>The chart measures what the scan saw, the same for saved and live scans.</summary>
    private static long Scanned(SnapshotRecord rec) => rec.Meta.ScannedBytes ?? rec.UsedBytes;

    private long? Delta =>
        Record(_beforeId) is { } before && Record(_afterId) is { } after ? Scanned(after) - Scanned(before) : null;

    // ---- Reload and compare ----

    private async void Reload()
    {
        int generation = ++_reloadGeneration;
        string? root = Model.RootPath;
        var (records, cleanup) = await Task.Run(() =>
        {
            var list = root is null ? [] : SnapshotStore.Records(StoreDirectory, root);
            DateTimeOffset? last = null;
            try { last = CleanupRecord.Load() is { Items.Count: > 0 } record ? record.Date : null; } catch { }
            return (list, last);
        });
        if (generation != _reloadGeneration) return;
        _records = records;
        _lastCleanup = cleanup;
        // Today against the most recent saved scan.
        if (_afterId is null || Record(_afterId) is null) _afterId = CurrentRecord?.Id ?? _records.FirstOrDefault()?.Id;
        if (_beforeId is null || Record(_beforeId) is null) _beforeId = _records.FirstOrDefault(r => r.Id != _afterId)?.Id;
        ScheduleCompare();
        Rebuild();
    }

    private bool CanCompare =>
        _beforeId is { } b && _afterId is { } a && b != a && Record(b) is not null && Record(a) is not null;

    private void SetSelection(string? before, string? after)
    {
        _beforeId = before;
        _afterId = after;
        ScheduleCompare();
        Rebuild();
    }

    /// <summary>Selection changes land here; a newer choice cancels an older compare.</summary>
    private async void ScheduleCompare()
    {
        if (!CanCompare)
        {
            _compareCts?.Cancel();
            _pendingKey = null;
            _comparison = null;
            _hotspots = [];
            _isComparing = false;
            return;
        }
        var beforeRec = Record(_beforeId)!;
        var afterRec = Record(_afterId)!;
        // A rescan changes "Today", so the live tree is part of what was compared.
        object? live = beforeRec.IsCurrent || afterRec.IsCurrent ? Model.Tree : null;
        var key = (_beforeId!, _afterId!, live, Model.SizeBasis);
        if (_comparedKey == key && _comparison is not null) return;
        if (_isComparing && _pendingKey == key) return;
        _pendingKey = key;
        _compareCts?.Cancel();
        var cts = new CancellationTokenSource();
        _compareCts = cts;
        var basis = Model.SizeBasis;
        DiskSnapshot? current = Model.Tree is { } tree && Model.RootPath is { } root
            ? new DiskSnapshot(root, DateTimeOffset.Now, tree) : null;
        _isComparing = true;
        _statusMessage = null;
        Rebuild();
        // Loading two trees and rolling both up takes seconds on a large drive, so it runs
        // off the UI thread.
        var result = await Task.Run(() =>
        {
            try
            {
                DiskSnapshot? load(SnapshotRecord rec) => rec.IsCurrent ? current : SnapshotStore.Load(rec.Path);
                if (load(beforeRec) is not { } before || cts.IsCancellationRequested) return null;
                if (load(afterRec) is not { } after || cts.IsCancellationRequested) return null;
                var built = new SnapshotComparison(before, after, basis);
                return ((SnapshotComparison, List<SnapshotComparison.Entry>)?)(built, built.Hotspots(built.DefaultMinimumChange, 60));
            }
            catch { return null; }
        });
        if (cts.IsCancellationRequested) return;
        _isComparing = false;
        if (result is not { } done)
        {
            _comparison = null;
            _hotspots = [];
            _statusMessage = "Could not read those scans";
        }
        else
        {
            (_comparison, _hotspots) = done;
            _comparedKey = key;
        }
        Rebuild();
    }

    // ---- Build ----

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        if (Model.Tree is null && _records.Count == 0)
        {
            _scroll.Content = null;
            Content = ExploreInspector.EmptyState(Icons.BarChart, "Scan first, then save scans",
                "Saved scans record sizes over time so you can see what grew. They don’t copy your files.", "RdPage");
            return;
        }
        if (!ReferenceEquals(Content, _scroll)) Content = _scroll;
        double pad = _class.PagePadding();
        double gap = _class.Gap();
        var stack = new StackPanel { Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, pad) };
        stack.Children.Add(Header());
        var chart = ChartCard();
        chart.Margin = new Thickness(0, gap, 0, 0);
        stack.Children.Add(chart);
        if (ChangeCards() is { } changes)
        {
            changes.Margin = new Thickness(0, gap, 0, 0);
            stack.Children.Add(changes);
        }
        if (_statusMessage is { } status)
        {
            var text = Rd.Text(status, 12, null, "RdInk3");
            text.Margin = new Thickness(0, gap, 0, 0);
            stack.Children.Add(text);
        }
        double offset = _scroll.VerticalOffset;
        _scroll.Content = stack;
        _scroll.ScrollToVerticalOffset(offset);
    }

    // ---- Header ----

    private FrameworkElement Header()
    {
        bool stacked = _class == Rd.WidthClass.Compact;
        var titles = new StackPanel();
        titles.Children.Add(new Viewbox
        {
            Child = Headline(), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        var lede = Rd.Text("Comparing your saved scans. These are freedisk.space's own scans, kept on this PC. Not File History.",
            13, null, "RdInk2", wrap: true);
        lede.MaxHeight = Math.Ceiling(Ui.Scaled(13) * Rd.TextLineHeight) * 2;
        lede.TextTrimming = TextTrimming.CharacterEllipsis;
        lede.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(lede);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        controls.Children.Add(ComparePicker());
        var save = Rd.PrimaryButton("Save this scan", PrepareSave);
        save.IsEnabled = Model.Tree is not null;
        save.Margin = new Thickness(10, 0, 0, 0);
        controls.Children.Add(save);
        var saved = SavedMenu();
        saved.Margin = new Thickness(10, 0, 0, 0);
        controls.Children.Add(saved);

        if (stacked)
        {
            var column = new StackPanel();
            column.Children.Add(titles);
            controls.Margin = new Thickness(0, 12, 0, 0);
            column.Children.Add(controls);
            return column;
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(titles);
        Grid.SetColumn(controls, 2);
        grid.Children.Add(controls);
        return grid;
    }

    /// <summary>"What changed · +18.6 GB since Thursday", or "What changed · nothing saved yet".</summary>
    private TextBlock Headline()
    {
        var title = new TextBlock
        {
            FontFamily = Rd.FontFor(26), FontSize = Ui.Scaled(26), FontWeight = FontWeights.SemiBold,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = Math.Ceiling(Ui.Scaled(26) * Rd.TextLineHeight),
        };
        title.Inlines.Add(new System.Windows.Documents.Run("What changed") { Foreground = Ui.Brush("RdInk") });
        if (Delta is not { } delta || Record(_beforeId) is not { } before)
        {
            title.Inlines.Add(new System.Windows.Documents.Run(" · nothing saved yet") { Foreground = Ui.Brush("RdInk3") });
            return title;
        }
        var now = Record(_afterId)?.Header.CapturedAt ?? DateTimeOffset.Now;
        title.Inlines.Add(new System.Windows.Documents.Run(" · ") { Foreground = Ui.Brush("RdInk3") });
        title.Inlines.Add(new System.Windows.Documents.Run(Signed(delta)) { Foreground = Ui.Brush(delta > 0 ? "AppAccent" : "RdInk") });
        title.Inlines.Add(new System.Windows.Documents.Run(" " + SinceText(before.Header.CapturedAt, now)) { Foreground = Ui.Brush("RdInk") });
        return title;
    }

    /// <summary>COMPARE | Today ⌄ vs Pick a scan ⌄ — 28 tall, radius 8, card fill, 1 px line.</summary>
    private FrameworkElement ComparePicker()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Background = Ui.Brush("RdSoft"), Padding = new Thickness(10, 0, 10, 0),
            Child = Rd.Figure("COMPARE", 10.5, FontWeights.Medium, "RdInk3"),
        });
        row.Children.Add(new Border { Width = 1, Background = Ui.Brush("RdLine") });
        row.Children.Add(RecordMenu(_afterId, Record(_afterId) is { } a ? ShortName(a) : "Today",
            id => SetSelection(_beforeId, id), "Compare"));
        var vs = Rd.Text("vs", 12, null, "RdInk3");
        row.Children.Add(vs);
        row.Children.Add(RecordMenu(_beforeId, Record(_beforeId) is { } b ? ShortName(b) : "Pick a scan",
            id => SetSelection(id, _afterId), "Against"));
        var surface = Rd.Surface(row, "RdCard", 8);
        surface.Height = 28;
        surface.HorizontalAlignment = HorizontalAlignment.Left;
        surface.VerticalAlignment = VerticalAlignment.Top;
        return surface;
    }

    private FrameworkElement RecordMenu(string? selection, string title, Action<string> pick, string role)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(Rd.Text(title, 13, FontWeights.Medium));
        var chevron = Ui.Glyph(Icons.ChevronDown, 9, Ui.Brush("RdInk3"));
        chevron.Margin = new Thickness(6, 1, 0, 0);
        label.Children.Add(chevron);
        var host = new Border
        {
            Padding = new Thickness(10, 0, 10, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Focusable = true, Child = label,
        };
        void open()
        {
            var menu = new ContextMenu { PlacementTarget = host, Placement = PlacementMode.Bottom };
            foreach (var rec in AllRecords)
            {
                string text = rec.IsCurrent ? "Today (this scan)" : rec.PickerLabel;
                var id = rec.Id;
                menu.Items.Add(Item((selection == id ? "✓  " : "     ") + text, () => pick(id), text));
            }
            menu.IsOpen = true;
        }
        host.MouseLeftButtonUp += (_, e) => { open(); e.Handled = true; };
        host.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space or Key.Down) { open(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(host, $"{role}: {title}");
        return host;
    }

    /// <summary>Favourite, open or remove saved scans; open a scan file; export this scan.</summary>
    private FrameworkElement SavedMenu()
    {
        Border? button = null;
        button = Rd.IconButton(Icons.More, "Saved scans", () =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            if (_records.Count == 0) menu.Items.Add(new MenuItem { Header = "No saved scans yet", IsEnabled = false });
            foreach (var rec in _records.Take(12))
            {
                var sub = new MenuItem { Header = Escape(rec.PickerLabel) };
                System.Windows.Automation.AutomationProperties.SetName(sub, rec.PickerLabel);
                sub.Items.Add(Item("Compare with today", () => SetSelection(rec.Id, CurrentRecord?.Id ?? _afterId)));
                sub.Items.Add(Item("Open this scan", () => _ = LoadAsync(rec.Path)));
                sub.Items.Add(Item(rec.Meta.Favorite ? "Unfavourite" : "Favourite", () => ToggleFavorite(rec)));
                sub.Items.Add(new Separator());
                sub.Items.Add(Item("Add to basket…", () => ConfirmStage(rec)));
                menu.Items.Add(sub);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Open a saved scan file…", LoadDialog));
            if (Model.Tree is not null)
                menu.Items.Add(Item("Export this scan…", () => (Window.GetWindow(this) as MainWindow)?.ExportScan()));
            menu.IsOpen = true;
        });
        button.VerticalAlignment = VerticalAlignment.Top;
        return button;
    }

    private static MenuItem Item(string header, Action action, string? name = null)
    {
        var item = new MenuItem { Header = Escape(header) };
        item.Click += (_, _) => action();
        System.Windows.Automation.AutomationProperties.SetName(item, name ?? header);
        return item;
    }

    /// <summary>MenuItem headers read "_" as an access key; a scan named "my_disk" keeps its underscore.</summary>
    private static string Escape(string text) => text.Replace("_", "__");

    private static string ShortName(SnapshotRecord rec) => rec.IsCurrent ? "Today" : ShortDate(rec.Header.CapturedAt);

    // ---- Chart ----

    private List<UsageChart.Point> ChartPoints()
    {
        var saved = _records.OrderBy(r => r.Header.CapturedAt).ToList();
        int keep = _class == Rd.WidthClass.Compact ? 5 : 7;
        var points = saved.Skip(Math.Max(0, saved.Count - keep)).Select(rec => new UsageChart.Point(
            rec.Id, ShortDate(rec.Header.CapturedAt).ToUpperInvariant(), Scanned(rec),
            rec.Id == _beforeId ? UsageChart.Role.Before : UsageChart.Role.Past,
            rec.Meta.Note.Length == 0 ? null : rec.Meta.Note)).ToList();
        if (CurrentRecord is { } current)
            points.Add(new UsageChart.Point(current.Id, "TODAY", Scanned(current),
                current.Id == _afterId ? UsageChart.Role.After : UsageChart.Role.Past, null));
        return points;
    }

    private FrameworkElement ChartCard()
    {
        var points = ChartPoints();
        var (grew, shrank) = Split(_hotspots);
        var stack = new StackPanel();

        var head = new DockPanel { LastChildFill = true };
        if (_comparison is not null && _class != Rd.WidthClass.Compact)
        {
            var stats = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            stats.Children.Add(Stat("Grew", Signed(grew.Sum(e => e.Delta)), accent: true));
            stats.Children.Add(Stat("Shrank", Signed(shrank.Sum(e => e.Delta)), accent: false));
            stats.Children.Add(Stat("Folders changed", (grew.Count + shrank.Count).ToString(CultureInfo.InvariantCulture), accent: false));
            stats.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(stats, Dock.Right);
            head.Children.Add(stats);
        }
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Rd.CardTitle("Scan size"));
        var last = Rd.Text($"last {points.Count} scan{(points.Count == 1 ? "" : "s")}", 12, null, "RdInk3");
        last.Margin = new Thickness(14, 0, 0, 0);
        titles.Children.Add(last);
        head.Children.Add(titles);
        stack.Children.Add(new Border
        {
            Height = 50, Padding = new Thickness(20, 0, 20, 0), Child = head,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
        });

        var chart = new UsageChart(points, id => { if (id != _afterId) SetSelection(id, _afterId); })
        {
            Height = _class switch { Rd.WidthClass.Compact => 230, Rd.WidthClass.Regular => 300, _ => 360 },
            Margin = new Thickness(20, 14, 20, 12),
        };
        stack.Children.Add(chart);
        if (_records.Count == 0)
        {
            var hint = Rd.Text("Save this scan now. Next time, this chart shows how your disk moved and the lists below say which folders did it.",
                13, null, "RdInk2", wrap: true);
            hint.Margin = new Thickness(20, 0, 20, 16);
            stack.Children.Add(hint);
        }
        return Rd.Card(stack);
    }

    private static FrameworkElement Stat(string label, string value, bool accent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) };
        row.Children.Add(Rd.Figure(label.ToUpperInvariant(), 11.5, FontWeights.Medium, "RdInk3"));
        var figure = Rd.Figure(value, 11.5, FontWeights.Medium, accent ? "RdAccentDeep" : "RdInk");
        figure.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(figure);
        return row;
    }

    // ---- Grew and shrank ----

    private FrameworkElement? ChangeCards()
    {
        if (_isComparing)
        {
            var loading = ExploreInspector.LoadingState("Comparing", "Reading both scans.");
            loading.MinHeight = 160;
            return loading;
        }
        if (_comparison is null) return null;
        var (grew, shrank) = Split(_hotspots);
        var grewCard = ChangeCard("Grew", grew, grew.Sum(e => e.Delta), accent: true,
            "Click a folder to open it in Explore.",
            grew.Count > 7 ? (_showAllGrew ? "Show fewer" : $"See all {grew.Count} →") : null,
            () => { _showAllGrew = !_showAllGrew; Rebuild(); });
        var shrankCard = ChangeCard("Shrank", shrank, shrank.Sum(e => e.Delta), accent: false,
            CleanupFooter(), _lastCleanup is not null ? "Open History →" : null, () => Model.ShowPage("Cleanup"));
        double gap = _class.Gap();
        if (_class == Rd.WidthClass.Compact)
        {
            var column = new StackPanel();
            column.Children.Add(grewCard);
            shrankCard.Margin = new Thickness(0, gap, 0, 0);
            column.Children.Add(shrankCard);
            return column;
        }
        // Grew takes the room; Shrank keeps to what its rows need, at most 520.
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ShrankWidth(shrank)) });
        grewCard.VerticalAlignment = VerticalAlignment.Top;
        shrankCard.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(grewCard);
        Grid.SetColumn(shrankCard, 2);
        grid.Children.Add(shrankCard);
        return grid;
    }

    /// <summary>
    /// The Shrank card's width in the side-by-side layout. SwiftUI gives Grew (layout priority 1)
    /// everything Shrank can spare, so Shrank sits at its rows' minimum: the 120 pt name column,
    /// the "before → after" and signed figures, the chevron and their spacing — at most 520.
    /// </summary>
    private double ShrankWidth(List<SnapshotComparison.Entry> shrank)
    {
        double before = 0, signed = 76;
        foreach (var entry in shrank.Take(25))
        {
            before = Math.Max(before, MeasureMono(BeforeAfter(entry.Before, entry.After), 11.5, FontWeights.Normal));
            signed = Math.Max(signed, MeasureMono(Signed(entry.Delta), 13, FontWeights.Medium));
        }
        double row = 10 + 120 + 14 + before + 14 + signed + 14 + 14 + 20;
        return Math.Min(520, Math.Max(shrank.Count == 0 ? 300 : 0, row));
    }

    private double MeasureMono(string text, double size, FontWeight weight)
    {
        var block = Rd.Figure(text, size, weight);
        block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(block.DesiredSize.Width);
    }

    private string CleanupFooter()
    {
        if (_lastCleanup is not { } cleanup || Record(_beforeId) is not { } before || cleanup <= before.Header.CapturedAt)
            return "Folders that got smaller since then.";
        return "Part of this is your cleanup " + Rd.RelativeTime(cleanup, DateTimeOffset.Now) + ".";
    }

    private FrameworkElement ChangeCard(string title, List<SnapshotComparison.Entry> entries, long total, bool accent,
        string footer, string? link, Action linkAction)
    {
        var shown = title == "Grew" && !_showAllGrew ? entries.Take(7).ToList() : entries.Take(25).ToList();
        var stack = new StackPanel();

        var head = new DockPanel { LastChildFill = true };
        var sum = Rd.Figure(Signed(total), 14, FontWeights.Medium, accent ? "AppAccent" : "RdInk");
        sum.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(sum, Dock.Right);
        head.Children.Add(sum);
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Rd.CardTitle(title));
        var count = Rd.Text($"{entries.Count} folder{(entries.Count == 1 ? "" : "s")}", 12, null, "RdInk3");
        count.Margin = new Thickness(10, 0, 0, 0);
        titles.Children.Add(count);
        head.Children.Add(titles);
        stack.Children.Add(new Border
        {
            Height = 50, Padding = new Thickness(20, 0, 20, 0), Child = head,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
        });

        if (shown.Count == 0)
        {
            var none = Rd.Text(title == "Grew" ? "Nothing grew noticeably." : "Nothing shrank noticeably.", 13, null, "RdInk3");
            none.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(new Border { MinHeight = 80, Child = none });
        }
        long maxDelta = Math.Abs(entries.FirstOrDefault()?.Delta ?? 1);
        foreach (var entry in shown)
            stack.Children.Add(ChangeRow(entry, accent, maxDelta));

        var foot = new DockPanel { LastChildFill = true };
        if (link is not null)
        {
            var button = Rd.Link(link, linkAction, 12, "RdAccentDeep", FontWeights.Normal);
            button.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            foot.Children.Add(button);
        }
        foot.Children.Add(Rd.Text(footer, 12, null, "RdInk2"));
        stack.Children.Add(new Border
        {
            Height = 46, Padding = new Thickness(20, 0, 20, 0), Child = foot,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0),
        });
        return Rd.Surface(stack, "RdCard", 12);
    }

    /// <summary>"CoreSimulator  ~\AppData\…   22.8 → 31.2 GB   +8.4 GB  ›" — 58 tall, opens in Explore.</summary>
    private FrameworkElement ChangeRow(SnapshotComparison.Entry entry, bool accent, long maxDelta)
    {
        bool compact = _class == Rd.WidthClass.Compact;
        string name = entry.Name.Length == 0 ? "Everything" : entry.Name;
        long delta = entry.Delta;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        if (!compact)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 76 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });

        // The name and where it is, over a bar for how big the change is against the biggest here.
        var names = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
        names.Children.Add(Rd.Text(name, 13.5, FontWeights.Medium));
        var path = Rd.Figure(DisplayPath(entry), 11, null, "RdInk3");
        path.Margin = new Thickness(0, 2, 0, 0);
        names.Children.Add(ExploreInspector.Fitted(path, 1, middle: true));
        var bar = new Border
        {
            Background = Ui.Brush(accent ? "RdAccentWash" : "RdSoft"), HorizontalAlignment = HorizontalAlignment.Left,
        };
        var block = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        double fraction = Math.Min(1, (double)Math.Abs(delta) / Math.Max(1, maxDelta));
        block.SizeChanged += (_, e) => bar.Width = e.NewSize.Width * fraction;
        block.Children.Add(bar);
        block.Children.Add(names);
        grid.Children.Add(block);

        int col = 1;
        TextBlock? figures = null, openHint = null;
        if (!compact)
        {
            var swap = new Grid { VerticalAlignment = VerticalAlignment.Center };
            figures = Rd.Figure(BeforeAfter(entry.Before, entry.After), 11.5, null, "RdInk3");
            figures.HorizontalAlignment = HorizontalAlignment.Right;
            openHint = Rd.Figure("Open in Explore →", 11.5, null, "RdAccentDeep");
            openHint.HorizontalAlignment = HorizontalAlignment.Right;
            openHint.Opacity = 0;
            swap.Children.Add(figures);
            swap.Children.Add(openHint);
            Grid.SetColumn(swap, 2);
            grid.Children.Add(swap);
            col = 3;
        }
        var signed = Rd.Figure(Signed(delta), 13, FontWeights.Medium, accent ? "RdAccentDeep" : "RdInk");
        signed.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(signed, col + 1);
        grid.Children.Add(signed);
        var chevron = Ui.Glyph("BsChevronRight", 10, Ui.Brush("RdInk3"));
        chevron.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(chevron, col + 3);
        grid.Children.Add(chevron);

        var hover = Rd.WithOpacity(Ui.Brush("RdAccentWash"), 0.4);
        var row = new Border
        {
            Height = 58, Padding = new Thickness(10, 0, 20, 0), Child = grid, Background = Brushes.Transparent,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand, Focusable = true,
        };
        void hovering(bool on)
        {
            row.Background = on ? hover : Brushes.Transparent;
            if (figures is not null && openHint is not null) { figures.Opacity = on ? 0 : 1; openHint.Opacity = on ? 1 : 0; }
        }
        row.MouseEnter += (_, _) => hovering(true);
        row.MouseLeave += (_, _) => hovering(false);
        row.MouseLeftButtonUp += (_, e) => { Open(entry); e.Handled = true; };
        row.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Open(entry); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(row,
            $"{name}, {Signed(delta)}, from {ByteFormat.Format(entry.Before)} to {ByteFormat.Format(entry.After)}");
        System.Windows.Automation.AutomationProperties.SetHelpText(row, "Open in Explore");
        return row;
    }

    private string DisplayPath(SnapshotComparison.Entry entry)
    {
        string abs = _comparison?.AbsolutePathOf(entry) ?? entry.Path;
        return Place(abs, Model.RootPath ?? "");
    }

    /// <summary>Opens a changed folder in Explore, if it is still there.</summary>
    private void Open(SnapshotComparison.Entry entry)
    {
        string path = _comparison?.AbsolutePathOf(entry) ?? entry.Path;
        if (Model.Tree is not { } tree || Model.RootPath is not { } root)
        {
            Model.Toast("This isn't on disk any more");
            return;
        }
        FileQuery.NodeLookup found;
        try { found = FileQuery.NodeAt(path, tree, root); }
        catch { found = FileQuery.NodeLookup.Missing; }
        if (found.Kind != FileQuery.NodeLookupKind.Found)
        {
            Model.Toast("This isn't on disk any more");
            return;
        }
        int id = found.Id;
        int folder = tree.IsDirectory[id] ? id : Math.Max(0, tree.Parent[id]);
        Model.DrillTo(folder);
        Model.Select(id);
        Model.ShowPage("Visualize");
    }

    // ---- Actions ----

    private void PrepareSave()
    {
        if (Model.Tree is null) return;
        SaveScanSheet.Show(Window.GetWindow(this), SnapshotMeta.DefaultName(DateTimeOffset.Now),
            Model.Tree.Count, Model.Totals.Length > 0 ? Model.Totals[0] : null, SaveSnapshot);
    }

    /// <summary>Saves the live scan with its name and note; true closes the sheet.</summary>
    private async Task<bool> SaveSnapshot(string name, string note)
    {
        if (Model.Tree is not { } tree || Model.RootPath is not { } root) return false;
        string trimmed = name.Trim();
        var meta = CurrentMeta(trimmed.Length == 0 ? SnapshotMeta.DefaultName(DateTimeOffset.Now) : trimmed, note.Trim(), "freedisk.space");
        try
        {
            await Task.Run(() => SnapshotStore.Save(new DiskSnapshot(root, DateTimeOffset.Now, tree), meta, StoreDirectory));
            Reload();
            Model.Toast("Scan saved");
            return true;
        }
        catch
        {
            _statusMessage = "Could not save this scan";
            Rebuild();
            return false;
        }
    }

    private void ConfirmStage(SnapshotRecord rec)
    {
        if (rec.IsCurrent) return;
        var files = new[] { rec.Path, SnapshotStore.MetaPath(rec.Path) }.Where(File.Exists).ToList();
        var items = files.Select(f => new Dialogs.ConfirmItem(Path.GetFileName(f), f, SizeOf(f))).ToList();
        if (!Dialogs.Confirm(Window.GetWindow(this), "Add this saved scan to the basket?",
                "Only the saved scan and its notes go in the basket; you review them before anything moves to the Recycle Bin. Your files are not affected.",
                items, "Add to basket"))
            return;
        int added = 0;
        foreach (var item in items)
            if (Model.StagePath(item.Path, item.Bytes, $"Saved scan: {rec.DisplayName}", notify: false)) added++;
        Model.Toast(added > 0 ? "Saved scan added to the basket" : "That saved scan is already in the basket");
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private void ToggleFavorite(SnapshotRecord rec)
    {
        try { SnapshotStore.SaveMeta(rec.Meta with { Favorite = !rec.Meta.Favorite }, rec.Path); } catch { }
        Reload();
    }

    private async void LoadDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a saved scan",
            Filter = "freedisk.space saved scans (*.snapshot)|*.snapshot",
        };
        if (Directory.Exists(StoreDirectory)) dlg.InitialDirectory = StoreDirectory;
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) await LoadAsync(dlg.FileName);
    }

    /// <summary>Makes a saved scan the current one (no rescan).</summary>
    private async Task LoadAsync(string path)
    {
        try
        {
            var snapshot = await Task.Run(() => SnapshotStore.Load(path));
            await Model.LoadSnapshot(snapshot);
        }
        catch
        {
            Model.Toast("Could not read that scan");
        }
    }

    // ---- Words ----

    private static readonly CultureInfo British = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>"+18.6 GB", "−4.3 GB", "±0".</summary>
    public static string Signed(long bytes) =>
        bytes == 0 ? "±0" : (bytes > 0 ? "+" : "−") + ShellFormat.HeroBytes(Math.Abs(bytes));

    /// <summary>"since yesterday", "since Thursday", "since last week", "since 10 Oct".</summary>
    public static string SinceText(DateTimeOffset date, DateTimeOffset now)
    {
        int days = (now.ToLocalTime().Date - date.ToLocalTime().Date).Days;
        return days switch
        {
            < 1 => "since earlier today",
            1 => "since yesterday",
            <= 6 => "since " + date.ToLocalTime().ToString("dddd", British),
            <= 13 => "since last week",
            _ => "since " + ShortDate(date),
        };
    }

    /// <summary>"10 Oct".</summary>
    public static string ShortDate(DateTimeOffset date) => date.ToLocalTime().ToString("d MMM", British);

    /// <summary>Hotspots into what grew (biggest first) and what shrank (biggest drop first).</summary>
    public static (List<SnapshotComparison.Entry> Grew, List<SnapshotComparison.Entry> Shrank) Split(
        IEnumerable<SnapshotComparison.Entry> entries)
    {
        var list = entries.ToList();
        return (list.Where(e => e.Delta > 0).OrderByDescending(e => e.Delta).ToList(),
                list.Where(e => e.Delta < 0).OrderBy(e => e.Delta).ToList());
    }

    /// <summary>"22.8 → 31.2 GB", both in the larger figure's unit.</summary>
    public static string BeforeAfter(long before, long after)
    {
        long big = Math.Max(before, after);
        var (div, unit) = big >= 1_000_000_000 ? (1e9, "GB") : big >= 1_000_000 ? (1e6, "MB") : (1e3, "KB");
        return $"{ShellFormat.Fixed(before / div, 1)} → {ShellFormat.Fixed(after / div, 1)} {unit}";
    }

    /// <summary>
    /// "~\AppData\Local\Docker" under the home folder; otherwise from the scanned folder
    /// ("scan\Movies\Holiday.mov") (Mac: DuplicatesView.place).
    /// </summary>
    public static string Place(string path, string root, string? home = null)
    {
        string display = CanonicalPath.DisplayPath(path, home);
        if (display.StartsWith('~')) return display;
        string b = root.TrimEnd('\\', '/');
        if (b.Length == 0 || path.Length <= b.Length
            || !path.StartsWith(b, StringComparison.OrdinalIgnoreCase) || path[b.Length] is not ('\\' or '/'))
            return display;
        string name = Path.GetFileName(b);
        return (name.Length > 0 ? name : b) + path[b.Length..];
    }
}

/// <summary>
/// Bars for each saved scan, today in ink with what it grew hatched on top (Mac: UsageChart).
/// Click a bar to compare today with it.
/// </summary>
public sealed class UsageChart : Grid
{
    public enum Role { Past, Before, After }
    public sealed record Point(string Id, string Label, long Bytes, Role Role, string? Note);

    private const double Left = 44, Bottom = 26, Top = 26;

    public UsageChart(IReadOnlyList<Point> points, Action<string> pick)
    {
        Children.Add(new Plot(points));
        // One click target per bar, across the plot's width.
        var targets = new Grid { Margin = new Thickness(Left, 0, 0, 0) };
        foreach (var point in points)
        {
            targets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var target = new Border
            {
                Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true,
                ToolTip = point.Role == Role.After ? "Today" : "Compare today with this scan",
            };
            string id = point.Id;
            target.MouseLeftButtonUp += (_, e) => { pick(id); e.Handled = true; };
            target.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { pick(id); e.Handled = true; } };
            System.Windows.Automation.AutomationProperties.SetName(target, $"{point.Label}, {ByteFormat.Format(point.Bytes)}");
            Grid.SetColumn(target, targets.ColumnDefinitions.Count - 1);
            targets.Children.Add(target);
        }
        Children.Add(targets);
    }

    /// <summary>Axis bounds and step, in the chart's unit, for a few round grid lines.</summary>
    public static (double Low, double High, double Step) Axis(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (0, 1, 1);
        double lo = values.Min(), hi = values.Max();
        double span = Math.Max(Math.Max(hi - lo, hi * 0.08), 1e-9);
        double raw = span / 4;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1.0, 2, 5, 10 }.Select(m => m * magnitude).FirstOrDefault(s => s >= raw, magnitude * 10);
        double low = Math.Max(0, Math.Floor((lo - span * 0.35) / step) * step);
        double high = Math.Ceiling((hi + span * 0.15) / step) * step;
        return (low, Math.Max(high, low + step), step);
    }

    /// <summary>GB when anything is a gigabyte or more, otherwise MB.</summary>
    public static (double Divisor, string Name) Unit(IReadOnlyList<Point> points) =>
        (points.Count == 0 ? 0 : points.Max(p => p.Bytes)) >= 1_000_000_000 ? (1e9, "GB") : (1e6, "MB");

    /// <summary>"184", "12.4".</summary>
    public static string Figure(double value) =>
        value >= 100 ? Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
            : ShellFormat.Fixed(value, 1);

    private sealed class Plot(IReadOnlyList<Point> points) : FrameworkElement
    {
        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth, height = ActualHeight;
            if (width <= 0 || height <= 0) return;
            var unit = Unit(points);
            var values = points.Select(p => p.Bytes / unit.Divisor).ToList();
            var axis = Axis(values);
            var plot = new Rect(Left, Top, Math.Max(10, width - Left), Math.Max(10, height - Top - Bottom));
            double slot = plot.Width / Math.Max(points.Count, 1);
            double barWidth = Math.Min(86, slot * 0.46);
            double y(double v) => plot.Bottom - (v - axis.Low) / (axis.High - axis.Low) * plot.Height;
            double? before = points.FirstOrDefault(p => p.Role == Role.Before) is { } b ? b.Bytes / unit.Divisor : null;

            var ink = Ui.Brush("RdInk");
            var ink2 = Ui.Brush("RdInk2");
            var ink3 = Ui.Brush("RdInk3");
            var accent = Ui.Brush("AppAccent");
            var accentDeep = Ui.Brush("RdAccentDeep");

            var gridPen = new Pen(Ui.Brush("RdLine"), 1);
            for (double v = axis.Low; v <= axis.High + axis.Step / 2; v += axis.Step)
            {
                dc.DrawLine(gridPen, new System.Windows.Point(plot.Left, y(v)), new System.Windows.Point(plot.Right, y(v)));
                Draw(dc, Figure(v), 11, FontWeights.Normal, ink3, plot.Left - 8, y(v), 1, true);
            }
            dc.DrawLine(new Pen(Rd.WithOpacity(ink, 0.8), 1), new System.Windows.Point(plot.Left, plot.Bottom),
                new System.Windows.Point(plot.Right, plot.Bottom));
            Draw(dc, unit.Name + " SCANNED", 10.5, FontWeights.Medium, ink3, plot.Left, 6, 0, true);

            var hatch = Rd.HatchBrush(Rd.WithOpacity(accent, 0.55), Ui.Brush("RdAccentWash"), 7, 1.5);
            for (int i = 0; i < points.Count; i++)
            {
                var point = points[i];
                double value = values[i];
                double cx = plot.Left + slot * (i + 0.5);
                double topY = y(value);
                var fill = point.Role switch
                {
                    Role.After => ink,
                    Role.Before => Rd.WithOpacity(ink, 0.5),
                    _ => Rd.WithOpacity(ink, 0.17),
                };
                if (point.Role == Role.After && before is { } bv && value > bv)
                {
                    dc.DrawRectangle(fill, null, new Rect(cx - barWidth / 2, y(bv), barWidth, plot.Bottom - y(bv)));
                    var grown = new Rect(cx - barWidth / 2, topY, barWidth, y(bv) - topY);
                    dc.DrawRectangle(hatch, new Pen(accent, 1), grown);
                    Draw(dc, "+" + Figure(value - bv), 12, FontWeights.SemiBold, accentDeep, cx - barWidth / 2 - 8, y(bv) - 10, 1, true);
                }
                else
                {
                    dc.DrawRectangle(fill, null, new Rect(cx - barWidth / 2, topY, barWidth, Math.Max(0, plot.Bottom - topY)));
                }
                Draw(dc, Figure(value), 12, point.Role == Role.After ? FontWeights.SemiBold : FontWeights.Normal,
                    point.Role == Role.Past ? ink2 : ink, cx, topY - 10, 0.5, true);
                if (point.Note is { } note)
                    Draw(dc, note.ToLowerInvariant(), 11, FontWeights.Normal, ink3, cx, topY - 28, 0.5, false);
                Draw(dc, point.Label, 11, point.Role == Role.Past ? FontWeights.Normal : FontWeights.SemiBold,
                    point.Role == Role.Past ? ink3 : ink, cx, plot.Bottom + 14, 0.5, true);
            }
            // A dashed line at the comparison height, from that bar to today.
            int bi = points.ToList().FindIndex(p => p.Role == Role.Before);
            int ai = points.ToList().FindIndex(p => p.Role == Role.After);
            if (before is { } level && bi >= 0 && ai > bi)
            {
                var dash = new Pen(Rd.WithOpacity(accentDeep, 0.8), 1) { DashStyle = new DashStyle([3, 3], 0) };
                dc.DrawLine(dash, new System.Windows.Point(plot.Left + slot * (bi + 0.5) + barWidth / 2, y(level)),
                    new System.Windows.Point(plot.Left + slot * (ai + 0.5) - barWidth / 2, y(level)));
            }
        }

        /// <summary>Text anchored like Canvas.draw(at:anchor:): <paramref name="ax"/> 0 leading, 0.5 centre, 1 trailing; centred vertically.</summary>
        private void Draw(DrawingContext dc, string text, double size, FontWeight weight, Brush brush,
            double x, double y, double ax, bool mono)
        {
            var typeface = new Typeface(mono ? Mono : Rd.FontFor(size), FontStyles.Normal, weight, FontStretches.Normal);
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
                size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(formatted, new System.Windows.Point(x - formatted.WidthIncludingTrailingWhitespace * ax, y - formatted.Height / 2));
        }
    }
}

/// <summary>Save this scan: a name, an optional note, what is kept (Mac: SaveScanSheet, 500 wide).</summary>
internal sealed class SaveScanSheet : Window
{
    private SaveScanSheet() { }

    public static void Show(Window? owner, string defaultName, int? itemCount, long? bytes, Func<string, string, Task<bool>> onSave)
    {
        var sheet = new SaveScanSheet
        {
            Title = "Save this scan",
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Ui.Brush("RdPage"),
        };
        var stack = new StackPanel { Width = 500 - 52 };

        var head = new DockPanel();
        var tile = Rd.IconTile(Icons.BarChart, 40);
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var words = new StackPanel();
        words.Children.Add(Rd.Text("Save this scan", 20, FontWeights.SemiBold));
        var lede = Rd.Text("Keeps the sizes from this scan so What changed can compare next time. It doesn’t copy or back up your files.",
            13, null, "RdInk2", wrap: true);
        lede.Margin = new Thickness(0, 6, 0, 0);
        words.Children.Add(lede);
        head.Children.Add(words);
        stack.Children.Add(head);

        var name = Field("Name", defaultName, "", multiline: false, out var nameBox);
        name.BorderBrush = Ui.Brush("RdLine");
        name.BorderThickness = new Thickness(0, 0, 0, 1);
        var note = Field("Note · optional", "", "e.g. Before cleaning Docker", multiline: true, out var noteBox);
        var fields = new StackPanel();
        fields.Children.Add(name);
        fields.Children.Add(note);
        var card = Rd.Card(fields);
        card.Margin = new Thickness(0, 18, 0, 0);
        stack.Children.Add(card);

        if (itemCount is { } count)
        {
            var row = Rd.LeaderRow("Folders and files", count.ToString("N0", CultureInfo.GetCultureInfo("en-US")));
            row.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(row);
        }
        if (bytes is { } size)
        {
            var row = Rd.LeaderRow("Size of this scan", ShellFormat.HeroBytes(size));
            row.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(row);
        }

        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = true };
        bool saving = false;
        var save = Rd.AccentButton("Save", null, large: true);
        save.IsDefault = true;
        save.Click += async (_, _) =>
        {
            if (saving) return;
            saving = true;
            save.IsEnabled = false;
            bool ok = await onSave(nameBox.Text, noteBox.Text);
            saving = false;
            save.IsEnabled = true;
            if (ok) sheet.Close();
        };
        DockPanel.SetDock(save, Dock.Right);
        buttons.Children.Add(save);
        var cancel = Rd.SecondaryButton("Cancel", () => sheet.Close(), large: true);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(cancel);
        buttons.Children.Add(Rd.Text("Kept on this PC only.", 12, null, "RdInk3"));
        stack.Children.Add(buttons);

        sheet.Content = new Border { Padding = new Thickness(26), Child = stack };
        sheet.Loaded += (_, _) => { nameBox.Focus(); nameBox.SelectAll(); };
        sheet.ShowDialog();
    }

    /// <summary>A label over a plain field, 14 padding; the hint is drawn when the field is empty.</summary>
    private static Border Field(string label, string text, string hint, bool multiline, out TextBox box)
    {
        var stack = new StackPanel();
        stack.Children.Add(Rd.Text(label, 12, null, "RdInk3"));
        var input = new TextBox
        {
            Text = text, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontSize = Ui.Scaled(13), Foreground = Ui.Brush("RdInk"), CaretBrush = Ui.Brush("RdInk"),
            Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinLines = multiline ? 2 : 1, MaxLines = multiline ? 4 : 1,
        };
        System.Windows.Automation.AutomationProperties.SetName(input, label == "Name" ? "Name" : "Note");
        var layers = new Grid();
        if (hint.Length > 0)
        {
            var placeholder = Rd.Text(hint, 13, null, "RdInk3");
            placeholder.IsHitTestVisible = false;
            placeholder.VerticalAlignment = VerticalAlignment.Top;
            placeholder.Margin = new Thickness(0, 6, 0, 0);
            placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            input.TextChanged += (_, _) => placeholder.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            layers.Children.Add(placeholder);
        }
        layers.Children.Add(input);
        stack.Children.Add(layers);
        box = input;
        return new Border { Padding = new Thickness(14), Child = stack };
    }
}
