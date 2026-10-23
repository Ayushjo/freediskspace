using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-112: History &amp; Put Back (Mac: HistoryView, redesign board 10). Every cleanup
/// freedisk.space made, what is still in the Recycle Bin, and Put Back for each item or a
/// whole cleanup.
/// </summary>
public sealed class HistoryPage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly HashSet<int> _expanded = [];
    private bool _didSetDefaults;
    private string _query = "";
    private HashSet<string> _inTrash = [];
    private CleanupHistory _history = new();
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private TextBox? _search;

    public HistoryPage()
    {
        Background = Ui.Brush("RdPage");
        Content = _scroll;
        ModelEvents.WhileLoaded(this, Reload);
        Loaded += (_, _) => Model.StagedChanged += OnStaged;
        Unloaded += (_, _) => Model.StagedChanged -= OnStaged;
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != _class) Rebuild(); };
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Reload);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    /// <summary>Re-read the history and which Recycle Bin copies still exist, off the UI thread.</summary>
    private async void Reload()
    {
        var history = await Task.Run(() => CleanupHistory.LoadOrSeed());
        var paths = history.Entries.SelectMany(e => e.Record.Items.Select(i => i.TrashedPath)).ToList();
        var found = await Task.Run(() => paths.Where(CleanupHistory.ExistsInTrash).ToHashSet(StringComparer.OrdinalIgnoreCase));
        _history = history;
        _inTrash = found;
        if (!_didSetDefaults)
        {
            _didSetDefaults = true;
            // Open the cleanups you can still put back; the first one always.
            for (int i = 0; i < history.Entries.Count; i++)
            {
                var entry = history.Entries[i];
                if (i == 0 || entry.Record.Items.Any(item => CleanupHistory.State(item, entry, Exists) == CleanupHistory.ItemState.InTrash))
                    _expanded.Add(entry.Number);
            }
        }
        Rebuild();
    }

    private bool Exists(string path) => _inTrash.Contains(path);

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        bool searchFocused = _search?.IsKeyboardFocusWithin == true;
        int caret = _search?.CaretIndex ?? 0;
        double pad = _class.PagePadding();
        var stack = new StackPanel { Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, pad) };
        stack.Children.Add(Header());
        if (_history.Entries.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(24) };
            empty.Children.Add(Rd.Text("Nothing moved yet", 15, FontWeights.SemiBold));
            var why = Rd.Text("When you clean up, each cleanup appears here so you can put things back while they're in the Recycle Bin.", 13, null, "RdInk2", wrap: true);
            why.Margin = new Thickness(0, 6, 0, 0);
            empty.Children.Add(why);
            var card = Rd.Card(empty);
            card.Margin = new Thickness(0, _class.Gap(), 0, 0);
            stack.Children.Add(card);
        }
        else
        {
            var stats = Stats();
            stats.Margin = new Thickness(0, _class.Gap(), 0, 0);
            stack.Children.Add(stats);
            var timeline = Timeline();
            timeline.Margin = new Thickness(0, _class.Gap(), 0, 0);
            stack.Children.Add(timeline);
        }
        double offset = _scroll.VerticalOffset;
        _scroll.Content = stack;
        _scroll.ScrollToVerticalOffset(offset);
        if (searchFocused && _search is not null)
        {
            _search.Focus();
            _search.CaretIndex = Math.Min(caret, _search.Text.Length);
        }
    }

    // ---- Header and stats ----

    private FrameworkElement Header()
    {
        var dock = new DockPanel();
        if (_history.Entries.Count > 0)
        {
            var field = SearchField();
            field.Width = _class == Rd.WidthClass.Compact ? 200 : 260;
            field.VerticalAlignment = VerticalAlignment.Top;
            field.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(field, Dock.Right);
            dock.Children.Add(field);
        }
        var titles = new StackPanel();
        int deleted = _history.DeletedPermanentlyCount;
        var title = new TextBlock { FontFamily = Rd.FontFor(26), FontSize = Ui.Scaled(26), FontWeight = FontWeights.SemiBold };
        title.Inlines.Add(new System.Windows.Documents.Run($"Freed {ShellFormat.HeroBytes(_history.FreedBytes)} with freedisk.space") { Foreground = Ui.Brush("RdInk") });
        if (_class != Rd.WidthClass.Compact)
            title.Inlines.Add(new System.Windows.Documents.Run($" · {deleted:N0} {(deleted == 1 ? "folder" : "files")} deleted permanently") { Foreground = Ui.Brush("RdInk3") });
        titles.Children.Add(new Viewbox { Child = title, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        var lede = Rd.Text("Every cleanup goes to the Recycle Bin first. While it's there, Put Back returns it to the same place, with the same name.", 13, null, "RdInk2", wrap: true);
        lede.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(lede);
        dock.Children.Add(titles);
        return dock;
    }

    /// <summary>DiskMapSearchField: a quiet card field with a magnifier.</summary>
    private FrameworkElement SearchField()
    {
        var box = new TextBox
        {
            Text = _query, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontSize = Ui.Scaled(13), Foreground = Ui.Brush("RdInk"), CaretBrush = Ui.Brush("RdInk"),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Find a file you removed");
        var hint = new TextBlock
        {
            Text = "Find a file you removed", FontSize = Ui.Scaled(13), Foreground = Ui.Brush("RdInk3"),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
            Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) =>
        {
            _query = box.Text;
            hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Rebuild();
        };
        _search = box;
        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(box);
        var row = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
        var glass = Ui.Glyph(Icons.Search, 12, Ui.Brush("RdInk3"));
        glass.Margin = new Thickness(0, 0, 8, 0);
        glass.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(glass, Dock.Left);
        row.Children.Add(glass);
        row.Children.Add(field);
        return new Border
        {
            Height = 32, CornerRadius = new CornerRadius(8), Background = Ui.Brush("RdCard"),
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1), Child = row,
        };
    }

    private FrameworkElement Stats()
    {
        long trashBytes = _history.BytesInTrash(Exists);
        var first = _history.Entries.LastOrDefault()?.Record.Date;
        int deleted = _history.DeletedPermanentlyCount;
        var grid = new Grid();
        void column(FrameworkElement e)
        {
            if (grid.ColumnDefinitions.Count > 0)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                var line = new Border { Background = Ui.Brush("RdLine") };
                Grid.SetColumn(line, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(line);
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(e, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(e);
        }
        int count = _history.Entries.Count;
        column(Stat("Freed so far", ShellFormat.HeroBytes(_history.FreedBytes), $"in {count} cleanup{(count == 1 ? "" : "s")}",
            first is { } f ? $"Since you started on {DayText(f)}." : "", accent: false));
        // Only moves to the Recycle Bin — except the documented too-big regenerable folders.
        column(Stat("Deleted permanently", deleted.ToString("N0"), deleted == 1 ? "folder" : "files",
            deleted == 0 ? "Nothing skipped the Recycle Bin." : $"Too big for the Recycle Bin, regenerable, deleted with your OK · {ByteFormat.Format(_history.DeletedPermanentlyBytes)}.",
            accent: false));
        column(Stat("Still in Recycle Bin", ShellFormat.HeroBytes(trashBytes), trashBytes > 0 ? "can be put back" : "",
            trashBytes > 0 ? "Empty the Recycle Bin when you're sure." : "The Recycle Bin has been emptied since.", accent: trashBytes > 0));
        var layers = new Grid();
        layers.Children.Add(new Border { Background = Rd.Grid24() });
        layers.Children.Add(grid);
        return Rd.Surface(layers, "RdCard", 12);
    }

    private FrameworkElement Stat(string label, string value, string unit, string detail, bool accent)
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(_class == Rd.WidthClass.Compact ? 18 : 28, 22, _class == Rd.WidthClass.Compact ? 18 : 28, 22),
        };
        stack.Children.Add(Rd.Figure(label.ToUpperInvariant(), 10.5, FontWeights.Medium, "RdInk3"));
        double size = _class == Rd.WidthClass.Compact ? 32 : 44;
        var number = Rd.TightText(value, size, 0, accent ? "AppAccent" : "RdInk");
        var unitText = Rd.Text(unit, 13, null, "RdInk2");
        var line = Rd.BaselineRow(number, size, unitText, 13, 6);
        stack.Children.Add(new Viewbox
        {
            Child = line, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0),
        });
        var d = Rd.Text(detail, 13, null, "RdInk2", wrap: true);
        d.Margin = new Thickness(0, 8, 0, 0);
        d.MaxHeight = Ui.Scaled(13) * 2.8;
        stack.Children.Add(d);
        return stack;
    }

    // ---- Timeline ----

    private List<CleanupHistory.Entry> ShownEntries()
    {
        string q = _query.Trim().ToLowerInvariant();
        if (q.Length == 0) return _history.Entries;
        return _history.Entries.Select(e =>
        {
            var hits = e.Record.Items.Where(i => i.OriginalPath.ToLowerInvariant().Contains(q)).ToList();
            return hits.Count == 0 ? null : new CleanupHistory.Entry { Number = e.Number, Record = new CleanupRecord(e.Record.Date, hits), Restored = e.Restored };
        }).Where(e => e is not null).Cast<CleanupHistory.Entry>().ToList();
    }

    private FrameworkElement Timeline()
    {
        var entries = ShownEntries();
        var list = new StackPanel();
        if (entries.Count == 0)
        {
            var none = Rd.Text($"No removed file matches “{_query}”.", 13, null, "RdInk3");
            none.Margin = new Thickness(40, 0, 0, 0);
            list.Children.Add(none);
        }
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var row = new DockPanel { Margin = new Thickness(0, i > 0 ? 14 : 0, 0, 0) };
            var marker = new Ellipse
            {
                Width = 14, Height = 14, StrokeThickness = 1.5, Stroke = Ui.Brush("AppAccent"),
                Fill = i == 0 && _query.Length == 0 ? Ui.Brush("AppAccent") : Ui.Brush("RdCard"),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 22, 16, 0),
            };
            DockPanel.SetDock(marker, Dock.Left);
            row.Children.Add(marker);
            row.Children.Add(CleanupCard(entry, open: _query.Length > 0 || _expanded.Contains(entry.Number)));
            list.Children.Add(row);
        }
        // The dashed line the markers sit on.
        var line = new Line
        {
            X1 = 0, X2 = 0, Y1 = 0, Y2 = 1, Stretch = Stretch.Fill, Stroke = Rd.WithOpacity(Ui.Brush("RdInk3"), 0.5),
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 4 },
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(7, 28, 0, 28), Width = 1,
        };
        var layers = new Grid();
        layers.Children.Add(line);
        layers.Children.Add(list);
        return layers;
    }

    private FrameworkElement CleanupCard(CleanupHistory.Entry entry, bool open)
    {
        var items = entry.Record.Items.OrderByDescending(i => i.Bytes).ToList();
        var states = items.Select(i => CleanupHistory.State(i, entry, Exists)).ToList();
        var back = items.Where((_, k) => states[k] == CleanupHistory.ItemState.InTrash).ToList();
        long total = items.Sum(i => i.Bytes);
        var stack = new StackPanel();

        var head = new DockPanel { Height = 56, Margin = new Thickness(18, 0, 18, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var chevron = Ui.Glyph(open ? Icons.ChevronDown : "BsChevronRight", 10, Ui.Brush("RdInk3"));
        chevron.Width = 12;
        chevron.Margin = new Thickness(0, 0, 12, 0);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(chevron, Dock.Left);
        head.Children.Add(chevron);
        var size = Rd.Figure(ShellFormat.HeroBytes(total), 14, FontWeights.SemiBold);
        DockPanel.SetDock(size, Dock.Right);
        head.Children.Add(size);
        var when = Rd.Text(WhenText(entry.Record.Date), 15, FontWeights.SemiBold);
        DockPanel.SetDock(when, Dock.Left);
        head.Children.Add(when);
        var meta = Rd.Figure($"{items.Count} item{(items.Count == 1 ? "" : "s")} · cleanup #{entry.Number}".ToUpperInvariant(), 11, FontWeights.Medium, "RdInk3");
        meta.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(meta, Dock.Left);
        head.Children.Add(meta);
        var (tagText, accent) = CardTag(states);
        var tag = Rd.Tag(tagText, accent ? Rd.TagTone.Accent : Rd.TagTone.Neutral);
        tag.Margin = new Thickness(12, 0, 0, 0);
        tag.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(tag, Dock.Left);
        head.Children.Add(tag);
        head.Children.Add(new Border());
        head.MouseLeftButtonUp += (_, _) =>
        {
            if (!_expanded.Remove(entry.Number)) _expanded.Add(entry.Number);
            Rebuild();
        };
        System.Windows.Automation.AutomationProperties.SetName(head,
            $"Cleanup {entry.Number}, {WhenText(entry.Record.Date)}, {items.Count} items, {ByteFormat.Format(total)}");
        stack.Children.Add(head);

        if (open)
        {
            for (int k = 0; k < items.Count; k++) stack.Children.Add(ItemRow(items[k], states[k], entry.Number));
            if (back.Count > 0)
            {
                var foot = new DockPanel { Height = 56, Margin = new Thickness(18, 0, 18, 0) };
                var put = Rd.AccentButton(back.Count == items.Count ? $"Put Back all {back.Count}" : $"Put Back {back.Count} in Recycle Bin",
                    () => PutBack(back, entry.Number));
                put.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(put, Dock.Right);
                foot.Children.Add(put);
                foot.Children.Add(Rd.Text("Each item goes back to where it was, with the same name.", 13, null, "RdInk2"));
                stack.Children.Add(new Border
                {
                    Child = foot, Background = Rd.WithOpacity(Ui.Brush("RdSoft"), 0.5),
                    BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0),
                });
            }
        }
        return Rd.Surface(stack, "RdCard", 12);
    }

    private FrameworkElement ItemRow(CleanupRecord.Item item, CleanupHistory.ItemState state, int number)
    {
        bool gone = state == CleanupHistory.ItemState.TrashEmptied;
        bool compact = _class == Rd.WidthClass.Compact;
        var grid = new Grid { Margin = new Thickness(18, 0, 18, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 150 : 260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(compact ? 120 : 250) });
        string name = System.IO.Path.GetFileName(item.OriginalPath.TrimEnd('\\'));
        void put(FrameworkElement e, int c) { e.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(e, c); grid.Children.Add(e); }
        put(Rd.Text(name, 13.5, FontWeights.Medium, gone ? "RdInk3" : "RdInk"), 0);
        var where = Rd.Figure(CanonicalPath.DisplayPath(System.IO.Path.GetDirectoryName(item.OriginalPath) ?? ""), 11.5, null, "RdInk3");
        put(where, 2);
        var size = Rd.Figure(ShellFormat.HeroBytes(item.Bytes), 12, null, gone ? "RdInk3" : "RdInk");
        size.HorizontalAlignment = HorizontalAlignment.Right;
        put(size, 4);
        FrameworkElement status;
        switch (state)
        {
            case CleanupHistory.ItemState.InTrash:
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (!compact)
                {
                    var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Brush("AppAccent"), VerticalAlignment = VerticalAlignment.Center };
                    row.Children.Add(dot);
                    var t = Rd.Figure("IN RECYCLE BIN", 11, FontWeights.Medium, "RdAccentDeep");
                    t.Margin = new Thickness(6, 0, 12, 0);
                    row.Children.Add(t);
                }
                var button = Rd.SecondaryButton("Put Back", () => PutBack([item], number));
                System.Windows.Automation.AutomationProperties.SetName(button, $"Put back {name}");
                row.Children.Add(button);
                status = row;
                break;
            case CleanupHistory.ItemState.PutBack:
                status = Rd.Tag("Put back", Rd.TagTone.Accent);
                break;
            default:
                var emptied = new StackPanel { Orientation = Orientation.Horizontal };
                var lockIcon = Ui.Glyph("BsLock", 10, Ui.Brush("RdInk3"));
                lockIcon.VerticalAlignment = VerticalAlignment.Center;
                emptied.Children.Add(lockIcon);
                var label = Rd.Figure(compact ? "BIN EMPTIED" : "RECYCLE BIN EMPTIED · CAN'T RESTORE", 11, FontWeights.Medium, "RdInk3");
                label.Margin = new Thickness(6, 0, 0, 0);
                emptied.Children.Add(label);
                emptied.ToolTip = "The Recycle Bin was emptied after this cleanup, so there is nothing to put back.";
                status = emptied;
                break;
        }
        status.HorizontalAlignment = HorizontalAlignment.Right;
        put(status, 6);
        return new Border { Height = 50, Child = grid, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    private async void PutBack(List<CleanupRecord.Item> items, int number)
    {
        if (items.Count == 0) return;
        var report = await Task.Run(() => CleanupHistory.PutBackItems(items, number));
        Model.Toast(report.Skipped.Count == 0
            ? $"Put back {report.Restored.Count} item{(report.Restored.Count == 1 ? "" : "s")}"
            : $"Put back {report.Restored.Count} of {items.Count} — {report.Skipped[0].Reason}");
        if (report.Restored.Count > 0 && Model.RootPath is not null && !Model.IsScanning) _ = Model.RescanAsync();
        Reload();
    }

    // ---- Words ----

    /// <summary>The card's tag: ALL IN RECYCLE BIN, 2 IN RECYCLE BIN, PUT BACK, BIN EMPTIED.</summary>
    public static (string Text, bool Accent) CardTag(IReadOnlyList<CleanupHistory.ItemState> states)
    {
        int inTrash = states.Count(s => s == CleanupHistory.ItemState.InTrash);
        if (inTrash > 0) return (inTrash == states.Count ? "All in Recycle Bin" : $"{inTrash} in Recycle Bin", true);
        if (states.Count > 0 && states.All(s => s == CleanupHistory.ItemState.PutBack)) return ("Put back", false);
        return ("Bin emptied", false);
    }

    private static readonly CultureInfo British = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>"Today, 11:42", "Yesterday, 09:10", "Thursday, 18:05", "2 Oct".</summary>
    public static string WhenText(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var local = date.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime();
        int days = (today.Date - local.Date).Days;
        string time = local.ToString("HH:mm", British);
        return days switch
        {
            < 1 => "Today, " + time,
            1 => "Yesterday, " + time,
            <= 6 => local.ToString("dddd", British) + ", " + time,
            _ => DayText(date, now),
        };
    }

    /// <summary>"2 Oct", or "2 Oct 2025" in another year.</summary>
    public static string DayText(DateTimeOffset date, DateTimeOffset? now = null)
    {
        var local = date.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime();
        return local.ToString(local.Year == today.Year ? "d MMM" : "d MMM yyyy", British);
    }
}
