using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-106: Clean up (Mac: CleanUpView.swift, redesign board 02). One list, already ticked
/// with the things that rebuild by themselves, beside a receipt of exactly what happens. The
/// big button stages the ticked items and opens the basket, where the move to the Recycle
/// Bin is confirmed — staging and committing stay two separate actions.
/// </summary>
public sealed class CleanUpPage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    public enum CleanGroup { Rebuilds, ReviewFirst, Downloads, Media }

    /// <summary>One row: what it is, why it's here, its size, and the folders/files it stages.</summary>
    public sealed record CleanItem(string Id, CleanGroup Group, string Title, string Why, long Bytes,
        IReadOnlyList<(int NodeId, string Path)> Targets, string RevealPath);

    private readonly Grid _layout = new();
    private readonly ScrollViewer _listScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly ScrollViewer _receiptScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly HashSet<string> _ticked = [];
    private readonly HashSet<CleanGroup> _expanded = [];
    private object? _tickedFor;
    private bool _isMoving;
    private Rd.WidthClass _class = Rd.WidthClass.Regular;

    public CleanUpPage()
    {
        Background = Ui.Brush("RdPage");
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
        _layout.Children.Add(_listScroll);
        var line = new Border { Background = Ui.Brush("RdLine") };
        Grid.SetColumn(line, 1);
        _layout.Children.Add(line);
        var receiptHost = new Grid { Background = Ui.Brush("RdSidebar") };
        receiptHost.Children.Add(new Border { Background = Rd.Grid24(), Opacity = 0.8 });
        receiptHost.Children.Add(_receiptScroll);
        Grid.SetColumn(receiptHost, 2);
        _layout.Children.Add(receiptHost);
        Content = _layout;
        ModelEvents.WhileLoaded(this, Rebuild);
        Loaded += (_, _) => Model.StagedChanged += OnStaged;
        Unloaded += (_, _) => Model.StagedChanged -= OnStaged;
        SizeChanged += (_, _) =>
        {
            if (Rd.ClassFor(WindowWidth) != _class) Rebuild();
        };
        // Return moves the ticked items (.keyboardShortcut(.defaultAction)).
        Focusable = true;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.OriginalSource is not TextBox && Keyboard.Modifiers == ModifierKeys.None)
            {
                MoveTicked();
                e.Handled = true;
            }
        };
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    private bool Ready => Model.Reviewables is not null && Model.OldDownloads is not null && Model.LargeMedia is not null;

    private List<CleanItem> Items() => BuildItems(Model.Reviewables ?? [], Model.Totals,
        DownloadItems(Model.OldDownloads?.Candidates ?? [], Model.RootPath),
        MediaItems(Model.LargeMedia?.Candidates ?? [], Model.RootPath));

    /// <summary>
    /// Old downloads older than 30 days — the Old downloads page's default, so the group's
    /// "Not opened in a long time" holds and its count matches the page.
    /// </summary>
    public static List<CleanItem> DownloadItems(IReadOnlyList<OldDownloadsCandidate> candidates, string? root) =>
        OldDownloadsCatalog.Filter(candidates, OldDownloadsAgeFilter.Days30, OldDownloadsSizeFilter.Any, OldDownloadsTypeFilter.All, "")
            .Where(c => c.Safety.Level != SafetyLevel.Protected)
            .Select(c => new CleanItem($"d-{c.NodeID}", CleanGroup.Downloads, c.Name,
                $"{Age(c.AgeDays)} · {Place(c.AbsolutePath, root)}", c.Bytes, [(c.NodeID, c.AbsolutePath)], c.AbsolutePath))
            .ToList();

    public static List<CleanItem> MediaItems(IReadOnlyList<MediaCandidate> candidates, string? root) =>
        candidates.Where(m => m.Safety.Level != SafetyLevel.Protected)
            .Select(m => new CleanItem($"m-{m.NodeID}", CleanGroup.Media, m.Name,
                $"{Age(m.AgeDays)} · {Place(m.AbsolutePath, root)}", m.Bytes, [(m.NodeID, m.AbsolutePath)], m.AbsolutePath))
            .ToList();

    /// <summary>The folder a file sits in, relative to the scan ("Downloads\\misc"), else its display path.</summary>
    public static string Place(string absolutePath, string? root)
    {
        string parent = System.IO.Path.GetDirectoryName(absolutePath) ?? absolutePath;
        if (root is { Length: > 0 })
        {
            string r = root.TrimEnd('\\', '/');
            if (parent.Equals(r, StringComparison.OrdinalIgnoreCase))
                return System.IO.Path.GetFileName(r) is { Length: > 0 } name ? name : r;
            if (parent.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                return parent[(r.Length + 1)..];
        }
        return CanonicalPath.DisplayPath(parent);
    }

    /// <summary>"today", "9 days old", "5 weeks old", "5 months old", "2 years old".</summary>
    public static string Age(int days)
    {
        if (days < 1) return "today";
        if (days < 14) return $"{days} days old";
        if (days < 60) return $"{days / 7} weeks old";
        if (days < 730) return $"{days / 30} months old";
        return $"{days / 365} years old";
    }

    /// <summary>
    /// Every Clean up row, biggest first. Protected items never appear; a file that is both an
    /// old download and large media is listed once (downloads).
    /// </summary>
    public static List<CleanItem> BuildItems(IReadOnlyList<ReviewableTarget> reviewables, long[] totals,
        IReadOnlyList<CleanItem> downloads, IReadOnlyList<CleanItem> media)
    {
        var output = new List<CleanItem>();
        foreach (var t in reviewables.Where(t => !t.IsProtected))
        {
            var targets = new List<(int, string)>();
            for (int i = 0; i < t.Paths.Count; i++)
                targets.Add((i < t.NodeIds.Count ? t.NodeIds[i] : -1, t.Paths[i]));
            string places = t.Paths.Count > 1 ? $"{t.Paths.Count} places · " : "";
            output.Add(new CleanItem("r-" + t.Id, t.IsGenerallySafe ? CleanGroup.Rebuilds : CleanGroup.ReviewFirst,
                t.DisplayName, places + (t.Consequence.Length == 0 ? t.Detail : t.Consequence), t.Bytes,
                targets, t.PrimaryPath));
        }
        output.AddRange(downloads);
        var downloadIds = downloads.SelectMany(d => d.Targets.Select(x => x.NodeId)).ToHashSet();
        output.AddRange(media.Where(m => !m.Targets.Any(x => downloadIds.Contains(x.NodeId))));
        return output.Where(i => i.Bytes > 0).OrderByDescending(i => i.Bytes).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>What Clean up ticks by itself: only what rebuilds by itself, and nothing already in the basket.</summary>
    public static HashSet<string> DefaultTicks(IEnumerable<CleanItem> items, Func<string, bool> isStaged, bool tickRebuilds) =>
        tickRebuilds
            ? items.Where(i => i.Group == CleanGroup.Rebuilds && !isStaged(i.RevealPath)).Select(i => i.Id).ToHashSet()
            : [];

    public static string GroupTitle(CleanGroup g) => g switch
    {
        CleanGroup.Rebuilds => "Rebuilds by itself",
        CleanGroup.ReviewFirst => "Comes back, but check first",
        CleanGroup.Downloads => "Old downloads",
        _ => "Your files",
    };

    public static string GroupSubtitle(CleanGroup g) => g switch
    {
        CleanGroup.Rebuilds => "Apps and tools make these again",
        CleanGroup.ReviewFirst => "Emulator images, dependencies and other things you can get back",
        CleanGroup.Downloads => "Not opened in a long time",
        _ => "Never ticked for you. Look before you add them",
    };

    /// <summary>The page with the full, filterable list behind a group.</summary>
    private static string? DetailPage(CleanGroup g) => g switch
    {
        CleanGroup.Rebuilds => "Caches",
        CleanGroup.Downloads => "Old Downloads",
        CleanGroup.Media => "Large Media",
        _ => null,
    };

    private static int Preview(CleanGroup g) => g == CleanGroup.Rebuilds ? 8 : 4;

    // ---- Build ----

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        _layout.ColumnDefinitions[2].Width = new GridLength(_class switch
        {
            Rd.WidthClass.Compact => 340, Rd.WidthClass.Regular => 420, _ => 520,
        });
        if (Model.Tree is null)
        {
            Model.ShowPage("Overview");   // the first launch lives on Overview
            return;
        }
        var all = Ready ? Items() : [];
        // New catalogs (a scan, a rescan, a basis change) re-tick from the defaults.
        if (Ready && !ReferenceEquals(_tickedFor, Model.Reviewables))
        {
            _tickedFor = Model.Reviewables;
            _ticked.Clear();
            _ticked.UnionWith(DefaultTicks(all, Model.IsStaged, AppSettings.Load().TickRebuilds));
        }
        _ticked.IntersectWith(all.Select(i => i.Id));
        double offset = _listScroll.VerticalOffset;
        _listScroll.Content = BuildList(all);
        _listScroll.ScrollToVerticalOffset(offset);
        var picked = all.Where(i => _ticked.Contains(i.Id)).ToList();
        _receiptScroll.Content = BuildReceiptColumn(picked);
    }

    private FrameworkElement BuildList(List<CleanItem> all)
    {
        double pad = _class.PagePadding();
        var stack = new StackPanel { Margin = new Thickness(pad + 6, pad, pad + 6, pad) };
        stack.Children.Add(Header(all));
        if (!Ready)
        {
            var wait = Rd.Text("Sorting what's safe to free…", 12, null, "RdInk2");
            wait.Margin = new Thickness(0, 40, 0, 0);
            stack.Children.Add(wait);
        }
        else if (all.Count == 0)
        {
            var none = Rd.Text("Nothing to clean up in this scan. Explore shows everything that takes space.", 13, null, "RdInk2", wrap: true);
            none.Margin = new Thickness(0, 32, 0, 0);
            stack.Children.Add(none);
        }
        else
        {
            foreach (CleanGroup g in Enum.GetValues<CleanGroup>())
            {
                var rows = all.Where(i => i.Group == g).ToList();
                if (rows.Count > 0) stack.Children.Add(Section(g, rows));
            }
        }
        return stack;
    }

    private FrameworkElement Header(List<CleanItem> all)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (Ready && all.Count > 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            buttons.Children.Add(Rd.SecondaryButton("Tick safe only", () =>
            {
                _ticked.Clear();
                _ticked.UnionWith(all.Where(i => i.Group == CleanGroup.Rebuilds).Select(i => i.Id));
                Rebuild();
            }));
            var untick = Rd.SecondaryButton("Untick all", () => { _ticked.Clear(); Rebuild(); });
            untick.Margin = new Thickness(8, 0, 0, 0);
            untick.IsEnabled = _ticked.Count > 0;
            buttons.Children.Add(untick);
            buttons.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(buttons, Dock.Right);
            dock.Children.Add(buttons);
        }
        var titles = new StackPanel();
        titles.Children.Add(Rd.PageTitle("Choose what to free"));
        var sub = Rd.Text("We ticked only things that rebuild by themselves. Everything else waits for you.", 13, null, "RdInk2");
        sub.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(sub);
        dock.Children.Add(titles);
        return dock;
    }

    private FrameworkElement Section(CleanGroup group, List<CleanItem> rows)
    {
        var ids = rows.Select(r => r.Id).ToHashSet();
        int on = ids.Count(_ticked.Contains);
        var state = on == 0 ? TickState.Off : on == ids.Count ? TickState.On : TickState.Mixed;
        long total = rows.Sum(r => r.Bytes);
        bool open = _expanded.Contains(group);
        int preview = Preview(group);
        var shown = open ? rows : rows.Take(preview).ToList();

        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        var size = Rd.Figure(ByteFormat.Format(total), 13.5, FontWeights.SemiBold);
        DockPanel.SetDock(size, Dock.Right);
        head.Children.Add(size);
        if (DetailPage(group) is { } page)
        {
            var every = Rd.Text("See every one →", 12, null, "RdAccentDeep");
            every.Cursor = Cursors.Hand;
            every.ToolTip = "The full list, with filters";
            every.Margin = new Thickness(8, 0, 12, 0);
            every.MouseLeftButtonUp += (_, _) => Model.ShowPage(page);
            DockPanel.SetDock(every, Dock.Right);
            head.Children.Add(every);
        }
        var box = TickBox(state, () =>
        {
            if (state == TickState.On) _ticked.ExceptWith(ids); else _ticked.UnionWith(ids);
            Rebuild();
        });
        System.Windows.Automation.AutomationProperties.SetName(box,
            $"{GroupTitle(group)}: {(state == TickState.On ? "all ticked" : state == TickState.Mixed ? "some ticked" : "none ticked")}");
        box.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(box, Dock.Left);
        head.Children.Add(box);
        var title = Rd.Text(GroupTitle(group), 15.5, FontWeights.SemiBold);
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        var subtitle = Rd.Text(GroupSubtitle(group), 12, null, "RdInk2");
        subtitle.Margin = new Thickness(12, 0, 0, 0);
        head.Children.Add(subtitle);
        stack.Children.Add(new Border
        {
            Child = head, Padding = new Thickness(0, 0, 0, 10),
            BorderBrush = Ui.Brush("RdInk"), BorderThickness = new Thickness(0, 0, 0, 1),
        });

        foreach (var item in shown) stack.Children.Add(Row(item));
        if (rows.Count > preview)
        {
            var hidden = rows.Skip(preview).ToList();
            var more = Rd.Text(open ? "Show fewer" : $"{hidden.Count} more · {ByteFormat.Format(hidden.Sum(h => h.Bytes))}",
                12, null, "AppAccent");
            more.Margin = new Thickness(31, 0, 0, 0);
            var moreRow = new Border { Height = 34, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = more };
            moreRow.MouseLeftButtonUp += (_, _) =>
            {
                if (!_expanded.Remove(group)) _expanded.Add(group);
                Rebuild();
            };
            System.Windows.Automation.AutomationProperties.SetName(moreRow, more.Text);
            stack.Children.Add(moreRow);
        }
        return stack;
    }

    private FrameworkElement Row(CleanItem item)
    {
        bool ticked = _ticked.Contains(item.Id);
        bool staged = Model.IsStaged(item.RevealPath);
        void toggle()
        {
            if (!_ticked.Remove(item.Id)) _ticked.Add(item.Id);
            Rebuild();
        }
        var row = new DockPanel { Margin = new Thickness(3, 0, 2, 0) };
        var box = TickBox(ticked ? TickState.On : TickState.Off, toggle);
        box.Margin = new Thickness(0, 0, 13, 0);
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        var size = Rd.Figure(ByteFormat.Format(item.Bytes), 12);
        size.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        var title = Rd.Text(item.Title, 13, FontWeights.Medium);
        DockPanel.SetDock(title, Dock.Left);
        row.Children.Add(title);
        if (staged)
        {
            var tag = Rd.Tag("In basket", Rd.TagTone.Accent);
            tag.Margin = new Thickness(13, 0, 0, 0);
            tag.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tag, Dock.Right);
            row.Children.Add(tag);
        }
        var why = Rd.Text(item.Why, 12, null, "RdInk3");
        why.Margin = new Thickness(13, 0, 0, 0);
        why.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(why);

        double height = _class switch { Rd.WidthClass.Compact => 40, Rd.WidthClass.Regular => 44, _ => 48 };
        var host = new Border
        {
            Height = height, Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.55), BorderThickness = new Thickness(0, 0, 0, 1),
            ToolTip = item.RevealPath,
        };
        host.MouseEnter += (_, _) => host.Background = Ui.Brush("RdHover");
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, e) => { if (!e.Handled) toggle(); };
        var menu = new ContextMenu();
        var tick = new MenuItem { Header = ticked ? "Untick" : "Tick" };
        tick.Click += (_, _) => toggle();
        var reveal = new MenuItem { Header = "Show in File Explorer" };
        reveal.Click += (_, _) => Explorer.Reveal(item.RevealPath);
        menu.Items.Add(tick);
        menu.Items.Add(reveal);
        host.ContextMenu = menu;
        System.Windows.Automation.AutomationProperties.SetName(host,
            $"{item.Title}, {ByteFormat.Format(item.Bytes)}, {(ticked ? "ticked" : "not ticked")}");
        return host;
    }

    public enum TickState { Off, On, Mixed }

    /// <summary>A violet tick box: off, on, or mixed (some of a group). 16 pt, radius 4.</summary>
    public static Border TickBox(TickState state, Action action)
    {
        var mark = new Grid();
        if (state == TickState.On)
        {
            var check = Ui.Glyph("BsCheckLg", 9.5, Brushes.White);
            check.HorizontalAlignment = HorizontalAlignment.Center;
            check.VerticalAlignment = VerticalAlignment.Center;
            mark.Children.Add(check);
        }
        else if (state == TickState.Mixed)
        {
            mark.Children.Add(new Rectangle { Width = 7, Height = 1.6, Fill = Brushes.White });
        }
        var box = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(4),
            Background = state == TickState.Off ? Brushes.Transparent : Ui.Brush("AppAccent"),
            BorderBrush = state == TickState.Off ? Ui.Brush("RdInk3") : Ui.Brush("AppAccent"),
            BorderThickness = new Thickness(1.5),
            Child = mark,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // A 6 pt larger hit area, like the Mac's contentShape inset.
        var hit = new Border { Padding = new Thickness(6), Margin = new Thickness(-6), Background = Brushes.Transparent, Child = box, Cursor = Cursors.Hand };
        hit.MouseLeftButtonUp += (_, e) => { action(); e.Handled = true; };
        return hit;
    }

    // ---- Receipt ----

    private FrameworkElement BuildReceiptColumn(List<CleanItem> picked)
    {
        bool compact = _class == Rd.WidthClass.Compact;
        long bytes = picked.Sum(p => p.Bytes);
        var stack = new StackPanel { Margin = new Thickness(compact ? 18 : 28, _class.PagePadding(), compact ? 18 : 28, _class.PagePadding()) };
        var vol = Model.Volume ?? Model.Snapshot?.Volume;
        string name = vol?.DriveLabel ?? System.IO.Path.GetFileName(Model.RootPath?.TrimEnd('\\') ?? "") ?? "This PC";
        int skipped = picked.SelectMany(p => p.Targets).Count(t => IsRisky(t.NodeId));
        stack.Children.Add(new ReceiptView(picked.Select(p => (p.Title, p.Bytes)).ToList(), bytes, vol?.FreeBytes, name, skipped, compact));
        stack.Children.Add(MoveButton(picked, bytes));
        var note = Rd.Text("You’ll see the full list once more before anything moves.", 12, null, "RdInk2", wrap: true);
        note.TextAlignment = TextAlignment.Center;
        note.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(note);
        if (CleanupRecord.Load() is { Items.Count: > 0 } last)
        {
            var card = LastCleanupCard(last);
            card.Margin = new Thickness(0, 28, 0, 0);
            stack.Children.Add(card);
        }
        return stack;
    }

    private static bool IsRisky(int nodeId) =>
        nodeId >= 0 && Model.VerdictOf(nodeId).Advice is CleanupAdvice.Warn or CleanupAdvice.Never;

    private FrameworkElement MoveButton(List<CleanItem> picked, long bytes)
    {
        bool compact = _class == Rd.WidthClass.Compact;
        bool empty = picked.Count == 0;
        string title = empty ? "Tick something to free" : $"Move {picked.Count} item{(picked.Count == 1 ? "" : "s")} to Recycle Bin";
        var content = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        var enter = Rd.Figure("⏎ ENTER", 10.5, FontWeights.Medium, "RdOnAccent");
        enter.Opacity = 0.85;
        content.Children.Add(enter);
        var big = Rd.TightText(title, compact ? 19 : 23, -0.4, "RdOnAccent");
        big.Margin = new Thickness(0, compact ? 10 : 18, 0, 0);
        content.Children.Add(new Viewbox { Child = big, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Margin = big.Margin });
        big.Margin = new Thickness(0);
        var face = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = empty ? Rd.WithOpacity(Ui.Brush("AppAccent"), 0.45) : Ui.Brush("AppAccent"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(217, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Child = content,
        };
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(224, 0, 0, 0)),
            Margin = new Thickness(5, 5, -5, -5),
        };
        var button = new Grid { Margin = new Thickness(0, compact ? 22 : 32, 5, 0), Cursor = empty ? Cursors.Arrow : Cursors.Hand };
        button.Children.Add(shadow);
        button.Children.Add(face);
        button.MouseLeftButtonDown += (_, _) => { if (!empty) face.RenderTransform = new TranslateTransform(2, 2); };
        button.MouseLeave += (_, _) => face.RenderTransform = null;
        button.MouseLeftButtonUp += (_, _) =>
        {
            face.RenderTransform = null;
            MoveTicked();
        };
        System.Windows.Automation.AutomationProperties.SetName(button,
            empty ? "Nothing ticked" : $"Move {picked.Count} items to Recycle Bin, {ByteFormat.Format(bytes)}");
        return button;
    }

    private Border LastCleanupCard(CleanupRecord record)
    {
        var row = new DockPanel { Margin = new Thickness(14, 0, 14, 0) };
        var putBack = Rd.SecondaryButton("Put Back", async () => await Model.PutBackLastCleanupAsync());
        putBack.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(putBack, Dock.Right);
        row.Children.Add(putBack);
        var size = Rd.Figure(ByteFormat.Format(record.Items.Sum(i => i.Bytes)), 12);
        size.Margin = new Thickness(6, 0, 10, 0);
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        row.Children.Add(Rd.Text($"{record.Date.LocalDateTime:dddd} · {record.Items.Count} item{(record.Items.Count == 1 ? "" : "s")}", 13));
        var boxed = new Grid { Height = 50 };
        boxed.Children.Add(new Rectangle
        {
            RadiusX = 10, RadiusY = 10, Fill = Ui.Brush("RdCard"), Stroke = Ui.Brush("RdLine"),
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
        });
        boxed.Children.Add(row);
        var stack = new StackPanel();
        stack.Children.Add(Rd.Text("Last cleanup", 12, null, "RdInk2"));
        boxed.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(boxed);
        return new Border { Child = stack };
    }

    private void MoveTicked()
    {
        if (_isMoving || !Ready) return;
        var picked = Items().Where(i => _ticked.Contains(i.Id)).ToList();
        if (picked.Count == 0) return;
        _isMoving = true;
        try
        {
            var targets = picked.SelectMany(p => p.Targets).ToList();
            var risky = targets.Where(t => IsRisky(t.NodeId)).ToList();
            var safe = targets.Except(risky).ToList();
            if (safe.Count == 0)
            {
                Model.Toast($"{risky.Count} risky item{(risky.Count == 1 ? "" : "s")} — add {(risky.Count == 1 ? "it" : "them")} from Explore to see why");
                return;
            }
            // Folders the scan knows stage by node (verdicts, sizes); anything else by path.
            var byNode = safe.Where(t => t.NodeId >= 0).Select(t => t.NodeId).ToList();
            Model.StageMany(byNode, "Clean up");
            foreach (var (nodeId, path) in safe.Where(t => t.NodeId < 0))
                Model.StagePath(path, 0, "Clean up", notify: false);
            if (risky.Count > 0)
                Model.Toast($"Skipped {risky.Count} risky item{(risky.Count == 1 ? "" : "s")} — add {(risky.Count == 1 ? "it" : "them")} one at a time to see why");
            _ticked.ExceptWith(picked.Select(p => p.Id));
            Model.ShowPage("Cleanup");
        }
        finally { _isMoving = false; }
    }
}

/// <summary>The paper receipt: what moves, what you get back, where it goes, the undo (Mac: Receipt).</summary>
internal sealed class ReceiptView : Border
{
    private static readonly Brush Paper = Brushes.White;
    private static Brush Black(double opacity) => new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 0, 0, 0));

    public ReceiptView(List<(string Title, long Bytes)> lines, long total, long? freeBefore, string volumeName,
        int skippedCount, bool compact)
    {
        int maxLines = compact ? 4 : 7;
        var stack = new StackPanel { Margin = compact ? new Thickness(18, 18, 18, 20) : new Thickness(26, 24, 26, 28) };
        var head = new DockPanel();
        var tag = Rd.Figure("CLEANUP", 10.5, FontWeights.Medium);
        tag.Foreground = Black(0.4);
        DockPanel.SetDock(tag, Dock.Right);
        head.Children.Add(tag);
        var brand = Rd.Text("freedisk.space", 15, FontWeights.SemiBold);
        brand.Foreground = Brushes.Black;
        head.Children.Add(brand);
        stack.Children.Add(head);
        var when = Rd.Text($"{DateTime.Now:h:mm tt} · {volumeName}", 12);
        when.Foreground = Black(0.55);
        when.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(when);
        stack.Children.Add(Dashed());
        if (lines.Count == 0)
        {
            var none = Rd.Text("Nothing ticked yet.", 13);
            none.Foreground = Black(0.45);
            none.Margin = new Thickness(0, 6, 0, 6);
            stack.Children.Add(none);
        }
        foreach (var (title, bytes) in lines.Take(maxLines)) stack.Children.Add(Dotted(title, ByteFormat.Format(bytes)));
        if (lines.Count > maxLines)
            stack.Children.Add(Dotted($"and {lines.Count - maxLines} more", ByteFormat.Format(lines.Skip(maxLines).Sum(l => l.Bytes)), muted: true));
        if (skippedCount > 0)
        {
            var skip = Rd.Text($"{skippedCount} risky item{(skippedCount == 1 ? "" : "s")} will be skipped. Add {(skippedCount == 1 ? "it" : "them")} on {(skippedCount == 1 ? "its" : "their")} own to see why.", 12, wrap: true);
            skip.Foreground = Black(0.55);
            skip.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(skip);
        }
        stack.Children.Add(Dashed());
        var back = Leader(Rd.Text("You get back", 16, FontWeights.SemiBold), Rd.Figure(ByteFormat.Format(total), 16, FontWeights.SemiBold, "AppAccent"));
        ((TextBlock)back.Children[0]).Foreground = Brushes.Black;
        back.Margin = new Thickness(0, 6, 0, 6);
        stack.Children.Add(back);
        if (freeBefore is { } free)
            stack.Children.Add(Dotted("Free space after", $"{ShellFormat.HeroBytes(free)} → {ShellFormat.HeroBytes(free + total)}"));
        stack.Children.Add(Dotted("Where it goes", "The Recycle Bin", mono: false));
        stack.Children.Add(Dotted("Deleted forever", "Nothing", mono: false));
        stack.Children.Add(Dotted("Undo", "Put Back, any time", mono: false));
        if (!compact)
        {
            stack.Children.Add(Dashed());
            var foot = Rd.Text("The space comes back when you empty the Recycle Bin. Put Back returns everything to the same place, with the same name.", 12, wrap: true);
            foot.Foreground = Black(0.55);
            foot.LineHeight = Ui.Scaled(12) * 1.33 + 2;
            stack.Children.Add(foot);
        }

        // The paper is drawn on a Canvas, which measures as zero: the torn teeth hang 7 pt below
        // the text, and a Path that took part in layout grew the box by 7 pt on every pass
        // (it pushed the Move button millions of pixels down).
        var paper = new System.Windows.Shapes.Path { Fill = Paper, Stroke = Black(0.1), StrokeThickness = 1 };
        var paperHost = new Canvas { ClipToBounds = false };
        paperHost.Children.Add(paper);
        var layers = new Grid();
        layers.Children.Add(paperHost);
        layers.Children.Add(stack);
        stack.SizeChanged += (_, _) =>
            paper.Data = PaperShape(new Size(stack.ActualWidth + stack.Margin.Left + stack.Margin.Right,
                stack.ActualHeight + stack.Margin.Top + stack.Margin.Bottom));
        Child = layers;
        Margin = new Thickness(0, 0, 0, 7);   // the torn teeth hang below the box
        Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Opacity = 0.18, BlurRadius = 44, ShadowDepth = 18, Direction = 270 };
        System.Windows.Automation.AutomationProperties.SetName(this,
            $"Receipt: {lines.Count} items, you get back {ByteFormat.Format(total)}, to the Recycle Bin, undo with Put Back");
    }

    /// <summary>A receipt with a torn, zig-zag bottom edge: 8 pt teeth, 7 deep.</summary>
    private static Geometry PaperShape(Size size)
    {
        const double tooth = 8, depth = 7;
        var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
        figure.Segments.Add(new LineSegment(new Point(size.Width, 0), true));
        figure.Segments.Add(new LineSegment(new Point(size.Width, size.Height), true));
        double x = size.Width;
        bool down = false;
        while (x > 0)
        {
            x = Math.Max(0, x - tooth);
            figure.Segments.Add(new LineSegment(new Point(x, size.Height + (down ? 0 : depth)), true));
            down = !down;
        }
        figure.Segments.Add(new LineSegment(new Point(0, 0), true));
        return new PathGeometry([figure]);
    }

    private static FrameworkElement Dashed() => new Line
    {
        X1 = 0, X2 = 1, Stretch = Stretch.Fill, Height = 1, Stroke = Black(0.22), StrokeThickness = 1,
        StrokeDashArray = new DoubleCollection { 4, 3 }, Margin = new Thickness(0, 11, 0, 11),
    };

    private static Grid Leader(TextBlock left, TextBlock right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 12 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        left.VerticalAlignment = VerticalAlignment.Bottom;
        right.VerticalAlignment = VerticalAlignment.Bottom;
        grid.Children.Add(left);
        var leader = new Line
        {
            X1 = 0, X2 = 1, Stretch = Stretch.Fill, Height = 1, Stroke = Black(0.25), StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 1, 3 }, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(10, 0, 10, 5),
        };
        Grid.SetColumn(leader, 1);
        grid.Children.Add(leader);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private static Grid Dotted(string label, string value, bool muted = false, bool mono = true)
    {
        var left = Rd.Text(label, 13);
        left.Foreground = Black(muted ? 0.5 : 0.9);
        var right = mono ? Rd.Figure(value, 12) : Rd.Text(value, 13);
        right.Foreground = Black(muted ? 0.5 : 0.9);
        var row = Leader(left, right);
        row.Margin = new Thickness(0, 4, 0, 4);
        return row;
    }
}
