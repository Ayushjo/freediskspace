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
    /// <summary>Groups opened past their preview ("N more"), and opened past <see cref="ExpandedLimit"/> ("Show all").</summary>
    private readonly HashSet<CleanGroup> _expanded = [], _showAll = [];
    private object? _tickedFor;
    private bool _isMoving;
    /// <summary>"Last used" filter, in days (0 = any time), and each row's newest file date, per catalog.</summary>
    private int _lastUsed;
    private readonly Dictionary<string, int> _lastDay = [];
    /// <summary>
    /// Built once per catalog, not on every tick: the rows (thousands of downloads and media
    /// re-filtered per click), each row's risky-folder count, and the last cleanup's record,
    /// which is read again only when the catalog or the basket changes (a commit, a Put Back).
    /// </summary>
    private object? _itemsFor, _downloadsFor, _mediaFor;
    private List<CleanItem> _items = [];
    private readonly Dictionary<string, int> _risky = [];
    private CleanupRecord? _lastCleanup;
    private bool _recordStale = true;
    /// <summary>This build's focusable controls by key, so a rebuild hands focus back (see <see cref="RestoreFocus"/>).</summary>
    private readonly Dictionary<string, FrameworkElement> _focusables = [];
    private string? _pendingFocus;
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
        Loaded += (_, _) =>
        {
            Model.StagedChanged += OnStaged;
            Focus();   // Enter works at once, and Tab starts from the page
        };
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

    private void OnStaged(object? sender, EventArgs e)
    {
        _recordStale = true;   // a commit or a Put Back rewrote the last cleanup's record
        Dispatcher.InvokeAsync(Rebuild);
    }

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    private bool Ready => Model.Reviewables is not null && Model.OldDownloads is not null && Model.LargeMedia is not null;

    private List<CleanItem> Items()
    {
        if (!ReferenceEquals(_itemsFor, Model.Reviewables) || !ReferenceEquals(_downloadsFor, Model.OldDownloads)
            || !ReferenceEquals(_mediaFor, Model.LargeMedia))
        {
            (_itemsFor, _downloadsFor, _mediaFor) = (Model.Reviewables, Model.OldDownloads, Model.LargeMedia);
            _items = BuildItems(Model.Reviewables ?? [], Model.Totals,
                DownloadItems(Model.OldDownloads?.Candidates ?? [], Model.RootPath),
                MediaItems(Model.LargeMedia?.Candidates ?? [], Model.RootPath));
        }
        return _items;
    }

    /// <summary>
    /// Old downloads older than 30 days — the Old downloads page's default, so the group's
    /// "Not changed in a long time" holds and its count matches the page.
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
        CleanGroup.Downloads => "Not changed in a long time",   // aged by file dates; nothing records when it was opened
        _ => "Never ticked for you. Look before you add them",
    };

    /// <summary>The big button. It fills the basket; the move to the Recycle Bin is confirmed there.</summary>
    public static string MoveTitle(int count) =>
        count == 0 ? "Tick something to free" : $"Add {count:N0} item{(count == 1 ? "" : "s")} to the basket";

    /// <summary>Rows an opened group draws before "Show all": the page rebuilds on every tick.</summary>
    public const int ExpandedLimit = 100;

    /// <summary>
    /// How many of a group's rows (biggest first) to draw. Folded: the preview — but a ticked row
    /// is never folded away under "N more": a tick past the preview opens the group. Open: up to
    /// <see cref="ExpandedLimit"/> rows until "Show all"; ticks past that are counted on the link.
    /// </summary>
    public static int RowsShown(IReadOnlyList<CleanItem> rows, int preview, IReadOnlySet<string> ticked,
        bool expanded, bool showAll)
    {
        if (showAll) return rows.Count;
        bool open = expanded || rows.Skip(preview).Any(r => ticked.Contains(r.Id));
        return Math.Min(rows.Count, open ? Math.Max(preview, ExpandedLimit) : preview);
    }

    /// <summary>
    /// The link under rows still hidden: "12 more · 3 GB" while folded (nothing hidden is ticked
    /// then), "Show all 250 · 40 more ticked" past <see cref="ExpandedLimit"/>, so no tick is hidden unsaid.
    /// </summary>
    public static string MoreLink(int count, int shown, int preview, int hiddenTicked, long hiddenBytes) =>
        shown <= preview ? $"{count - shown:N0} more · {ByteFormat.Format(hiddenBytes)}"
        : $"Show all {count:N0} · " + (hiddenTicked > 0 ? $"{hiddenTicked:N0} more ticked" : $"{ByteFormat.Format(hiddenBytes)} more");

    /// <summary>The page with the full, filterable list behind a group.</summary>
    private static string? DetailPage(CleanGroup g) => g switch
    {
        CleanGroup.Rebuilds => "Caches",
        CleanGroup.Downloads => "Old Downloads",
        CleanGroup.Media => "Large Media",
        _ => null,
    };

    public static int Preview(CleanGroup g) => g == CleanGroup.Rebuilds ? 8 : 4;

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
        string? focusKey = _pendingFocus
            ?? (Keyboard.FocusedElement is FrameworkElement { Tag: string key } had && IsAncestorOf(had) ? key : null);
        _focusables.Clear();
        var all = Ready ? Items() : [];
        // New catalogs (a scan, a rescan, a basis change) re-tick from the defaults.
        if (Ready && !ReferenceEquals(_tickedFor, Model.Reviewables))
        {
            _tickedFor = Model.Reviewables;
            _lastDay.Clear();
            _risky.Clear();
            _recordStale = true;
            _ticked.Clear();
            _ticked.UnionWith(DefaultTicks(all, Model.IsStaged, AppSettings.Load().TickRebuilds));
        }
        if (_lastUsed > 0 && Model.Tree is { } tree)
        {
            int today = AgeMap.Today();
            all = all.Where(i => DeveloperBoard.LastUsedBefore(LastDay(i, tree), today, _lastUsed)).ToList();
        }
        _ticked.IntersectWith(all.Select(i => i.Id));   // a hidden row never moves
        _ticked.ExceptWith(all.Where(i => Model.IsStaged(i.RevealPath)).Select(i => i.Id));   // already in the basket
        double offset = _listScroll.VerticalOffset;
        _listScroll.Content = BuildList(all);
        _listScroll.ScrollToVerticalOffset(offset);
        var picked = all.Where(i => _ticked.Contains(i.Id)).ToList();
        _receiptScroll.Content = BuildReceiptColumn(picked);
        RestoreFocus(focusKey);
    }

    /// <summary>Remembers a focusable control under a key that survives rebuilds.</summary>
    private T Keyed<T>(T element, string key) where T : FrameworkElement
    {
        element.Tag = key;
        _focusables[key] = element;
        return element;
    }

    /// <summary>
    /// A rebuild replaces every control, so the one that had keyboard focus gets it back by key
    /// once the new ones are laid out — a keyboard user ticks row after row.
    /// </summary>
    private void RestoreFocus(string? key)
    {
        if (key is null || !_focusables.ContainsKey(key)) return;
        _pendingFocus = key;
        Dispatcher.BeginInvoke(() =>
        {
            if (_pendingFocus is { } k && _focusables.TryGetValue(k, out var element)) element.Focus();
            _pendingFocus = null;
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The newest file anywhere in the row's folders — when it was last used.</summary>
    private int LastDay(CleanItem item, FileTree tree)
    {
        if (!_lastDay.TryGetValue(item.Id, out int day))
            _lastDay[item.Id] = day = item.Targets.Select(t => AgeMap.NewestDay(tree, t.NodeId)).DefaultIfEmpty(0).Max();
        return day;
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
            var none = Rd.Text(_lastUsed > 0
                    ? $"Nothing here was last used {DeveloperBoard.LastUsedTitle(_lastUsed).ToLowerInvariant()}."
                    : "Nothing to clean up in this scan. Explore shows everything that takes space.", 13, null, "RdInk2", wrap: true);
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
        if (Ready)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var lastUsed = ListKit.Dropdown("Last used", DeveloperBoard.LastUsedChoices, _lastUsed, DeveloperBoard.LastUsedTitle,
                days => { _lastUsed = days; Rebuild(); });
            lastUsed.ToolTip = "Only what nothing has touched for a while — the newest file inside each item";
            lastUsed.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(lastUsed);
            buttons.Children.Add(Keyed(Rd.SecondaryButton("Tick safe only", () =>
            {
                _ticked.Clear();
                _ticked.UnionWith(all.Where(i => i.Group == CleanGroup.Rebuilds).Select(i => i.Id));
                Rebuild();
            }), "safe"));
            var untick = Keyed(Rd.SecondaryButton("Untick all", () => { _ticked.Clear(); Rebuild(); }), "untick");
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
        int preview = Preview(group);
        bool expanded = _expanded.Contains(group), showAll = _showAll.Contains(group);
        int shown = RowsShown(rows, preview, _ticked, expanded, showAll);
        int folded = RowsShown(rows, preview, _ticked, expanded: false, showAll: false);

        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        var size = Rd.Figure(ByteFormat.Format(total), 13.5, FontWeights.SemiBold);
        DockPanel.SetDock(size, Dock.Right);
        head.Children.Add(size);
        if (DetailPage(group) is { } page)
        {
            var every = Keyed(Rd.Link("See every one →", () => Model.ShowPage(page), 12, "RdAccentDeep", FontWeights.Normal), "every:" + group);
            every.ToolTip = "The full list, with filters";
            every.Margin = new Thickness(8, 0, 12, 0);
            DockPanel.SetDock(every, Dock.Right);
            head.Children.Add(every);
        }
        var box = Box(state == TickState.On ? true : state == TickState.Off ? false : null,
            $"{GroupTitle(group)}, {ByteFormat.Format(total)}", "group:" + group, () =>
            {
                if (state == TickState.On) _ticked.ExceptWith(ids); else _ticked.UnionWith(ids);
                Rebuild();
            });
        box.Margin = new Thickness(-5, 0, 7, 0);   // the box itself lines up with the rows' boxes
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

        foreach (var item in rows.Take(shown)) stack.Children.Add(Row(item));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Height = 34, Margin = new Thickness(31, 0, 0, 0) };
        if (shown < rows.Count)
        {
            var hidden = rows.Skip(shown).ToList();
            bool isFolded = shown <= preview;
            string text = MoreLink(rows.Count, shown, preview, hidden.Count(r => _ticked.Contains(r.Id)), hidden.Sum(h => h.Bytes));
            links.Children.Add(Keyed(Rd.Link(text, () =>
            {
                if (isFolded) _expanded.Add(group); else _showAll.Add(group);
                Rebuild();
            }, 12, "AppAccent", FontWeights.Normal), "more:" + group));
        }
        if (folded < shown)
        {
            var fewer = Keyed(Rd.Link("Show fewer", () =>
            {
                _expanded.Remove(group);
                _showAll.Remove(group);
                Rebuild();
            }, 12, "AppAccent", FontWeights.Normal), "fewer:" + group);
            if (links.Children.Count > 0) fewer.Margin = new Thickness(18, 0, 0, 0);
            links.Children.Add(fewer);
        }
        foreach (FrameworkElement link in links.Children) link.VerticalAlignment = VerticalAlignment.Center;
        if (links.Children.Count > 0) stack.Children.Add(links);
        return stack;
    }

    private FrameworkElement Row(CleanItem item)
    {
        bool ticked = _ticked.Contains(item.Id);
        bool staged = Model.IsStaged(item.RevealPath);
        void toggle()
        {
            if (staged) return;   // in the basket: the violet row is the tick
            if (!_ticked.Remove(item.Id)) _ticked.Add(item.Id);
            Rebuild();
        }
        var row = new DockPanel { Margin = new Thickness(3, 0, 2, 0) };
        var box = Box(ticked || staged, $"{item.Title}, {ByteFormat.Format(item.Bytes)}{(staged ? ", in the basket" : "")}",
            "row:" + item.Id, toggle);
        System.Windows.Automation.AutomationProperties.SetHelpText(box, item.Why);
        box.IsEnabled = !staged;   // in the basket: the violet row is the tick; take it out from the menu or the basket
        box.Margin = new Thickness(-5, 0, 8, 0);
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        var size = Rd.Figure(ByteFormat.Format(item.Bytes), 12);
        size.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        var title = Rd.Text(item.Title, 13, FontWeights.Medium);
        DockPanel.SetDock(title, Dock.Left);
        row.Children.Add(title);
        var why = Rd.Text(item.Why, 12, null, "RdInk3");
        why.Margin = new Thickness(13, 0, 0, 0);
        why.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(why);

        double height = _class switch { Rd.WidthClass.Compact => 40, Rd.WidthClass.Regular => 44, _ => 48 };
        var layers = new Grid();
        layers.Children.Add(row);
        if (staged) layers.Children.Add(Rd.InBasketEdge());
        var host = new Border
        {
            Height = height, Child = layers, Cursor = staged ? Cursors.Arrow : Cursors.Hand,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.55), BorderThickness = new Thickness(0, 0, 0, 1),
            ToolTip = staged ? $"In the basket — right-click to take it out\n{item.RevealPath}" : item.RevealPath,
        };
        Brush rest = staged ? Rd.InBasketFill : Brushes.Transparent;
        host.Background = rest;
        host.MouseEnter += (_, _) => host.Background = staged ? Rd.InBasketHover : Ui.Brush("RdHover");
        host.MouseLeave += (_, _) => host.Background = rest;
        host.MouseLeftButtonUp += (_, e) => { if (!e.Handled) toggle(); };
        var menu = new ContextMenu();
        var tick = new MenuItem { Header = staged ? "Take Out of the Basket" : ticked ? "Untick" : "Tick" };
        tick.Click += (_, _) =>
        {
            if (staged) Model.UnstagePaths(item.Targets.Select(t => t.Path).Append(item.RevealPath));
            else toggle();
        };
        var reveal = new MenuItem { Header = "Show in File Explorer" };
        reveal.Click += (_, _) => Explorer.Reveal(item.RevealPath);
        menu.Items.Add(tick);
        menu.Items.Add(reveal);
        host.ContextMenu = menu;
        return host;
    }

    /// <summary>
    /// A real check box in App.xaml's style — Tab reaches it, Space ticks it and a screen reader
    /// reads its state; null is the mixed state of a partly ticked group.
    /// </summary>
    private CheckBox Box(bool? isChecked, string name, string key, Action toggle)
    {
        var box = Keyed(new CheckBox { IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand }, key);
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        box.Click += (_, _) => toggle();
        return box;
    }

    public enum TickState { Off, On, Mixed }

    /// <summary>
    /// A violet tick box: off, on, or mixed (some of a group). 16 pt, radius 4. Clean up itself uses
    /// real check boxes. Name the Border it comes back in; the toggle inside reads its name from it.
    /// </summary>
    public static Border TickBox(TickState state, Action action)
    {
        var mark = new Grid();
        if (state == TickState.On)
        {
            var check = Ui.Glyph("BsCheckLg", 9.5, Ui.Brush("RdOnAccent"));
            check.HorizontalAlignment = HorizontalAlignment.Center;
            check.VerticalAlignment = VerticalAlignment.Center;
            mark.Children.Add(check);
        }
        else if (state == TickState.Mixed)
        {
            mark.Children.Add(new Rectangle { Width = 7, Height = 1.6, Fill = Ui.Brush("RdOnAccent") });
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
        return TickToggle(box, state, action);
    }

    /// <summary>
    /// The tick box's control: a real toggle button around <paramref name="face"/>, so Tab reaches
    /// it, Space flips it and a screen reader reads its state; <paramref name="action"/> runs on a
    /// click, Space or the screen reader's toggle. It used to be a Border only a mouse could use.
    /// </summary>
    internal static Border TickToggle(UIElement face, TickState state, Action action)
    {
        var toggle = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = face,
            IsChecked = state switch { TickState.On => true, TickState.Off => false, _ => null },
            Template = Rd.PlainTemplate(0),
            // A 6 pt larger hit area, like the Mac's contentShape inset.
            Padding = new Thickness(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,   // the box's own size, whatever the theme's ToggleButton style says
            Cursor = Cursors.Hand,
        };
        // Set after IsChecked: only a flip runs the action, however it was asked for.
        toggle.Checked += (_, _) => action();
        toggle.Unchecked += (_, _) => action();
        var host = Rd.ButtonHost(toggle);
        host.Margin = new Thickness(-6);
        return host;
    }

    // ---- Receipt ----

    private FrameworkElement BuildReceiptColumn(List<CleanItem> picked)
    {
        bool compact = _class == Rd.WidthClass.Compact;
        long bytes = picked.Sum(p => p.Bytes);
        var stack = new StackPanel { Margin = new Thickness(compact ? 18 : 28, _class.PagePadding(), compact ? 18 : 28, _class.PagePadding()) };
        var vol = Model.Volume ?? Model.Snapshot?.Volume;
        string name = vol?.DriveLabel ?? System.IO.Path.GetFileName(Model.RootPath?.TrimEnd('\\') ?? "") ?? "This PC";
        int skipped = picked.Sum(Risky);
        stack.Children.Add(new ReceiptView(picked.Select(p => (p.Title, p.Bytes)).ToList(), bytes, vol?.FreeBytes, name, skipped, compact));
        stack.Children.Add(MoveButton(picked, bytes));
        var note = Rd.Text("You’ll see the full list once more before anything moves.", 12, null, "RdInk2", wrap: true);
        note.TextAlignment = TextAlignment.Center;
        note.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(note);
        if (_recordStale)
        {
            _lastCleanup = CleanupRecord.Load();
            _recordStale = false;
        }
        if (_lastCleanup is { Items.Count: > 0 } last)
        {
            var card = LastCleanupCard(last);
            card.Margin = new Thickness(0, 28, 0, 0);
            stack.Children.Add(card);
        }
        return stack;
    }

    private static bool IsRisky(int nodeId) =>
        nodeId >= 0 && Model.VerdictOf(nodeId).Advice is CleanupAdvice.Warn or CleanupAdvice.Never;

    /// <summary>How many of a row's folders are risky — classified once per catalog, not per tick (Temp alone can be thousands).</summary>
    private int Risky(CleanItem item)
    {
        if (!_risky.TryGetValue(item.Id, out int count))
            _risky[item.Id] = count = item.Targets.Count(t => IsRisky(t.NodeId));
        return count;
    }

    private FrameworkElement MoveButton(List<CleanItem> picked, long bytes)
    {
        bool compact = _class == Rd.WidthClass.Compact;
        bool empty = picked.Count == 0;
        string title = MoveTitle(picked.Count);
        var content = new StackPanel();
        var enter = Rd.Figure("⏎ ENTER", 10.5, FontWeights.Medium, "RdOnAccent");
        enter.Opacity = 0.85;
        content.Children.Add(enter);
        var big = Rd.TightText(title, compact ? 19 : 23, -0.4, "RdOnAccent");
        content.Children.Add(new Viewbox
        {
            Child = big, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, compact ? 10 : 18, 0, 0),
        });
        // A real button: Tab reaches it, Enter or Space presses it, a screen reader names it.
        var button = Keyed(Rd.AccentButton(title, MoveTicked, large: true, fullWidth: true, hard: true), "move");
        button.Content = content;
        button.Height = double.NaN;
        button.Padding = new Thickness(20, 16, 20, 16);
        button.Margin = new Thickness(0, compact ? 22 : 32, 3, 0);
        button.IsEnabled = !empty;
        System.Windows.Automation.AutomationProperties.SetName(button,
            empty ? "Nothing ticked" : $"{title}, {ByteFormat.Format(bytes)}");
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
            Model.OpenBasket();
        }
        finally { _isMoving = false; }
    }
}

/// <summary>The paper receipt: what moves, what you get back, where it goes, the undo (Mac: Receipt).</summary>
public sealed class ReceiptView : Border
{
    /// <summary>The print: black on the white paper, the window text colour under high contrast.</summary>
    private static Brush Black(double opacity) => Rd.Faded("RdOnPaper", opacity);

    /// <summary>
    /// The fine print, in order. Recycling frees nothing by itself: the space comes back once the
    /// bin is emptied, and Put Back works only until then. Nothing is deleted for good unless you
    /// confirm it (the basket asks before deleting what the bin refuses as too big).
    /// </summary>
    public static List<(string Label, string Value, bool Mono)> Terms(long? freeBefore, long total)
    {
        var terms = new List<(string, string, bool)> { ("Where it goes", "The Recycle Bin", false) };
        if (freeBefore is { } free)
            terms.Add(("Free once emptied", $"{ShellFormat.HeroBytes(free)} → {ShellFormat.HeroBytes(free + total)}", true));
        terms.Add(("Deleted forever", "Only if you confirm", false));
        terms.Add(("Undo", "Put Back, until it's emptied", false));
        return terms;
    }

    /// <summary>Shown at every width: without it the receipt reads as if the space were free already.</summary>
    public const string Footnote =
        "The space comes back when you empty the Recycle Bin. Put Back returns everything to the same place, with the same name.";

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
        brand.Foreground = Black(1);
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
        ((TextBlock)back.Children[0]).Foreground = Black(1);
        back.Margin = new Thickness(0, 6, 0, 6);
        stack.Children.Add(back);
        foreach (var (label, value, mono) in Terms(freeBefore, total)) stack.Children.Add(Dotted(label, value, mono: mono));
        stack.Children.Add(Dashed());
        var foot = Rd.Text(Footnote, 12, wrap: true);
        foot.Foreground = Black(0.55);
        foot.LineHeight = Math.Ceiling(Ui.Scaled(12) * Rd.TextLineHeight) + 2;   // .lineSpacing(2)
        stack.Children.Add(foot);

        // The paper is drawn on a Canvas, which measures as zero: the torn teeth hang 7 pt below
        // the text, and a Path that took part in layout grew the box by 7 pt on every pass
        // (it pushed the Move button millions of pixels down).
        var paper = new System.Windows.Shapes.Path { Fill = Ui.Brush("RdPaper"), Stroke = Black(0.1), StrokeThickness = 1 };
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
            $"Receipt: {lines.Count} items, {ByteFormat.Format(total)}, to the Recycle Bin. The space comes back once you empty it; until then Put Back undoes it.");
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
