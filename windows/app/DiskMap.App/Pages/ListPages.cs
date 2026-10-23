using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// Shared helper for the list-style pages: a vertical stack with a header
/// row, content area, and refresh on state changes (while on screen —
/// see <see cref="ModelEvents"/>).
/// </summary>
public abstract class ListPage : UserControl
{
    protected ScanModel Model => ScanModel.Shared;
    protected readonly StackPanel Root = new()
    {
        Margin = new Thickness(Ui.PageSide, Ui.PageTop, Ui.PageSide, 24),
    };
    protected readonly ScrollViewer Scroller = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>Secondary-text brush, follows the theme palette.</summary>
    protected static Brush Subtle => Ui.Brush("AppSubtle");

    private int _generation;

    /// <summary>
    /// The page's selection bar, pinned under the scrolling content — the
    /// "Select all · N selected · Add Selected to Cleanup" actions stay on
    /// screen however long the list is, instead of sitting after its last
    /// row. Empty on pages without selectable rows.
    /// </summary>
    protected readonly ContentControl ActionBar = new();

    protected ListPage()
    {
        Scroller.Content = Root;
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(ActionBar, Dock.Bottom);
        dock.Children.Add(ActionBar);
        dock.Children.Add(Scroller);
        Content = dock;
        ModelEvents.WhileLoaded(this, Refresh);
    }

    /// <summary>
    /// The shared selection bar: a Select-all checkbox over everything the
    /// page currently shows, the running selection, and one primary
    /// "Add Selected to Cleanup". <paramref name="extra"/> buttons sit
    /// before it (Copy Path, Reveal…).
    /// </summary>
    protected static Border SelectionBar(int selectable, int selected, long selectedBytes,
        Action selectAll, Action clear, Action addSelected, params FrameworkElement[] extra)
    {
        var dock = new DockPanel();
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var all = new CheckBox
        {
            IsChecked = selectable > 0 && selected >= selectable ? true : selected > 0 ? null : false,
            IsEnabled = selectable > 0,
            VerticalAlignment = VerticalAlignment.Center,
            Content = Ui.T(selectable > 0 ? $"Select all {selectable:N0}" : "Nothing to select", 12.5, FontWeights.Medium),
            ToolTip = "Select every row this page is showing (Ctrl+A) — Esc clears",
        };
        // The box reflects state; a click always means "all" unless
        // everything is already selected.
        all.Click += (_, _) => { if (selected >= selectable && selectable > 0) clear(); else selectAll(); };
        left.Children.Add(all);
        var status = Ui.Mono(selected > 0
                ? $"  ·  {selected:N0} selected · {ByteFormat.Format(selectedBytes)}"
                : "  ·  or tick rows",
            11.5, null, selected > 0 ? Ui.Brush("AppForeground") : Ui.Brush("AppSubtle"));
        status.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(status);
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (selected > 0)
        {
            var clearButton = Ui.Button("Clear", null, Ui.ButtonStyle.Ghost, clear);
            clearButton.Margin = new Thickness(0, 0, 6, 0);
            right.Children.Add(clearButton);
        }
        // Secondary actions only once there's something to act on.
        if (selected > 0)
        {
            foreach (var e in extra)
            {
                e.Margin = new Thickness(0, 0, 6, 0);
                right.Children.Add(e);
            }
        }
        var add = Ui.Button(selected > 0 ? $"Add {selected:N0} to Cleanup" : "Add Selected to Cleanup",
            Icons.Cleanup, Ui.ButtonStyle.Primary, addSelected);
        add.IsEnabled = selected > 0;
        right.Children.Add(add);
        dock.Children.Add(right);

        return new Border
        {
            Background = Ui.Brush("AppBackground"),
            BorderBrush = Ui.Brush("AppBorder"), BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Ui.PageSide, 10, Ui.PageSide, 10),
            MinHeight = 52,
            Child = dock,
        };
    }

    internal void ResetScrollPosition() =>
        Dispatcher.BeginInvoke(Scroller.ScrollToTop, System.Windows.Threading.DispatcherPriority.Loaded);

    protected abstract void Refresh();

    /// <summary>
    /// Runs <paramref name="work"/> (anything proportional to the tree) on
    /// a worker thread, then <paramref name="show"/> on the UI thread —
    /// unless a newer refresh started meanwhile.
    /// </summary>
    protected async void Compute<T>(Func<T> work, Action<T> show)
    {
        int generation = ++_generation;
        T result;
        try
        {
            result = await Task.Run(work);
        }
        catch (Exception ex)
        {
            if (generation == _generation) Root.Children.Add(new TextBlock { Text = $"Failed: {ex.Message}", Foreground = Subtle });
            return;
        }
        if (generation == _generation) show(result);
    }

    protected static TextBlock Working(string text) => new() { Text = text, Foreground = Subtle };

    /// <summary>
    /// Page header (§17 anatomy): a mono eyebrow for the nav group, the
    /// 20 pt semibold title, the subtitle, an optional mono stat right.
    /// The legacy icon params are accepted and ignored — no icon tiles.
    /// </summary>
    protected DockPanel Header(string title, string subtitle, string? rightStat = null,
        string? glyph = null, Brush? iconBg = null, Brush? iconFg = null)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, Ui.ZoneGap) };
        if (rightStat is not null)
        {
            var stat = Ui.Mono(rightStat, 11, null, Ui.Brush("AppFaint"));
            stat.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(stat, Dock.Right);
            header.Children.Add(stat);
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var eyebrow = Ui.MonoLabel(EyebrowFor(title), Ui.Brush("AppSubtle"));
        eyebrow.Margin = new Thickness(0, 0, 0, 4);
        titles.Children.Add(eyebrow);
        var pageTitle = Ui.PageTitle(title);
        pageTitle.Margin = new Thickness(0, 0, 0, 4);
        titles.Children.Add(pageTitle);
        titles.Children.Add(Ui.PageSubtitle(subtitle));
        header.Children.Add(titles);
        return header;
    }

    private static string EyebrowFor(string title) => title switch
    {
        "Overview" => "MAIN",
        "Find" or "Biggest Files" or "Biggest Folders" or "Forgotten Files" or "Duplicates" => "FIND",
        "Safe to Review" or "Caches" or "Old Downloads" or "Large Media" or "Cleanup" => "CLEAN",
        "File Browser" or "Visualize" or "Developer Storage" or "Applications" or "Snapshots" => "EXPLORE",
        _ => title,
    };

    protected UIElement NeedsScan(string what)
    {
        return Ui.EmptyState(Icons.Folder, "Scan a folder to begin",
            $"{what} Once you pick a folder, freedisk.space maps it in seconds.",
            Ui.Button("Scan Folder…", Icons.Add, Ui.ButtonStyle.Primary, () => Model.RequestScan()));
    }
}

/// <summary>
/// A ranked file/folder table — the shared body of Biggest Files,
/// Biggest Folders, Forgotten Files, Large Media, Old Downloads, File
/// Browser and Search: toolbar (name filter, kind pills, sort), the
/// mockup's column grid, and selection into the inspector.
/// </summary>
public abstract class FileListPage : ListPage
{
    private string _filterText = "";
    private string _kindFilter = "all";
    private string _classFilter = "all";
    protected string SortMode = "largest";

    /// <summary>
    /// Set before navigating to a list page to open it on one storage
    /// category (Overview's "Where it's going" rows use it).
    /// </summary>
    public static string? PendingClassFilter { get; set; }

    /// <summary>What each listed row is (WSL disk, AI model, cache…) — computed with the list, off the UI thread.</summary>
    private Dictionary<int, StorageVerdict> _verdicts = [];

    private StorageVerdict VerdictFor(FileTree tree, int id, string rootPath) =>
        _verdicts.TryGetValue(id, out var v) ? v
            : _verdicts[id] = StorageClassifier.Classify(tree.PathOf(id, rootPath), tree.IsDirectory[id]);

    /// <summary>Candidate node ids for this page (already basis-aware).</summary>
    protected abstract List<int> Collect(FileTree tree, long[] totals, string rootPath);
    protected abstract string PageName { get; }
    protected abstract string PageBlurb { get; }
    protected abstract string PageIcon { get; }
    protected virtual Brush PageIconBg => Ui.Brush("AppAccentSoft");
    protected virtual Brush PageIconFg => Ui.Brush("AppAccent");
    protected virtual string Noun => "files";

    private readonly ContentControl _headerHost = new();
    private readonly ContentControl _heroHost = new();
    private readonly StackPanel _toolbar = new();
    private readonly StackPanel _table = new();
    private readonly HashSet<int> _checked = [];
    private List<int> _filtered = [];
    private readonly Dictionary<int, Border> _rowBorders = [];

    protected FileListPage()
    {
        Root.Children.Add(_headerHost);
        Root.Children.Add(_heroHost);
        Root.Children.Add(_toolbar);
        Root.Children.Add(_table);
    }

    /// <summary>Optional hero content (stat cards, breakdowns) between header and toolbar.</summary>
    protected virtual UIElement? Hero(FileTree tree, long[] totals, List<int> ids) => null;
    protected virtual bool UsesStandardToolbar => true;

    protected override void Refresh()
    {
        _headerHost.Content = null;
        _heroHost.Content = null;
        _toolbar.Children.Clear();
        _table.Children.Clear();
        ActionBar.Content = null;
        var tree = Model.Tree;
        if (tree is null || Model.Totals.Length != tree.Count || Model.RootPath is not { } rootPath)
        {
            _table.Children.Add(NeedsScan(PageBlurb));
            return;
        }
        var totals = Model.Totals;
        _table.Children.Add(Working("Ranking…"));
        var filterText = _filterText;
        var kindFilter = _kindFilter;
        var sort = SortMode;
        if (PendingClassFilter is { } pending) { _classFilter = pending; PendingClassFilter = null; }
        Compute(() =>
        {
            var ids = Collect(tree, totals, rootPath);
            // Classify up front (bounded) so the category pills can count.
            var verdicts = new Dictionary<int, StorageVerdict>();
            foreach (int id in ids.Take(20_000))
                verdicts[id] = StorageClassifier.Classify(tree.PathOf(id, rootPath), tree.IsDirectory[id]);
            return (ids, verdicts);
        }, collected =>
        {
            var ids = collected.ids;
            _verdicts = collected.verdicts;
            _table.Children.Clear();
            _rowBorders.Clear();
            var filtered = ApplyFilters(tree, totals, ids, filterText, kindFilter, sort);
            _filtered = filtered;
            _checked.IntersectWith(filtered);
            var hero = Hero(tree, totals, filtered);
            if (hero is not null) _heroHost.Content = hero;
            if (UsesStandardToolbar) BuildToolbar(tree, totals, ids);
            long bytes = filtered.Sum(id => totals[id]);
            _headerHost.Content = Header(PageName, PageBlurb,
                $"{filtered.Count:N0} {Noun} · {ByteFormat.Format(bytes)}",
                PageIcon, PageIconBg, PageIconFg);
            _table.Children.Add(BuildTable(tree, totals, filtered.Take(500).ToList(), rootPath));
            if (filtered.Count > 500)
                _table.Children.Add(Ui.Faint($"… and {filtered.Count - 500:N0} smaller {Noun} not shown"));
            PaintBottomBar();
        });
    }

    /// <summary>
    /// WIN-051: ↑/↓/j/k move the inspector selection through this page's
    /// rows — the shared list-navigation behavior from Keyboard.swift.
    /// Returns true when the key was consumed.
    /// </summary>
    public bool NavigateSelection(int delta)
    {
        if (_filtered.Count == 0) return false;
        int at = _filtered.IndexOf(Model.SelectedNode);
        int next = at < 0 ? (delta > 0 ? 0 : _filtered.Count - 1)
            : Math.Clamp(at + delta, 0, _filtered.Count - 1);
        int previous = Model.SelectedNode;
        Model.Select(_filtered[next]);
        // Repaint the two touched rows — no table rebuild.
        foreach (int touched in new[] { previous, _filtered[next] })
        {
            if (_rowBorders.TryGetValue(touched, out var b))
                b.Background = touched == Model.SelectedNode
                    ? Ui.Brush("AppRowSelected") : Brushes.Transparent;
        }
        return true;
    }

    /// <summary>Reveal/Open on Enter — dirs drill, files reveal in Explorer.</summary>
    public bool ActivateSelection()
    {
        var tree = Model.Tree;
        if (tree is null || Model.SelectedNode < 0 || Model.SelectedNode >= tree.Count) return false;
        int id = Model.SelectedNode;
        if (tree.IsDirectory[id]) { Model.DrillTo(id); Model.ShowPage("File Browser"); }
        else Explorer.Reveal(Model.PathOf(id));
        return true;
    }

    /// <summary>Delete/Ctrl+Backspace stages the selected row via this page's reason.</summary>
    public bool StageSelection()
    {
        if (Model.SelectedNode < 0) return false;
        return Model.Stage(Model.SelectedNode, PageName.ToLowerInvariant());
    }

    /// <summary>WIN-052: Ctrl+A checks every filtered row.</summary>
    public void SelectAll()
    {
        _checked.UnionWith(_filtered);
        PaintBottomBar();
        foreach (var cb in EnumerateCheckBoxes()) cb.IsChecked = true;
    }

    /// <summary>WIN-052: Esc clears the checked set.</summary>
    public void ClearChecked()
    {
        _checked.Clear();
        PaintBottomBar();
        foreach (var cb in EnumerateCheckBoxes()) cb.IsChecked = false;
    }

    private IEnumerable<CheckBox> EnumerateCheckBoxes()
    {
        foreach (var border in _table.Children.OfType<Border>())
            if (border.Child is Grid row && row.Children.OfType<CheckBox>().FirstOrDefault() is { } cb)
                yield return cb;
    }

    private void PaintSelected() { /* per-row closures handle hover; keyboard nav repaints via _rowBorders */ }

    /// <summary>The pinned selection bar (§13): select all, running total, add in one go.</summary>
    protected void PaintBottomBar()
    {
        var totals = Model.Totals;
        var selected = _filtered.Where(_checked.Contains).ToList();
        long selBytes = selected.Sum(id => totals[id]);
        var copy = Ui.Button("Copy Path", Icons.Copy, Ui.ButtonStyle.Outline, () =>
        {
            var paths = selected.Select(id => TreeExporter.QuotePathIfNeeded(Model.PathOf(id)));
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
        });
        ActionBar.Content = SelectionBar(_filtered.Count, selected.Count, selBytes,
            SelectAll, ClearChecked,
            () =>
            {
                Model.ConfirmStageMany(selected, PageName.ToLowerInvariant(),
                    $"Add {selected.Count:N0} item{(selected.Count == 1 ? "" : "s")} from {PageName} to Cleanup?");
                ClearChecked();
            },
            copy);
    }

    private void BuildToolbar(FileTree tree, long[] totals, List<int> ids)
    {
        var kinds = ids.Select(id => FileTypes.KindOf(tree, id))
            .GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => g.Key)
            .Where(k => k != FileTypes.FolderId).Take(6).ToList();

        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        var (box, input) = Ui.SearchBox("Search by name or path…", 260);
        input.Text = _filterText;
        input.TextChanged += (_, _) =>
        {
            _filterText = input.Text;
            RefreshTableOnly(tree, totals, ids);
        };
        row.Children.Add(new Border { Margin = new Thickness(0, 0, 10, 4), Child = box });

        var pills = new List<(string Id, string Label)> { ("all", $"All {Noun}") };
        pills.AddRange(kinds.Select(k => (k, FileTypes.LabelOf(k))));
        foreach (var (id, label) in pills)
        {
            var pill = Ui.Pill(label, _kindFilter == id, () =>
            {
                _kindFilter = id;
                RefreshTableOnly(tree, totals, ids);
            });
            pill.Margin = new Thickness(0, 0, 6, 4);
            row.Children.Add(pill);
        }

        var sort = new ComboBox
        {
            Width = 130, Height = 28, FontSize = Ui.Scaled(12),
            Margin = new Thickness(4, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (id, label) in new[]
        {
            ("largest", "Sort: Largest"), ("smallest", "Sort: Smallest"),
            ("name", "Sort: Name"), ("oldest", "Sort: Oldest"), ("newest", "Sort: Newest"),
        })
        {
            var item = new ComboBoxItem { Content = label, Tag = id };
            sort.Items.Add(item);
            if (id == SortMode) sort.SelectedItem = item;
        }
        sort.SelectionChanged += (_, _) =>
        {
            SortMode = (sort.SelectedItem as ComboBoxItem)?.Tag as string ?? "largest";
            RefreshTableOnly(tree, totals, ids);
        };
        row.Children.Add(sort);
        _toolbar.Children.Add(row);

        // Category view: what the rows are (WSL & Docker disks, AI models,
        // code, installers…) — a second way to slice the same list.
        var classes = ids.Where(_verdicts.ContainsKey)
            .GroupBy(id => _verdicts[id].Class)
            .Select(g => (Class: g.Key, Bytes: g.Sum(id => totals[id])))
            .OrderByDescending(c => c.Class == StorageClassifier.Other ? -1 : c.Bytes)
            .ToList();
        if (classes.Count > 1)
        {
            var catRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            var label = Ui.MonoLabel("CATEGORY", Ui.Brush("AppSubtle"));
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 10, 4);
            catRow.Children.Add(label);
            void Choose(string id)
            {
                _classFilter = id;
                _toolbar.Children.Clear();
                BuildToolbar(tree, totals, ids);
                RefreshTableOnly(tree, totals, ids);
            }
            // The biggest few as pills (plus the active one); the long tail
            // in a dropdown so the toolbar stays compact.
            const int pillCount = 7;
            var pillClasses = classes.Take(pillCount).ToList();
            if (classes.FindIndex(c => c.Class.Id == _classFilter) is >= pillCount and var active)
                pillClasses.Add(classes[active]);
            var catPills = new List<(string Id, string Label)> { ("all", "All") };
            catPills.AddRange(pillClasses.Select(c => (c.Class.Id, $"{c.Class.Title} · {ByteFormat.Format(c.Bytes)}")));
            foreach (var (id, text) in catPills)
            {
                string captured = id;
                var pill = Ui.Pill(text, _classFilter == id, () => Choose(captured));
                pill.Margin = new Thickness(0, 0, 6, 4);
                catRow.Children.Add(pill);
            }
            var rest = classes.Skip(pillCount).Where(c => c.Class.Id != _classFilter).ToList();
            if (rest.Count > 0)
            {
                var more = new ComboBox
                {
                    Height = 28, FontSize = Ui.Scaled(12), MinWidth = 150,
                    Margin = new Thickness(0, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center,
                };
                more.Items.Add(new ComboBoxItem { Content = $"{rest.Count} more categories…", Tag = "" });
                foreach (var c in rest)
                    more.Items.Add(new ComboBoxItem { Content = $"{c.Class.Title} · {ByteFormat.Format(c.Bytes)}", Tag = c.Class.Id });
                more.SelectedIndex = 0;
                more.SelectionChanged += (_, _) =>
                {
                    if ((more.SelectedItem as ComboBoxItem)?.Tag is string { Length: > 0 } id) Choose(id);
                };
                catRow.Children.Add(more);
            }
            _toolbar.Children.Add(catRow);
        }
    }

    protected void RefreshTableOnly(FileTree tree, long[] totals, List<int> ids)
    {
        // Rebuild just the table region: header and toolbar stay.
        var filtered = ApplyFilters(tree, totals, ids, _filterText, _kindFilter, SortMode);
        _filtered = filtered;
        _checked.IntersectWith(filtered);
        _table.Children.Clear();
        _table.Children.Add(BuildTable(tree, totals, filtered.Take(500).ToList(), Model.RootPath ?? ""));
        if (filtered.Count > 500)
            _table.Children.Add(Ui.Faint($"… and {filtered.Count - 500:N0} smaller {Noun} not shown"));
        PaintBottomBar();
    }

    private List<int> ApplyFilters(FileTree tree, long[] totals, List<int> ids,
        string filterText, string kindFilter, string sort)
    {
        IEnumerable<int> q = ids;
        if (kindFilter != "all")
            q = q.Where(id => FileTypes.KindOf(tree, id) == kindFilter);
        if (_classFilter != "all")
            q = q.Where(id => _verdicts.TryGetValue(id, out var v) && v.Class.Id == _classFilter);
        if (filterText.Trim().Length > 0)
        {
            string needle = filterText.Trim();
            q = q.Where(id => tree.NameOf(id).Contains(needle, StringComparison.OrdinalIgnoreCase)
                || Model.DisplayPath(id).Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        q = sort switch
        {
            "smallest" => q.OrderBy(id => totals[id]),
            "name" => q.OrderBy(id => tree.NameOf(id), StringComparer.OrdinalIgnoreCase),
            "oldest" => q.OrderBy(id => tree.ModifiedDay[id] == 0 ? int.MaxValue : tree.ModifiedDay[id]),
            "newest" => q.OrderByDescending(id => tree.ModifiedDay[id]),
            _ => q.OrderByDescending(id => totals[id]),
        };
        return q.ToList();
    }

    /// <summary>The reference table: ☑, #, name cell, type badge, modified, size, ⋯.</summary>
    protected UIElement BuildTable(FileTree tree, long[] totals, List<int> ids, string rootPath)
    {
        var table = new StackPanel();
        var headerRow = Ui.TableRowGrid(
            new GridLength(30), new GridLength(30), new GridLength(1, GridUnitType.Star),
            new GridLength(170), new GridLength(110), new GridLength(90), new GridLength(36));
        headerRow.MinHeight = 0;
        headerRow.Height = 28;
        headerRow.Margin = new Thickness(0);
        var selectAll = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = ids.Count > 0 && ids.All(_checked.Contains),
        };
        selectAll.Checked += (_, _) =>
        {
            foreach (var i in _filtered) _checked.Add(i);
            RebuildRows();
            PaintBottomBar();
        };
        selectAll.Unchecked += (_, _) =>
        {
            _checked.Clear();
            RebuildRows();
            PaintBottomBar();
        };
        Ui.Cell(headerRow, selectAll, 0);
        Ui.Cell(headerRow, Ui.Mono("#", 10, null, Ui.Brush("AppFaint")), 1);
        Ui.Cell(headerRow, Ui.TableHead("Name"), 2);
        Ui.Cell(headerRow, Ui.TableHead("Category"), 3);
        Ui.Cell(headerRow, Ui.TableHead("Modified"), 4);
        Ui.Cell(headerRow, Ui.TableHead("Size"), 5, right: true);
        table.Children.Add(headerRow);
        table.Children.Add(Ui.Hairline());

        _rowsHost = new StackPanel();
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) _rowsHost.Children.Add(Ui.Hairline());
            _rowsHost.Children.Add(FileRow(tree, totals, ids[i], i + 1, rootPath));
        }
        if (ids.Count == 0)
        {
            var empty = Ui.Subtle("Nothing matches these filters.");
            empty.Margin = new Thickness(0, 8, 0, 8);
            _rowsHost.Children.Add(empty);
        }
        _rowsHost.Tag = (tree, ids, rootPath);
        table.Children.Add(_rowsHost);
        return table;
    }

    private StackPanel? _rowsHost;

    /// <summary>Re-render the row stack in place — used when select-all toggles.</summary>
    private void RebuildRows()
    {
        if (_rowsHost?.Tag is not (FileTree tree, List<int> ids, string rootPath)) return;
        var totals = Model.Totals;
        _rowsHost.Children.Clear();
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) _rowsHost.Children.Add(Ui.Hairline());
            _rowsHost.Children.Add(FileRow(tree, totals, ids[i], i + 1, rootPath));
        }
    }

    private UIElement FileRow(FileTree tree, long[] totals, int id, int rank, string rootPath)
    {
        string kind = FileTypes.KindOf(tree, id);
        var kindBrush = Ui.KindColor(kind);
        bool isDir = tree.IsDirectory[id];

        var outer = new Border { CornerRadius = new CornerRadius(6), Background = Brushes.Transparent };
        _rowBorders[id] = outer;
        var row = Ui.TableRowGrid(
            new GridLength(30), new GridLength(30), new GridLength(1, GridUnitType.Star),
            new GridLength(170), new GridLength(110), new GridLength(90), new GridLength(36));
        outer.Child = row;
        void PaintSelected() => outer.Background =
            Model.SelectedNode == id ? Ui.Brush("AppRowSelected") : Brushes.Transparent;
        PaintSelected();

        var check = new CheckBox { IsChecked = _checked.Contains(id), VerticalAlignment = VerticalAlignment.Center };
        check.Checked += (_, _) => { _checked.Add(id); PaintBottomBar(); };
        check.Unchecked += (_, _) => { _checked.Remove(id); PaintBottomBar(); };
        Ui.Cell(row, check, 0);
        Ui.Cell(row, Ui.Mono(rank.ToString(), 10, null, Ui.Brush("AppFaint")), 1);
        // WIN-046: cloud-only rows get the glyph — the bytes sit online,
        // and Reveal would recall the content.
        bool cloud = (tree.Flags[id] & NodeFlags.NotDownloaded) != 0;
        var nameCell = Ui.NameCell(
            cloud ? "☁" : Icons.ForKind(kind),
            isDir || cloud ? Ui.Brush("AppHover")
                : Ui.Tint(((SolidColorBrush)kindBrush).Color, 41),
            isDir || cloud ? Ui.Brush("AppSubtle") : kindBrush,
            tree.NameOf(id),
            cloud
                ? $"{Model.DisplayPath(id)}  ·  cloud-only — opening downloads it"
                : Model.DisplayPath(id), 26);
        Ui.Cell(row, nameCell, 2);
        Ui.Cell(row, CategoryBadge(VerdictFor(tree, id, rootPath), kind), 3);
        Ui.Cell(row, Ui.Mono(Ui.RelativeDay(tree.ModifiedDay[id]), 11, null, Ui.Brush("AppSubtle")), 4);
        Ui.Cell(row, Ui.Mono(ByteFormat.Format(totals[id]), 12), 5, right: true);
        Ui.Cell(row, Ui.MoreButton(() => OpenRowMenu(outer, tree, id)), 6, right: true);

        outer.Cursor = System.Windows.Input.Cursors.Hand;
        outer.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                // Double-click: dirs drill, files reveal in Explorer —
                // the macOS FindView open-on-double-click behavior.
                if (isDir) { Model.DrillTo(id); Model.ShowPage("File Browser"); }
                else Explorer.Reveal(Model.PathOf(id));
            }
            else { Model.Select(id); PaintSelected(); }
        };
        outer.MouseEnter += (_, _) => { if (Model.SelectedNode != id) outer.Background = Ui.Brush("AppHover"); };
        outer.MouseLeave += (_, _) => PaintSelected();
        return outer;
    }

    /// <summary>
    /// The row's category: the storage class in its color (⚠ and a tooltip
    /// for risky ones — a WSL distro, an app, a browser profile); plain
    /// rows that no rule recognizes fall back to the file-kind badge.
    /// </summary>
    private static FrameworkElement CategoryBadge(StorageVerdict verdict, string kind)
    {
        if (verdict.Class == StorageClassifier.Other) return Ui.KindBadge(kind);
        var color = ((SolidColorBrush)Ui.Hex(verdict.Class.ColorHex)).Color;
        bool risky = verdict.Advice is CleanupAdvice.Warn or CleanupAdvice.Never;
        var badge = Ui.Badge((risky ? "⚠ " : "") + verdict.Class.Title,
            risky ? Ui.Brush("AppWarning") : new SolidColorBrush(color),
            risky ? Ui.Brush("AppWarningBg") : Ui.Tint(color, 36));
        badge.HorizontalAlignment = HorizontalAlignment.Left;
        if (verdict.Note is not null || verdict.Instead is not null)
            badge.ToolTip = string.Join("\n\n", new[] { verdict.Note, verdict.Instead }.Where(t => t is not null));
        return badge;
    }

    private void OpenRowMenu(UIElement anchor, FileTree tree, int id)
    {
        Model.Select(id);
        string path = Model.PathOf(id);
        var menu = new ContextMenu();
        var reveal = new MenuItem { Header = "Reveal in Explorer" };
        reveal.Click += (_, _) => Explorer.Reveal(path);
        menu.Items.Add(reveal);
        var copy = new MenuItem { Header = "Copy Path" };
        copy.Click += (_, _) => Clipboard.SetText(path);
        menu.Items.Add(copy);
        if (tree.IsDirectory[id])
        {
            var vis = new MenuItem { Header = "Show in Visualize" };
            vis.Click += (_, _) => Model.Visualize(id);
            menu.Items.Add(vis);
        }
        else
        {
            // Show-in-map: the file's parent folder in Visualize, selected.
            var show = new MenuItem { Header = "Show in Visualize" };
            int parent = tree.Parent[id];
            show.Click += (_, _) =>
            {
                if (parent >= 0) Model.DrillTo(parent);
                Model.Select(id);
                Model.ShowPage("Visualize");
            };
            menu.Items.Add(show);
        }
        menu.Items.Add(new Separator());
        var stage = new MenuItem { Header = "Add to Cleanup" };
        stage.Click += (_, _) => Model.Stage(id, PageName.ToLowerInvariant());
        menu.Items.Add(stage);
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }
}

/// <summary>Biggest Files — ranked individual files, folders excluded.</summary>
public sealed class BiggestFilesPage : FileListPage
{
    protected override string PageName => "Biggest Files";
    protected override string PageIcon => Icons.BiggestFiles;
    protected override string PageBlurb =>
        "The largest individual files using storage on this drive. Folders are not shown here.";
    protected override List<int> Collect(FileTree tree, long[] totals, string rootPath) =>
        TopSizes.Largest(1, tree.Count, 5000, id => totals[id],
            id => !tree.IsDirectory[id] && totals[id] > 0);
}

/// <summary>Biggest Folders — ranked directories by subtree total.</summary>
public sealed class BiggestFoldersPage : FileListPage
{
    protected override string PageName => "Biggest Folders";
    protected override string PageIcon => Icons.BiggestFolders;
    protected override Brush PageIconBg => Ui.Hex("#FEF3DE");
    protected override Brush PageIconFg => Ui.Hex("#B45309");
    protected override string PageBlurb =>
        "The largest folders by total size, including everything inside them.";
    protected override string Noun => "folders";
    protected override List<int> Collect(FileTree tree, long[] totals, string rootPath) =>
        TopSizes.Largest(1, tree.Count, 3000, id => totals[id],
            id => tree.IsDirectory[id] && totals[id] > 0);
}

/// <summary>Forgotten Files — large files untouched for over a year.</summary>
public sealed class ForgottenFilesPage : FileListPage
{
    protected override string PageName => "Forgotten Files";
    protected override string PageIcon => Icons.Forgotten;
    protected override Brush PageIconBg => Ui.Hex("#FEF3DE");
    protected override Brush PageIconFg => Ui.Hex("#B45309");
    protected override string PageBlurb =>
        "Large files you haven't touched in over a year — the best candidates to review.";

    /// <summary>WIN-045: a clicked age band filters the list; null = all.</summary>
    private AgeBucket? _band;

    protected override List<int> Collect(FileTree tree, long[] totals, string rootPath)
    {
        int today = AgeMap.Today();
        var ids = AgeMap.Untouched(tree, totals, today, limit: 1000);
        if (_band is { } band)
            ids = ids.Where(id => AgeMap.Bucket(tree.ModifiedDay[id], today) == band).ToList();
        return ids;
    }

    /// <summary>Stat cards + segmented age bar, like the mockup.</summary>
    protected override UIElement? Hero(FileTree tree, long[] totals, List<int> ids)
    {
        int today = AgeMap.Today();
        var bucketBytes = new Dictionary<AgeBucket, long>();
        foreach (var id in ids)
        {
            var b = AgeMap.Bucket(tree.ModifiedDay[id], today);
            bucketBytes[b] = bucketBytes.GetValueOrDefault(b) + totals[id];
        }
        long total = ids.Sum(id => totals[id]);
        long year1 = ids.Where(id => today - tree.ModifiedDay[id] > 365).Sum(id => totals[id]);
        long year2 = ids.Where(id => today - tree.ModifiedDay[id] > 2 * 365).Sum(id => totals[id]);
        long big = ids.Where(id => totals[id] >= 1_000_000_000).Sum(id => totals[id]);

        var stats = Ui.StatRow(
            Ui.StatCard(Icons.Forgotten, Ui.Hex("#FEF3DE"), Ui.Hex("#B45309"),
                ByteFormat.Format(total), $"Reviewable · {ids.Count:N0} files"),
            Ui.StatCard(Icons.Forgotten, Ui.Hex("#FDECEC"), Ui.Hex("#DC2626"),
                ByteFormat.Format(year1), "Over a year untouched"),
            Ui.StatCard(Icons.Forgotten, Ui.Hex("#F3E8FF"), Ui.Hex("#7C3AED"),
                ByteFormat.Format(year2), "Over two years"),
            Ui.StatCard(Icons.BiggestFiles, Ui.Brush("AppAccentSoft"), Ui.Brush("AppAccent"),
                ByteFormat.Format(big), "In files over 1 GB"));

        var order = new[] { AgeBucket.OneToTwoYears, AgeBucket.OverTwoYears };
        var colors = new Dictionary<AgeBucket, Brush>
        {
            [AgeBucket.Under30] = Ui.Hex("#7BA89C"), [AgeBucket.Days30To90] = Ui.Hex("#8FACC0"),
            [AgeBucket.Days90To365] = Ui.Hex("#B9A071"), [AgeBucket.OneToTwoYears] = Ui.Hex("#C99A7E"),
            [AgeBucket.OverTwoYears] = Ui.Hex("#C78797"), [AgeBucket.Unknown] = Ui.Brush("AppFaint"),
        };
        var present = order.Where(b => bucketBytes.GetValueOrDefault(b) > 0).ToList();
        long barTotal = Math.Max(1, present.Sum(b => bucketBytes[b]));
        var barCard = new StackPanel();
        barCard.Children.Add(Ui.T("Forgotten files by age", 13, FontWeights.SemiBold));
        barCard.Children.Add(new Border { Height = 8 });
        barCard.Children.Add(Ui.Segmented(
            present.Select(b => ((double)bucketBytes[b] / barTotal, colors[b])).ToList(), 10));
        barCard.Children.Add(new Border { Height = 8 });
        // WIN-045: legend dots are the filter — click a band to list just
        // those files; the active band shows as selected.
        var legend = new WrapPanel();
        foreach (var b in present)
        {
            bool on = _band == b;
            var dot = Ui.LegendDot(colors[b],
                $"{(on ? "• " : "")}{AgeMap.Title(b)}  {ByteFormat.Format(bucketBytes[b])} ({bucketBytes[b] * 100 / barTotal}%)");
            dot.Cursor = System.Windows.Input.Cursors.Hand;
            if (dot.Children.Count > 1 && dot.Children[1] is TextBlock tb)
                tb.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            var captured = b;
            dot.ToolTip = new ToolTip { Content = on ? "Show all bands" : "Show only this band" };
            dot.MouseLeftButtonDown += (_, _) =>
            {
                _band = on ? null : captured;
                Refresh();
            };
            legend.Children.Add(dot);
        }
        if (_band is not null)
        {
            var clear = Ui.Pill("Clear age filter ×", false, () => { _band = null; Refresh(); });
            clear.Margin = new Thickness(0, 0, 0, 2);
            legend.Children.Add(clear);
        }
        barCard.Children.Add(legend);

        var wrap = new StackPanel();
        wrap.Children.Add(stats);
        wrap.Children.Add(Ui.Card(barCard, 14));
        return wrap;
    }
}

/// <summary>Large Media — video, audio and image files over 100 MB.</summary>
public sealed class LargeMediaPage : FileListPage
{
    private const long MinBytes = 100L * 1000 * 1000;
    private static readonly HashSet<string> MediaKinds = new(StringComparer.Ordinal)
        { "video", "audio", "image" };

    protected override string PageName => "Large Media";
    protected override string PageIcon => Icons.Media;
    protected override Brush PageIconBg => Ui.Hex("#F3E8FF");
    protected override Brush PageIconFg => Ui.Hex("#7C3AED");
    protected override string PageBlurb =>
        "Find the videos, photos and audio using the most space on this drive. Nothing is deleted automatically.";
    protected override List<int> Collect(FileTree tree, long[] totals, string rootPath) =>
        TopSizes.Largest(1, tree.Count, 3000, id => totals[id],
            id => !tree.IsDirectory[id] && totals[id] >= MinBytes
                && MediaKinds.Contains(FileTypes.KindOfFile(tree.NameOf(id))));

    protected override UIElement? Hero(FileTree tree, long[] totals, List<int> ids)
    {
        long total = ids.Sum(id => totals[id]);
        var byKind = ids.GroupBy(id => FileTypes.KindOfFile(tree.NameOf(id)))
            .ToDictionary(g => g.Key, g => (Bytes: g.Sum(i => totals[i]), Count: g.Count()));
        long Video(string k) => byKind.GetValueOrDefault(k).Bytes;
        int Count(string k) => byKind.GetValueOrDefault(k).Count;

        var stats = Ui.StatRow(
            Ui.StatCard(Icons.Media, Ui.Hex("#F3E8FF"), Ui.Hex("#7C3AED"),
                ByteFormat.Format(total), "Media storage", $"in {ids.Count:N0} files"),
            Ui.StatCard(Icons.Media, Ui.Hex("#FDECEC"), Ui.Hex("#DC2626"),
                ByteFormat.Format(Video("video")), "Video files", $"in {Count("video"):N0} files"),
            Ui.StatCard(Icons.Image, Ui.Hex("#E8F6EE"), Ui.Hex("#16A34A"),
                ByteFormat.Format(Video("image")), "Images & photos", $"in {Count("image"):N0} files"),
            Ui.StatCard(Icons.Audio, Ui.Hex("#EAF1FE"), Ui.Hex("#2563EB"),
                ByteFormat.Format(Video("audio")), "Audio files", $"in {Count("audio"):N0} files"));

        // Breakdown cards: by type | age.
        var byType = new StackPanel();
        byType.Children.Add(Ui.T("Media storage by type", 13, FontWeights.SemiBold));
        var kindColors = new Dictionary<string, Brush>
        {
            ["video"] = Ui.Hex("#C78797"), ["image"] = Ui.Hex("#8FACC0"),
            ["audio"] = Ui.Hex("#A795C7"), ["other"] = Ui.Hex("#A2A4AC"),
        };
        foreach (var kv in byKind.OrderByDescending(k => k.Value.Bytes).Take(5))
        {
            long bytes = kv.Value.Bytes;
            byType.Children.Add(Ui.DotRow(kindColors.GetValueOrDefault(kv.Key, Ui.Brush("AppBarTrack")),
                FileTypes.LabelOf(kv.Key),
                $"{ByteFormat.Format(bytes)} ({bytes * 100 / Math.Max(1, total):0.0}%)",
                (double)bytes / Math.Max(1, total)));
        }
        var age = new StackPanel();
        age.Children.Add(Ui.T("Age distribution", 13, FontWeights.SemiBold));
        int today = AgeMap.Today();
        var ageBytes = new Dictionary<AgeBucket, long>();
        foreach (var id in ids)
        {
            var b = AgeMap.Bucket(tree.ModifiedDay[id], today);
            ageBytes[b] = ageBytes.GetValueOrDefault(b) + totals[id];
        }
        var ageColors = new Dictionary<AgeBucket, Brush>
        {
            [AgeBucket.Under30] = Ui.Hex("#7BA89C"), [AgeBucket.Days30To90] = Ui.Hex("#8FACC0"),
            [AgeBucket.Days90To365] = Ui.Hex("#B9A071"), [AgeBucket.OneToTwoYears] = Ui.Hex("#C99A7E"),
            [AgeBucket.OverTwoYears] = Ui.Hex("#C78797"), [AgeBucket.Unknown] = Ui.Brush("AppFaint"),
        };
        foreach (var b in new[] { AgeBucket.Under30, AgeBucket.Days30To90, AgeBucket.Days90To365,
                     AgeBucket.OneToTwoYears, AgeBucket.OverTwoYears, AgeBucket.Unknown }
                     .Where(b => ageBytes.GetValueOrDefault(b) > 0))
        {
            long bytes = ageBytes[b];
            age.Children.Add(Ui.DotRow(ageColors[b], AgeMap.Title(b),
                ByteFormat.Format(bytes), (double)bytes / Math.Max(1, total)));
        }

        var pair = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = Ui.Card(byType, 14, new Thickness(0));
        var right = Ui.Card(age, 14, new Thickness(0));
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
        pair.Children.Add(left); pair.Children.Add(right);

        var wrap = new StackPanel();
        wrap.Children.Add(stats);
        wrap.Children.Add(pair);
        return wrap;
    }
}

/// <summary>Old Downloads — files inside any "Downloads" folder.</summary>
public sealed class OldDownloadsPage : FileListPage
{
    protected override string PageName => "Old Downloads";
    protected override string PageIcon => Icons.Downloads;
    protected override Brush PageIconBg => Ui.Brush("AppSuccessBg");
    protected override Brush PageIconFg => Ui.Brush("AppSuccess");
    protected override string PageBlurb =>
        "Files sitting in Downloads — mostly installers, zips and documents you already used.";

    protected override List<int> Collect(FileTree tree, long[] totals, string rootPath)
    {
        // Find directories named Downloads, then rank the files inside them.
        var roots = new List<int>();
        var stack = new Stack<int>();
        if (tree.Count > 0) stack.Push(0);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (id != 0 && tree.IsDirectory[id]
                && tree.NameOf(id).Equals("Downloads", StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(id);
                continue; // don't nest into a second Downloads below this one
            }
            int child = tree.FirstChild[id];
            while (child != -1) { stack.Push(child); child = tree.NextSibling[child]; }
        }
        var files = new List<int>();
        foreach (var r in roots)
        {
            var inner = new Stack<int>();
            inner.Push(r);
            while (inner.Count > 0)
            {
                int id = inner.Pop();
                int child = tree.FirstChild[id];
                while (child != -1)
                {
                    if (tree.IsDirectory[child]) inner.Push(child);
                    else if (totals[child] > 0) files.Add(child);
                    child = tree.NextSibling[child];
                }
            }
        }
        return files;
    }

    protected override UIElement? Hero(FileTree tree, long[] totals, List<int> ids)
    {
        int today = AgeMap.Today();
        long total = ids.Sum(id => totals[id]);
        long days30 = ids.Where(id => today - tree.ModifiedDay[id] > 30).Sum(id => totals[id]);
        long days90 = ids.Where(id => today - tree.ModifiedDay[id] > 90).Sum(id => totals[id]);
        var big = ids.Where(id => totals[id] >= 100_000_000).ToList();

        var stats = Ui.StatRow(
            Ui.StatCard(Icons.Downloads, Ui.Brush("AppSuccessBg"), Ui.Brush("AppSuccess"),
                ByteFormat.Format(total), "Old downloads", $"in {ids.Count:N0} files"),
            Ui.StatCard(Icons.Forgotten, Ui.Hex("#EAF1FE"), Ui.Hex("#2563EB"),
                ByteFormat.Format(days30), "30+ days old"),
            Ui.StatCard(Icons.Forgotten, Ui.Hex("#F3E8FF"), Ui.Hex("#7C3AED"),
                ByteFormat.Format(days90), "90+ days old"),
            Ui.StatCard(Icons.BiggestFiles, Ui.Hex("#FEF3DE"), Ui.Hex("#B45309"),
                $"{big.Count:N0} files", "Worth reviewing", "> 100 MB"));

        // Green hero card — the mockup's "what's in Downloads" explainer.
        var heroText = new StackPanel();
        var top3 = ids.OrderByDescending(id => totals[id]).Take(3).ToList();
        long top3Bytes = top3.Sum(id => totals[id]);
        long yearOld = ids.Where(id => today - tree.ModifiedDay[id] > 365).Sum(id => totals[id]);
        heroText.Children.Add(Ui.T(
            $"Downloads has {ByteFormat.Format(total)} of files — " +
            $"{ByteFormat.Format(top3Bytes)} comes from {top3.Count} large files. " +
            $"{ByteFormat.Format(yearOld)} hasn't been modified in over a year. " +
            "Review the files below and add the ones you don't need to Cleanup.",
            13, null, Ui.Brush("AppForeground"), wrap: true));
        heroText.Children.Add(new Border { Height = 12 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var reveal = Ui.Button("Reveal Downloads in Explorer", null, Ui.ButtonStyle.Outline,
            () =>
            {
                if (Model.RootPath is { } rp)
                {
                    string dl = System.IO.Path.Combine(rp, "Downloads");
                    if (System.IO.Directory.Exists(dl)) Explorer.Reveal(dl);
                    else Explorer.Reveal(Model.PathOf(ids.FirstOrDefault()));
                }
            });
        actions.Children.Add(reveal);
        heroText.Children.Add(actions);
        var hero = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Ui.Brush("AppBorder"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 14, 18, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = heroText,
        };

        // Age + type breakdown side by side.
        var age = new StackPanel();
        age.Children.Add(Ui.T("Age distribution", 13, FontWeights.SemiBold));
        var ageBytes = new Dictionary<AgeBucket, long>();
        foreach (var id in ids)
        {
            var b = AgeMap.Bucket(tree.ModifiedDay[id], today);
            ageBytes[b] = ageBytes.GetValueOrDefault(b) + totals[id];
        }
        var ageColors = new Dictionary<AgeBucket, Brush>
        {
            [AgeBucket.Under30] = Ui.Hex("#7BA89C"), [AgeBucket.Days30To90] = Ui.Hex("#8FACC0"),
            [AgeBucket.Days90To365] = Ui.Hex("#B9A071"), [AgeBucket.OneToTwoYears] = Ui.Hex("#C99A7E"),
            [AgeBucket.OverTwoYears] = Ui.Hex("#C78797"), [AgeBucket.Unknown] = Ui.Brush("AppFaint"),
        };
        foreach (var b in new[] { AgeBucket.Under30, AgeBucket.Days30To90, AgeBucket.Days90To365,
                     AgeBucket.OneToTwoYears, AgeBucket.OverTwoYears, AgeBucket.Unknown }
                     .Where(b => ageBytes.GetValueOrDefault(b) > 0))
            age.Children.Add(Ui.DotRow(ageColors[b], AgeMap.Title(b),
                ByteFormat.Format(ageBytes[b]), (double)ageBytes[b] / Math.Max(1, total)));

        var byType = new StackPanel();
        byType.Children.Add(Ui.T("File type breakdown", 13, FontWeights.SemiBold));
        var kinds = ids.GroupBy(id => FileTypes.KindOfFile(tree.NameOf(id)))
            .Select(g => (Kind: g.Key, Bytes: g.Sum(i => totals[i])))
            .OrderByDescending(k => k.Bytes).Take(6).ToList();
        foreach (var k in kinds)
        {
            var cat = FileTypes.Categories.FirstOrDefault(c => c.Id == k.Kind);
            byType.Children.Add(Ui.DotRow(Ui.Hex(cat?.BadgeForeground ?? "#94A3B8"),
                FileTypes.LabelOf(k.Kind), ByteFormat.Format(k.Bytes),
                (double)k.Bytes / Math.Max(1, total)));
        }

        var pair = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = Ui.Card(age, 14, new Thickness(0));
        var right = Ui.Card(byType, 14, new Thickness(0));
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
        pair.Children.Add(left); pair.Children.Add(right);

        var wrap = new StackPanel();
        wrap.Children.Add(stats);
        wrap.Children.Add(hero);
        wrap.Children.Add(pair);
        return wrap;
    }
}

/// <summary>File Browser — children of the zoomed folder, sorted by size.</summary>
public sealed class FileBrowserPage : FileListPage
{
    protected override string PageName => "File Browser";
    protected override string PageIcon => Icons.FileBrowser;
    protected override string PageBlurb =>
        "Browse the current folder item by item — sizes, kinds and dates at a glance.";
    protected override string Noun => "items";
    protected override List<int> Collect(FileTree tree, long[] totals, string rootPath) =>
        tree.ChildrenOf(Model.ZoomedNode, totals).Select(c => c.Id).ToList();

    /// <summary>Breadcrumb + folder header card, mirroring the mockup.</summary>
    protected override UIElement? Hero(FileTree tree, long[] totals, List<int> ids)
    {
        int zoom = Model.ZoomedNode;
        // Breadcrumb: ‹ › | folder icon | segments | Focus
        var crumbs = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var chain = new List<int>();
        for (int id = zoom; id >= 0 && id != tree.Parent[id]; id = tree.Parent[id]) chain.Insert(0, id);
        if (chain.Count > 0) chain.RemoveAt(chain.Count - 1);
        foreach (var (id, i) in chain.Select((c, i) => (c, i)))
        {
            if (i > 0) crumbs.Children.Add(Ui.Faint("  ›  "));
            string label = id == 0 && Model.RootPath is { } rp
                ? rp.TrimEnd('\\', '/')
                : tree.NameOf(id);
            var t = Ui.T(label, 12.5, FontWeights.Normal, Ui.Brush("AppSubtle"));
            int captured = id;
            t.Cursor = System.Windows.Input.Cursors.Hand;
            t.MouseLeftButtonDown += (_, _) => { Model.DrillTo(captured); };
            crumbs.Children.Add(t);
        }
        var crumbRow = new DockPanel();
        var back = Ui.Button("", Icons.Back, Ui.ButtonStyle.Ghost, () => Model.GoBack());
        back.Padding = new Thickness(8, 6, 8, 6);
        var fwd = Ui.Button("", Icons.Forward, Ui.ButtonStyle.Ghost, () => Model.GoForward());
        fwd.Padding = new Thickness(8, 6, 8, 6);
        var focus = Ui.Button("Focus", null, Ui.ButtonStyle.Outline,
            () => Model.DrillTo(zoom));
        DockPanel.SetDock(focus, Dock.Right);
        crumbRow.Children.Add(focus);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(back); left.Children.Add(fwd);
        left.Children.Add(Ui.IconTile(Icons.Folder, 24, Ui.Brush("AppAccentSoft"), Ui.Brush("AppAccent"), 6));
        left.Children.Add(new Border { Width = 8 });
        left.Children.Add(crumbs);
        crumbRow.Children.Add(left);
        var crumbCard = Ui.Card(crumbRow, 10, new Thickness(0, 0, 0, 10));

        // Folder header card: icon + name/size/items + type bar with legend.
        var card = new DockPanel();
        card.Children.Add(Ui.IconTile(
            tree.IsDirectory[zoom] ? Icons.Folder : Icons.File, 40,
            Ui.Brush("AppAccentSoft"), Ui.Brush("AppAccent"), 9));
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        string name = zoom == 0 && Model.RootPath is { } rp2
            ? System.IO.Path.GetFileName(rp2.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : rp2.TrimEnd('\\', '/')
            : tree.NameOf(zoom);
        text.Children.Add(Ui.T(name, 16, FontWeights.SemiBold));
        long size = totals[zoom];
        int items = zoom < Model.Counts.Files.Length
            ? Model.Counts.Files[zoom] + Model.Counts.Folders[zoom] : 0;
        text.Children.Add(Ui.Subtle($"{ByteFormat.Format(size)} · {items:N0} items", 11.5));
        DockPanel.SetDock(text, Dock.Left);
        card.Children.Add(text);
        var parts = FileTypes.TypeBreakdown(tree, totals, zoom)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (FileTypes.LabelOf(kv.Key), kv.Value,
                Ui.Hex(FileTypes.TileColorOf(kv.Key))))
            .ToList();
        var right = new StackPanel { Width = 300, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(Ui.TypeBarWithLegend(parts, Math.Max(1, size)));
        card.Children.Add(right);

        var wrap = new StackPanel();
        wrap.Children.Add(crumbCard);
        wrap.Children.Add(Ui.Card(card, 14, new Thickness(0)));
        return wrap;
    }
}

/// <summary>
/// Search — the Find page: a query box taking the FileQuery language
/// (ext: size&gt; age&gt; name: path: in: is: type: kind:), suggestion chips
/// that toggle tokens in the text, and plain-language explanation of
/// what the query means. Bare words fall back to substring matching.
/// </summary>
public sealed class SearchPage : FileListPage
{
    private FileQuery.Result? _lastResult;
    private FileQuery.Parsed _lastParsed;
    private bool _usedQuery;
    private Dictionary<Guid, SavedSearches.Total> _savedTotals = [];

    protected override string PageName => "Find";
    protected override string PageIcon => Icons.Search;
    protected override Brush PageIconBg => Ui.Brush("AppHover");
    protected override Brush PageIconFg => Ui.Brush("AppSubtle");
    protected override string Noun => "matches";
    protected override bool UsesStandardToolbar => false;
    protected override string PageBlurb => string.IsNullOrWhiteSpace(Model.SearchQuery)
        ? "Search the scan by name, path, size, age, type or location."
        : $"Results for \"{Model.SearchQuery}\"";

    private static readonly (string Label, string Token)[] Chips =
    [
        ("Large", "size>500MB"),
        ("Old", "age>1y"),
        ("Duplicated", "is:duplicate"),
        ("Cached", "in:caches"),
        ("Media", "kind:media"),
        ("Downloads", "in:downloads"),
    ];

    protected override List<int> Collect(FileTree tree, long[] totals, string rootPath)
    {
        _lastParsed = FileQuery.Parse(Model.SearchQuery, home: UserFolders.Home, root: rootPath);
        _usedQuery = _lastParsed.Query.IsStructured;
        if (!_usedQuery)
        {
            // Bare words — the fast substring index answers directly.
            _lastResult = null;
            return FileSearch.Search(tree, totals, Model.SearchQuery, limit: 500);
        }
        var context = new FileQuery.Context(
            DuplicateFileIDs: Model.DuplicateFileIDs);
        _lastResult = _lastParsed.Query.Run(tree, rootPath, totals, context, limit: 500);
        // Saved searches get their live totals here — off the UI thread,
        // one count-only pass each.
        var saved = SavedSearches.Load();
        _savedTotals = saved.Count > 0
            ? SavedSearches.Totals(saved, tree, rootPath, totals, context)
            : [];
        return _lastResult.Ids;
    }

    /// <summary>
    /// Query box + chips + the meaning of the current query, between the
    /// header and the toolbar.
    /// </summary>
    protected override UIElement? Hero(FileTree tree, long[] totals, List<int> ids)
    {
        var hero = new StackPanel { Margin = new Thickness(0, 0, 0, Ui.GroupGap) };

        var (box, input) = Ui.SearchBox(
            "ext:mp4 size>500MB age>1y — or plain words", 340);
        input.Text = Model.SearchQuery;
        input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && input.Text.Trim().Length > 0)
            {
                Model.SearchQuery = input.Text.Trim();
                Refresh();
                e.Handled = true;
            }
        };
        var queryRow = new WrapPanel
        {
            Margin = new Thickness(0, 0, 0, 10),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var save = Ui.Button("Save search", Icons.Add, Ui.ButtonStyle.Outline, () =>
        {
            string text = Model.SearchQuery.Trim();
            if (text.Length == 0 || Model.RootPath is not { } r) return;
            var list = SavedSearches.Load();
            if (list.Count >= SavedSearches.Limit || list.Any(s => s.Query == text)) return;
            list.Add(SavedSearch.New(SavedSearches.DefaultName(text, r), text));
            SavedSearches.Save(list);
            Refresh();
        });
        save.Margin = new Thickness(Ui.InlineGap, 0, 0, 4);
        save.VerticalAlignment = VerticalAlignment.Center;
        var sort = new ComboBox
        {
            Width = 125, Height = 28, FontSize = Ui.Scaled(12),
            Margin = new Thickness(Ui.InlineGap, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (id, label) in new[]
        {
            ("largest", "Sort: Largest"), ("smallest", "Sort: Smallest"),
            ("name", "Sort: Name"), ("oldest", "Sort: Oldest"), ("newest", "Sort: Newest"),
        })
        {
            var item = new ComboBoxItem { Content = label, Tag = id };
            sort.Items.Add(item);
            if (id == SortMode) sort.SelectedItem = item;
        }
        sort.SelectionChanged += (_, _) =>
        {
            SortMode = (sort.SelectedItem as ComboBoxItem)?.Tag as string ?? "largest";
            RefreshTableOnly(tree, totals, ids);
        };
        box.Margin = new Thickness(0, 0, 0, 4);
        box.VerticalAlignment = VerticalAlignment.Center;
        sort.Margin = new Thickness(Ui.InlineGap, 0, 0, 4);
        queryRow.Children.Add(box);
        queryRow.Children.Add(sort);
        queryRow.Children.Add(save);
        hero.Children.Add(queryRow);

        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (label, token) in Chips)
        {
            bool on = FileQuery.ContainsToken(token, Model.SearchQuery);
            var pill = Ui.Pill(label, on, () =>
            {
                Model.SearchQuery = FileQuery.Toggling(token, Model.SearchQuery);
                Refresh();
            });
            pill.Margin = new Thickness(0, 0, 6, 4);
            chips.Children.Add(pill);
        }
        hero.Children.Add(chips);

        if (string.IsNullOrWhiteSpace(Model.SearchQuery))
        {
            var examples = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
            examples.Children.Add(Ui.SectionHeader("Try", "QUERY / MEANING"));
            foreach (var (query, meaning) in new[]
            {
                ("size>1GB", "Files and folders larger than 1 GB"),
                ("age>1y kind:media", "Media untouched for over a year"),
                ("in:downloads type:archive", "Archives in Downloads"),
            })
            {
                var row = new DockPanel();
                var detail = Ui.Subtle(meaning, 12);
                DockPanel.SetDock(detail, Dock.Right);
                row.Children.Add(detail);
                row.Children.Add(Ui.Mono(query, 12, FontWeights.Medium));
                string captured = query;
                examples.Children.Add(Ui.HoverRow(row, () =>
                {
                    Model.SearchQuery = captured;
                    Refresh();
                }));
            }
            hero.Children.Add(examples);
        }

        // WIN-029: saved searches — live totals over this scan; right-click
        // a pill to forget it.
        var saved = SavedSearches.Load();
        if (saved.Count > 0)
        {
            var totalsById = _savedTotals;
            var savedRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var search in saved)
            {
                string label = search.Name;
                if (totalsById.TryGetValue(search.Id, out var total) && total.Count > 0)
                    label += $"  ·  {ByteFormat.Format(total.Bytes)}";
                var pill = Ui.Pill(label, search.Query == Model.SearchQuery, () =>
                {
                    Model.SearchQuery = search.Query;
                    Refresh();
                });
                pill.Margin = new Thickness(0, 0, 6, 4);
                pill.ToolTip = new ToolTip
                {
                    Content = $"{search.Query}\nRight-click to remove",
                };
                var capturedId = search.Id;
                pill.MouseRightButtonDown += (_, _) =>
                {
                    SavedSearches.Save(SavedSearches.Load().Where(s => s.Id != capturedId));
                    Refresh();
                };
                savedRow.Children.Add(pill);
            }
            hero.Children.Add(savedRow);
        }

        var explanation = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        if (_usedQuery)
        {
            var parts = _lastParsed.Query.Describe();
            explanation.Children.Add(Ui.Subtle(string.Join("  ·  ", parts), 12));
            if (_lastParsed.Problems.Count > 0)
                explanation.Children.Add(Ui.Subtle(
                    "Not understood: " + string.Join("; ", _lastParsed.Problems.Select(p =>
                        $"{p.Token} ({p.Message})")), 11.5));
        }
        if (_lastResult is { } result)
        {
            if (result.MatchCount > result.Ids.Count)
                explanation.Children.Add(Ui.Subtle(
                    $"{result.MatchCount:N0} matches · " +
                    $"{ByteFormat.Format(result.MatchedBytes)} matched · showing the largest {result.Ids.Count}", 12));
            else if (result.MatchedBytes > 0)
                explanation.Children.Add(Ui.Subtle(
                    $"{result.MatchCount:N0} matches · {ByteFormat.Format(result.MatchedBytes)} matched", 12));
            foreach (var note in result.Notes)
                explanation.Children.Add(Ui.Subtle(note, 11.5));
            if (FileQuery.ContainsToken("is:duplicate", Model.SearchQuery))
                explanation.Children.Add(Ui.Subtle(
                    "Duplicated means in a same-content group — run Duplicates to refresh.", 11.5));
        }
        if (explanation.Children.Count > 0) hero.Children.Add(explanation);
        return hero;
    }
}
