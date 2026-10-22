using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-108: Developer (Mac: DeveloperStorageView.swift, redesign board 06). Build files,
/// dependencies and tool caches — by tool, where they live, by project and by item — and what
/// rebuilds by itself. The header and its legend, one notice, the tool strip, the tabs (Where it
/// lives · Projects · By tool · WSL &amp; SDKs, with Every item and Biggest wins under More), the
/// list, and the inspector on the right. Same metrics and words as the Mac; Xcode's tab shows
/// the Windows tools (WSL, Docker, Android, .NET SDKs) from developer-storage.json. Nothing is
/// deleted here: every Clean stages into the basket, risky items (WSL and Docker disks) keep
/// their warning, and a tool's own command is only ever copied.
/// </summary>
public sealed class DeveloperBoardPage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    public enum Tab { WhereItLives, Projects, ByTool, Tools, AllItems, Opportunities }

    internal static readonly Tab[] PrimaryTabs = [Tab.WhereItLives, Tab.Projects, Tab.ByTool, Tab.Tools];

    internal static string TabTitle(Tab tab) => tab switch
    {
        Tab.WhereItLives => "Where it lives",
        Tab.Projects => "Projects",
        Tab.ByTool => "By tool",
        Tab.Tools => "WSL & SDKs",
        Tab.AllItems => "Every item",
        _ => "Biggest wins",
    };

    /// <summary>What the inspector shows.</summary>
    internal abstract record Selection
    {
        public sealed record Folder(int Id) : Selection;
        public sealed record Project(string Id) : Selection;
        public sealed record Item(string Id) : Selection;
        public sealed record Tool(int Id) : Selection;
    }

    private Tab _tab = Tab.WhereItLives;
    private Selection? _selection;
    /// <summary>"Where it lives": the folder being shown; null = where it opens by itself.</summary>
    private int? _whereFolder;
    private string _query = "";
    /// <summary>Set from a tool tile: Every item shows only that tool.</summary>
    private DeveloperEcosystem? _ecosystemFilter;
    private readonly HashSet<string> _openTools = [];
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private object? _scanFor;
    private double _listOffset;
    private Tab _offsetTab;

    private readonly Grid _layout = new();
    private readonly ContentControl _tableHost = new() { Focusable = false };
    private readonly Border _inspectorHost = new();
    private ScrollViewer? _listScroll;

    /// <summary>The catalog is built off the UI thread once per scan and size basis (Mac: catalogGate).</summary>
    private static (FileTree Tree, long[] Totals, DeveloperCatalogResult Result)? _built;
    private static (FileTree Tree, long[] Totals)? _building;
    private static (DeveloperCatalogResult Result, DeveloperFolders Folders)? _folders;
    private static (FileTree Tree, long[] Totals, List<SystemStorageCatalog.Item> Items)? _tools;

    public DeveloperBoardPage()
    {
        Background = Ui.Brush("RdPage");
        Content = _layout;
        ModelEvents.WhileLoaded(this, Rebuild);
        Loaded += (_, _) => Model.StagedChanged += OnStaged;
        Unloaded += (_, _) => Model.StagedChanged -= OnStaged;
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != _class) Rebuild(); };
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    // ---- Pure helpers (tested in DiskMap.App.Tests) ----

    /// <summary>How many tool tiles fit before the rest fold into "N more".</summary>
    internal static int TileLimit(Rd.WidthClass c) => c switch { Rd.WidthClass.Compact => 5, Rd.WidthClass.Regular => 7, _ => 9 };

    internal static double InspectorWidth(Rd.WidthClass c) => c switch { Rd.WidthClass.Compact => 300, Rd.WidthClass.Regular => 320, _ => 360 };

    /// <summary>Items whose recipe says the Recycle Bin would damage the tool, and protected ones, are never staged.</summary>
    internal static bool CanStage(DeveloperItem item) => !item.IsProtected && item.Recipe?.TrashIsUnsafe != true;

    internal enum RowAction { None, Review, Clean }

    /// <summary>An item row's button: Clean what rebuilds, Review the rest, nothing for protected.</summary>
    internal static RowAction ActionFor(DeveloperItem item) =>
        item.Recipe?.TrashIsUnsafe == true || item.Reclaimability != DeveloperReclaimability.Reclaimable || item.IsProtected
            ? (item.IsProtected ? RowAction.None : RowAction.Review)
            : RowAction.Clean;

    /// <summary>The tag chip: the category with its size when it rebuilds, else "Keep" or "Review".</summary>
    internal static (string Text, long? Bytes, bool Accent) ItemTag(DeveloperItem item) =>
        item.Reclaimability == DeveloperReclaimability.Reclaimable
            ? (item.Category.Title(), item.Bytes, true)
            : (item.Reclaimability == DeveloperReclaimability.Keep ? "Keep" : "Review", null, false);

    internal static string ItemSubline(DeveloperItem item) => item.Reclaimability switch
    {
        DeveloperReclaimability.Reclaimable => "rebuilds",
        DeveloperReclaimability.Keep => "keep",
        _ => "not ticked",
    };

    /// <summary>The tool-storage row's add link (Mac XcodeStorageList.stageTitle).</summary>
    internal static string ToolStageTitle(SystemStorageCatalog.Item item, int count)
    {
        string size = ByteFormat.Format(item.SuggestedBytes);
        if (item.Entry.PerChild)
            return count == 1 ? $"Add the older version to the basket · {size}" : $"Add {count} older versions to the basket · {size}";
        return $"Add to basket · {size}";
    }

    /// <summary>What a tool-storage row stages: only <c>trash</c> entries, never a kept version.</summary>
    internal static List<SystemStorageCatalog.Found> ToolStageTargets(SystemStorageCatalog.Item item) =>
        item.Entry.Action != SystemStorageCatalog.Action.Trash ? [] : item.FoundPaths.Where(f => !item.Keep.Contains(f.Path)).ToList();

    // ---- Data ----

    private static readonly List<Action> _waiting = [];

    private static DeveloperCatalogResult? Catalog(FileTree tree, long[] totals, Action ready)
    {
        if (_built is { } done && ReferenceEquals(done.Tree, tree) && ReferenceEquals(done.Totals, totals)) return done.Result;
        // Every page that asks while it builds hears when it's done — a theme change makes a
        // new page mid-build, and only telling the first one left the new one loading.
        _waiting.Add(ready);
        if (_building is { } b && ReferenceEquals(b.Tree, tree) && ReferenceEquals(b.Totals, totals)) return null;
        _building = (tree, totals);
        string root = Model.RootPath ?? "";
        Task.Run(() => DeveloperCatalog.Build(tree, root, totals)).ContinueWith(task =>
        {
            var result = task.IsCompletedSuccessfully ? task.Result : DeveloperCatalogResult.EmptyResult;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (_building is { } now && ReferenceEquals(now.Tree, tree) && ReferenceEquals(now.Totals, totals)) _building = null;
                _built = (tree, totals, result);
                var waiting = _waiting.ToList();
                _waiting.Clear();
                foreach (var wake in waiting) wake();
            });
        }, TaskScheduler.Default);
        return null;
    }

    private static DeveloperFolders Folders(DeveloperCatalogResult catalog, FileTree tree)
    {
        if (_folders is { } f && ReferenceEquals(f.Result, catalog)) return f.Folders;
        var folders = DeveloperCatalog.FolderRollup(catalog, tree, i => CanStage(i) && i.Reclaimability != DeveloperReclaimability.Keep);
        _folders = (catalog, folders);
        return folders;
    }

    private static List<SystemStorageCatalog.Item> ToolStorage(FileTree tree, string root, long[] totals)
    {
        if (_tools is { } t && ReferenceEquals(t.Tree, tree) && ReferenceEquals(t.Totals, totals)) return t.Items;
        var items = SystemStorageCatalog.MeasureTree(SystemStorageCatalog.DeveloperEntries, tree, root, totals);
        _tools = (tree, totals, items);
        return items;
    }

    private static int CurrentWhere(DeveloperFolders folders, int? whereFolder) =>
        whereFolder is { } id && folders.Folders.ContainsKey(id) ? id : folders.AutoStart();

    // ---- Build ----

    private void Rebuild()
    {
        if (_listScroll is not null && _offsetTab == _tab) _listOffset = _listScroll.VerticalOffset;
        else _listOffset = 0;
        _class = Rd.ClassFor(WindowWidth);
        _layout.Children.Clear();
        _layout.ColumnDefinitions.Clear();
        _tableHost.Content = null;
        _inspectorHost.Child = null;
        _listScroll = null;
        // The list host moves into the new main column.
        if (_tableHost.Parent is Panel oldParent) oldParent.Children.Remove(_tableHost);
        if (Model.Tree is not { } tree || Model.RootPath is not { } root || Model.Totals.Length != tree.Count)
        {
            _layout.Children.Add(EmptyState("chevron.left.forwardslash.chevron.right", "Scan to see developer storage",
                "Dependencies, caches and build output, after a scan.", primary: ("Choose Folder…", () => Model.RequestScan())));
            return;
        }
        if (!ReferenceEquals(_scanFor, tree))
        {
            _scanFor = tree;
            _whereFolder = null;
            _selection = null;
        }
        var totals = Model.Totals;
        if (Catalog(tree, totals, () => { if (IsLoaded) Rebuild(); }) is not { } catalog)
        {
            _layout.Children.Add(LoadingState("Sorting developer storage…", ""));
            return;
        }

        var main = MainColumn(tree, root, catalog);
        bool showInspector = _class != Rd.WidthClass.Compact || _selection is not null;
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layout.Children.Add(main);
        if (showInspector)
        {
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(InspectorWidth(_class)) });
            var line = new Border { Background = Ui.Brush("RdLine") };
            Grid.SetColumn(line, 1);
            _layout.Children.Add(line);
            _inspectorHost.Background = Ui.Brush("RdSidebar");
            Grid.SetColumn(_inspectorHost, 2);
            _layout.Children.Add(_inspectorHost);
        }
        RenderBody(tree, root, catalog);
        _offsetTab = _tab;
    }

    /// <summary>The list and the inspector; the filter re-renders only these, so the field keeps focus.</summary>
    private void RenderBody(FileTree tree, string root, DeveloperCatalogResult catalog)
    {
        _listScroll = null;
        _tableHost.Content = _tab switch
        {
            Tab.WhereItLives => WhereList(tree, root, catalog),
            Tab.Projects => ProjectsList(catalog),
            Tab.AllItems => ItemsList(FilteredItems(catalog), _ecosystemFilter is { } eco ? $"{eco.Title()} items" : "Every item", catalog),
            Tab.Opportunities => ItemsList(Filter(catalog.Opportunities), "Biggest wins", catalog),
            Tab.ByTool => ToolsList(tree, root),
            _ => ToolStorageList(ToolStorage(tree, root, Model.Totals)),
        };
        if (_listScroll is { } scroll && _listOffset > 0)
        {
            double offset = _listOffset;
            scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(offset);
        }
        if (_layout.Children.Contains(_inspectorHost))
            _inspectorHost.Child = Inspector(tree, root, catalog);
    }

    private void Refilter()
    {
        if (Model.Tree is { } tree && Model.RootPath is { } root && _built is { } b && ReferenceEquals(b.Tree, tree))
        {
            _listOffset = 0;
            RenderBody(tree, root, b.Result);
        }
    }

    private void Select(Selection selection)
    {
        _selection = selection;
        Dispatcher.InvokeAsync(Rebuild);
    }

    private FrameworkElement MainColumn(FileTree tree, string root, DeveloperCatalogResult catalog)
    {
        double pad = _class.PagePadding();
        double gap = _class.Gap();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel { Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, 12) };
        var parts = new List<FrameworkElement> { Header(catalog.Summary) };
        if (Notice(catalog.Summary) is { } notice) parts.Add(notice);
        if (ToolTiles(catalog) is { } tiles) parts.Add(tiles);
        parts.Add(TabRow(tree, root, catalog));
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) parts[i].Margin = new Thickness(0, gap, 0, 0);
            top.Children.Add(parts[i]);
        }
        grid.Children.Add(top);
        _tableHost.Margin = new Thickness(pad, 0, pad, pad);
        Grid.SetRow(_tableHost, 1);
        grid.Children.Add(_tableHost);
        return grid;
    }

    // ---- Header ----

    private FrameworkElement Header(DeveloperSummary summary)
    {
        var dock = new DockPanel();
        if (_class != Rd.WidthClass.Compact)
        {
            var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(16, 8, 0, 0) };
            legend.Children.Add(new Border
            {
                Width = 12, Height = 12, Background = Rd.HatchBrush(Ui.Brush("AppAccent"), Ui.Brush("RdAccentWash"), 4, 1.5),
                BorderBrush = Ui.Brush("AppAccent"), BorderThickness = new Thickness(1),
            });
            var rebuilds = Rd.Text("Rebuilds", 12, null, "RdInk2");
            rebuilds.Margin = new Thickness(6, 0, 0, 0);
            legend.Children.Add(rebuilds);
            legend.Children.Add(new Border { Width = 12, Height = 12, Background = Ui.Brush("RdInk"), Margin = new Thickness(14, 0, 0, 0) });
            var keep = Rd.Text("Keep or review", 12, null, "RdInk2");
            keep.Margin = new Thickness(6, 0, 0, 0);
            legend.Children.Add(keep);
            DockPanel.SetDock(legend, Dock.Right);
            dock.Children.Add(legend);
        }
        var titles = new StackPanel();
        var title = Rd.PageTitle("");
        title.Inlines.Add(new Run("Developer") { Foreground = Ui.Brush("RdInk") });
        title.Inlines.Add(new Run(" · ") { Foreground = Ui.Brush("RdInk3") });
        title.Inlines.Add(new Run(ShellFormat.HeroBytes(summary.TotalBytes)) { Foreground = Ui.Brush("RdInk") });
        System.Windows.Automation.AutomationProperties.SetName(title, $"Developer, {ShellFormat.HeroBytes(summary.TotalBytes)}");
        titles.Children.Add(title);
        var sub = Rd.Text("", 13, null, "RdInk2");
        sub.Inlines.Add(new Run("Build files, dependencies and tool caches. "));
        sub.Inlines.Add(new Run($"{ShellFormat.HeroBytes(summary.ReclaimableBytes)} rebuilds by itself") { Foreground = Ui.Brush("RdAccentDeep") });
        sub.Inlines.Add(new Run(" with one build or install."));
        sub.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(sub);
        dock.Children.Add(titles);
        return dock;
    }

    /// <summary>One notice: stale projects, unpinned dependencies, or nothing.</summary>
    private static FrameworkElement? Notice(DeveloperSummary summary)
    {
        if (summary.StaleProjectCount > 0)
            return Rd.Banner(
                $"{DeveloperBoard.CountLabel(summary.StaleProjectCount, "project")} untouched for 6+ months "
                    + $"{(summary.StaleProjectCount == 1 ? "holds" : "hold")} {ShellFormat.HeroBytes(summary.StaleReclaimableBytes)}",
                "Measured from each project’s newest source file. Check Git before removing a whole project.");
        if (summary.UnpinnedBytes > 0)
            return Rd.Banner($"{ShellFormat.HeroBytes(summary.UnpinnedBytes)} of dependencies have no lockfile",
                RebuildCost.NetworkedUnpinned.Explanation());
        return null;
    }

    // ---- Tool tiles ----

    private FrameworkElement? ToolTiles(DeveloperCatalogResult catalog)
    {
        var tiles = DeveloperBoard.Tiles(catalog.Items, TileLimit(_class));
        if (tiles.Count == 0) return null;
        long largest = Math.Max(1, tiles[0].Bytes);
        var grid = new Grid();
        for (int i = 0; i < tiles.Count; i++)
        {
            if (i > 0)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                var line = new Border { Background = Ui.Brush("RdLine") };
                Grid.SetColumn(line, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(line);
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var view = TileView(tiles[i], largest);
            Grid.SetColumn(view, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(view);
        }
        return Rd.Card(grid);
    }

    private bool IsTileOn(DeveloperTile tile) =>
        tile.Ecosystem is not null && _ecosystemFilter == tile.Ecosystem && _tab == Tab.AllItems;

    private void ToggleTile(DeveloperTile tile)
    {
        if (IsTileOn(tile))
        {
            _ecosystemFilter = null;
            _tab = Tab.WhereItLives;
        }
        else
        {
            _ecosystemFilter = tile.Ecosystem;
            _tab = Tab.AllItems;
        }
        Rebuild();
    }

    private FrameworkElement TileView(DeveloperTile tile, long largest)
    {
        bool on = IsTileOn(tile);
        var stack = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        stack.Children.Add(Rd.Text(tile.Title, 12, null, "RdInk2"));
        var size = Rd.Figure(ShellFormat.HeroBytes(tile.Bytes), 14, FontWeights.Medium);
        size.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(size);
        var bar = ToolBar((double)tile.Bytes / largest, tile.Bytes > 0 ? (double)tile.RebuildBytes / tile.Bytes : 0);
        bar.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(bar);
        var host = new Border
        {
            Child = stack, Background = on ? Ui.Brush("RdSoft") : Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true,
            ToolTip = tile.Ecosystem is null ? "Smaller tools, together" : $"Show only {tile.Title}",
        };
        host.MouseLeftButtonUp += (_, _) => ToggleTile(tile);
        host.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { ToggleTile(tile); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(host,
            $"{tile.Title}, {ByteFormat.Format(tile.Bytes)}, {ByteFormat.Format(tile.RebuildBytes)} rebuilds by itself{(on ? ", selected" : "")}");
        return host;
    }

    /// <summary>A tool tile's bar: hatched for what rebuilds, ink for the rest, on a soft track.</summary>
    private static FrameworkElement ToolBar(double fraction, double rebuild)
    {
        var bar = new Grid { Height = 6 };
        bar.Children.Add(new Border { Background = Ui.Brush("RdSoft") });
        var fill = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var hatch = new Border { Background = Rd.HatchBrush(Ui.Brush("AppAccent"), Ui.Brush("RdAccentWash"), 5, 2) };
        var ink = new Border { Background = Ui.Brush("RdInk") };
        fill.Children.Add(hatch);
        fill.Children.Add(ink);
        bar.Children.Add(fill);
        bar.SizeChanged += (_, e) =>
        {
            double w = Math.Max(3, e.NewSize.Width * Math.Min(1, fraction));
            hatch.Width = w * rebuild;
            ink.Width = w * (1 - rebuild);
        };
        return bar;
    }

    // ---- Tabs ----

    private FrameworkElement TabRow(FileTree tree, string root, DeveloperCatalogResult catalog)
    {
        var grid = new Grid();
        void col(GridLength w) => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        col(GridLength.Auto); col(GridLength.Auto); col(new GridLength(1, GridUnitType.Star)); col(GridLength.Auto);
        var segments = Rd.SegmentSwitch(PrimaryTabs.Select(t => new Rd.Segment(TabTitle(t), _tab == t, () =>
        {
            _tab = t;
            _ecosystemFilter = null;
            Rebuild();
        })));
        segments.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(segments);
        var more = MoreMenu();
        more.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(more, 1);
        grid.Children.Add(more);
        if (_tab == Tab.WhereItLives && Breadcrumb(tree, root, catalog) is { } crumbs)
        {
            crumbs.Margin = new Thickness(12, 0, 8, 0);
            Grid.SetColumn(crumbs, 2);
            grid.Children.Add(crumbs);
        }
        if (_tab is Tab.Projects or Tab.AllItems or Tab.Opportunities)
        {
            var field = SearchField("Filter by name, path or tool", _class == Rd.WidthClass.Compact ? 180 : 260);
            field.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(field, 3);
            grid.Children.Add(field);
        }
        return grid;
    }

    private FrameworkElement MoreMenu()
    {
        string title = PrimaryTabs.Contains(_tab) ? "More" : TabTitle(_tab);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Focusable = true };
        row.Children.Add(Rd.Text(title, 13, FontWeights.Medium));
        var chevron = Ui.Glyph(Icons.ChevronDown, 8, Ui.Brush("RdInk"));
        chevron.Margin = new Thickness(4, 1, 0, 0);
        row.Children.Add(chevron);
        void open()
        {
            var menu = new ContextMenu { PlacementTarget = row, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var tab in new[] { Tab.AllItems, Tab.Opportunities })
            {
                var item = new MenuItem { Header = (_tab == tab ? "✓  " : "     ") + TabTitle(tab) };
                System.Windows.Automation.AutomationProperties.SetName(item, TabTitle(tab));
                item.Click += (_, _) =>
                {
                    _tab = tab;
                    if (tab == Tab.Opportunities) _ecosystemFilter = null;
                    Rebuild();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }
        row.MouseLeftButtonUp += (_, _) => open();
        row.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space or Key.Down) { open(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(row, title);
        return row;
    }

    /// <summary>DiskMapSearchField: glass, field, clear; 32 tall, raised fill, line (accent while focused).</summary>
    private FrameworkElement SearchField(string placeholder, double width)
    {
        var box = new TextBox
        {
            Text = _query, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = Rd.FontFor(13), FontSize = Ui.Scaled(13), Foreground = Ui.Brush("RdInk"), CaretBrush = Ui.Brush("RdInk"),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, placeholder);
        var hint = Rd.Text(placeholder, 13, null, "RdInk3");
        hint.IsHitTestVisible = false;
        hint.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var clear = Rd.IconButton(Icons.Cancel, "Clear search", () => { box.Text = ""; box.Focus(); }, 18);
        clear.Visibility = _query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        clear.ToolTip = "Clear (Esc)";
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = box.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _query = box.Text;
            Refilter();
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Escape) { box.Text = ""; e.Handled = true; } };
        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(box);
        var row = new DockPanel { Margin = new Thickness(11, 0, 11, 0) };
        var glass = Ui.Glyph(Icons.Search, 12, Ui.Brush("RdInk3"));
        glass.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(glass, Dock.Left);
        row.Children.Add(glass);
        DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(clear);
        row.Children.Add(field);
        var face = new Border
        {
            Height = 32, Width = width, CornerRadius = new CornerRadius(8), Background = Ui.Brush("RdCard"),
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1), Child = row, Cursor = Cursors.IBeam,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.GotKeyboardFocus += (_, _) => { face.BorderBrush = Rd.WithOpacity(Ui.Brush("AppAccent"), 0.75); glass.Opacity = 1; };
        box.LostKeyboardFocus += (_, _) => face.BorderBrush = Ui.Brush("RdLine");
        face.MouseLeftButtonUp += (_, _) => box.Focus();
        return face;
    }

    // ---- Where it lives (MAC-FIXES-FROM-WINDOWS §4.3) ----

    private FrameworkElement? Breadcrumb(FileTree tree, string root, DeveloperCatalogResult catalog)
    {
        var folders = Folders(catalog, tree);
        if (folders.Folders.Count == 0) return null;
        int current = CurrentWhere(folders, _whereFolder);
        var chain = tree.AncestorIds(current).Where(folders.Folders.ContainsKey).ToList();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < chain.Count; i++)
        {
            int id = chain[i];
            if (i > 0)
            {
                var sep = Rd.Text("›", 12.5, null, "RdInk3");
                sep.Margin = new Thickness(6, 0, 6, 0);
                row.Children.Add(sep);
            }
            string leaf = System.IO.Path.GetFileName(root.TrimEnd('\\', '/'));
            string label = id == 0 ? (leaf.Length == 0 ? root : leaf) : tree.NameOf(id);
            bool isCurrent = id == current;
            var crumb = Rd.Figure(label, 12.5, isCurrent ? FontWeights.SemiBold : FontWeights.Normal, isCurrent ? "RdInk" : "RdInk2");
            crumb.Cursor = Cursors.Hand;
            crumb.Focusable = true;
            void go() { _whereFolder = id; _selection = null; Rebuild(); }
            crumb.MouseLeftButtonUp += (_, _) => go();
            crumb.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { go(); e.Handled = true; } };
            System.Windows.Automation.AutomationProperties.SetName(crumb, label);
            row.Children.Add(crumb);
        }
        if (folders.Folders.TryGetValue(current, out var here))
        {
            var size = Rd.Figure(ShellFormat.HeroBytes(here.Bytes), 12, null, "RdInk3");
            size.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(size);
        }
        return new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private FrameworkElement WhereList(FileTree tree, string root, DeveloperCatalogResult catalog)
    {
        var folders = Folders(catalog, tree);
        int currentId = CurrentWhere(folders, _whereFolder);
        if (folders.Folders.Count == 0 || !folders.Folders.TryGetValue(currentId, out var current))
            return EmptyState("folder", "No developer storage found", "No dependencies, build output or tool caches in this scan.", dusty: true);
        var byId = catalog.Items.ToDictionary(i => i.Id);
        var children = current.Children.Select(c => folders.Folders.GetValueOrDefault(c)).OfType<DeveloperFolders.Folder>()
            .OrderByDescending(f => f.RemovableBytes).ThenByDescending(f => f.Bytes).ToList();
        var outside = catalog.Items
            .Where(i => i.NodeID != currentId && !FreeableIndex.IsAncestor(currentId, i.NodeID, tree))
            .OrderByDescending(i => i.Bytes).ToList();

        var stack = new StackPanel();
        var inCard = new StackPanel();
        FrameworkElement? cleanAll = null;
        if (current.RemovableBytes > 0)
        {
            var button = Rd.SecondaryButton($"Clean all {ShellFormat.HeroBytes(current.RemovableBytes)}", () => StageFolder(current, catalog));
            button.ToolTip = "Add every removable folder under here to the basket — you'll see the list first";
            cleanAll = button;
        }
        inCard.Children.Add(SectionHeader("In " + DisplayName(tree, root, currentId),
            $"{DeveloperBoard.CountLabel(current.ItemCount, "place")} · {ShellFormat.HeroBytes(current.RemovableBytes)} removable",
            _class == Rd.WidthClass.Compact ? "Removable" : "Places · removable", cleanAll));
        foreach (var child in children.Take(25)) inCard.Children.Add(WhereRow(child, tree, byId, catalog));
        if (children.Count > 25)
            inCard.Children.Add(MoreRow($"+ {DeveloperBoard.CountLabel(children.Count - 25, "smaller folder")}",
                children.Skip(25).Sum(c => c.RemovableBytes)));
        stack.Children.Add(Rd.Card(inCard));

        if (currentId != 0 && outside.Count > 0)
        {
            var outCard = new StackPanel();
            outCard.Children.Add(SectionHeader("Outside " + tree.NameOf(currentId), "Shared tool data",
                _class == Rd.WidthClass.Compact ? "" : "Places · removable", null));
            foreach (var item in outside.Take(8)) outCard.Children.Add(ItemRow(item));
            if (outside.Count > 8)
                outCard.Children.Add(MoreRow($"+ {DeveloperBoard.CountLabel(outside.Count - 8, "more item")}",
                    outside.Skip(8).Sum(i => i.Bytes), () => { _tab = Tab.AllItems; Rebuild(); }));
            var card = Rd.Card(outCard);
            card.Margin = new Thickness(0, _class.Gap(), 0, 0);
            stack.Children.Add(card);
        }
        return Scroll(stack);
    }

    private FrameworkElement WhereRow(DeveloperFolders.Folder folder, FileTree tree, Dictionary<string, DeveloperItem> byId,
        DeveloperCatalogResult catalog)
    {
        if (folder.ItemId is { } itemId && byId.TryGetValue(itemId, out var item)) return ItemRow(item);
        var removable = folder.RemovableItemIds.Select(id => byId.GetValueOrDefault(id)).OfType<DeveloperItem>()
            .OrderByDescending(i => i.Bytes).ToList();
        int day = folder.Id < tree.Count ? tree.ModifiedDay[folder.Id] : 0;
        bool canGo = folder.RemovableBytes > 0;
        return DevRow(Icons.Folder, tree.NameOf(folder.Id), DisplayPath(folder.Id),
            removable.Take(_class == Rd.WidthClass.Compact ? 2 : 3).Select(i => (i.DisplayName, (long?)i.Bytes, true)).ToList(),
            removable.Count > 3 ? $"+{removable.Count - 3} more" : null,
            DeveloperBoard.CountLabel(folder.ItemCount, "place"),
            ShellFormat.HeroBytes(canGo ? folder.RemovableBytes : folder.Bytes), canGo,
            "changed " + ExploreInspector.LastUsed(day, AgeMap.Today()).ToLowerInvariant(),
            canGo ? ("Clean", () => StageFolder(folder, catalog)) : null,
            _selection == new Selection.Folder(folder.Id),
            () => Select(new Selection.Folder(folder.Id)),
            () => { _whereFolder = folder.Id; _selection = null; Rebuild(); },
            Model.PathOf(folder.Id), canGo ? () => StageFolder(folder, catalog) : null);
    }

    private void StageFolder(DeveloperFolders.Folder folder, DeveloperCatalogResult catalog)
    {
        var ids = folder.RemovableItemIds.ToHashSet();
        Stage(catalog.Items.Where(i => ids.Contains(i.Id)).ToList(), confirm: true);
    }

    // ---- Projects ----

    private List<DeveloperProject> FilteredProjects(DeveloperCatalogResult catalog)
    {
        string q = _query.Trim();
        if (q.Length == 0) return catalog.Projects;
        return catalog.Projects.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.DisplayPath.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Ecosystem.Title().Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<DeveloperItem> ProjectItems(DeveloperProject project, DeveloperCatalogResult catalog)
    {
        var nodes = project.NodeIDs.ToHashSet();
        return catalog.Items.Where(i => nodes.Contains(i.NodeID)).OrderByDescending(i => i.Bytes).ToList();
    }

    /// <summary>Everything in a project that can go, in one confirmed add.</summary>
    private void StageProject(DeveloperProject project, DeveloperCatalogResult catalog) =>
        Stage(ProjectItems(project, catalog).Where(i => i.Reclaimability != DeveloperReclaimability.Keep).ToList(), confirm: true);

    private FrameworkElement ProjectsList(DeveloperCatalogResult catalog)
    {
        var projects = FilteredProjects(catalog).OrderByDescending(p => p.ReclaimableBytes).Take(60).ToList();
        if (projects.Count == 0)
            return EmptyState("folder", "No projects found",
                "No project folders with node_modules, bin and obj, .venv or build output in this scan.", dusty: true);
        var card = new StackPanel();
        card.Children.Add(SectionHeader("Projects",
            $"{DeveloperBoard.CountLabel(catalog.Projects.Count, "project")} · {ShellFormat.HeroBytes(catalog.Projects.Sum(p => p.ReclaimableBytes))} removable",
            _class == Rd.WidthClass.Compact ? "Removable" : "Places · removable", null));
        foreach (var project in projects)
        {
            var items = ProjectItems(project, catalog);
            bool canGo = project.ReclaimableBytes > 0;
            var p = project;
            card.Children.Add(DevRow(Icons.Folder, project.Name, project.DisplayPath,
                items.Take(_class == Rd.WidthClass.Compact ? 2 : 3)
                    .Select(i => (i.DisplayName, (long?)i.Bytes, i.Reclaimability == DeveloperReclaimability.Reclaimable)).ToList(),
                DeveloperBoard.GitShort(project.Git),
                DeveloperBoard.CountLabel(project.ItemCount, "place"),
                ShellFormat.HeroBytes(canGo ? project.ReclaimableBytes : project.Bytes), canGo,
                "changed " + DeveloperBoard.LastChange(project.LastSourceDay, AgeMap.Today()).ToLowerInvariant(),
                canGo ? ("Clean", () => StageProject(p, catalog)) : null,
                _selection == new Selection.Project(project.Id),
                () => Select(new Selection.Project(p.Id)), null,
                project.AbsolutePath, canGo ? () => StageProject(p, catalog) : null));
        }
        return Scroll(Rd.Card(card));
    }

    // ---- Items ----

    private List<DeveloperItem> Filter(IEnumerable<DeveloperItem> items)
    {
        string q = _query.Trim();
        if (q.Length == 0) return items.ToList();
        return items.Where(i => i.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || i.DisplayPath.Contains(q, StringComparison.OrdinalIgnoreCase)
            || i.Ecosystem.Title().Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private List<DeveloperItem> FilteredItems(DeveloperCatalogResult catalog)
    {
        IEnumerable<DeveloperItem> list = catalog.Items;
        if (_ecosystemFilter is { } eco) list = list.Where(i => i.Ecosystem == eco);
        return Filter(list);
    }

    private FrameworkElement ItemsList(List<DeveloperItem> items, string title, DeveloperCatalogResult catalog)
    {
        var shown = items.Take(120).ToList();
        if (shown.Count == 0) return EmptyState("shippingbox", "Nothing here", "No matching developer items.");
        var removable = shown.Where(i => CanStage(i) && i.Reclaimability == DeveloperReclaimability.Reclaimable).ToList();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (removable.Count > 0)
            actions.Children.Add(Rd.SecondaryButton($"Clean what rebuilds · {ShellFormat.HeroBytes(removable.Sum(i => i.Bytes))}",
                () => Stage(removable, confirm: true)));
        if (_ecosystemFilter is not null && _tab == Tab.AllItems)
        {
            var every = QuietButton("Show every tool", () => { _ecosystemFilter = null; Rebuild(); });
            every.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(every);
        }
        var card = new StackPanel();
        card.Children.Add(SectionHeader(title,
            $"{DeveloperBoard.CountLabel(items.Count, "item")} · {ShellFormat.HeroBytes(items.Sum(i => i.Bytes))}", "",
            actions.Children.Count > 0 ? actions : null));
        foreach (var item in shown) card.Children.Add(ItemRow(item));
        if (items.Count > shown.Count) card.Children.Add(MoreRow($"Largest 120 of {items.Count:N0} shown", null));
        return Scroll(Rd.Card(card));
    }

    private FrameworkElement ItemRow(DeveloperItem item)
    {
        var tag = ItemTag(item);
        bool rebuilds = item.Reclaimability == DeveloperReclaimability.Reclaimable;
        (string, Action)? action = ActionFor(item) switch
        {
            RowAction.Clean => ("Clean", () => Stage([item], confirm: false)),
            RowAction.Review => ("Review", () => Select(new Selection.Item(item.Id))),
            _ => null,
        };
        return DevRow(Icons.ForDeveloperItem(item), item.DisplayName, item.DisplayPath,
            [(tag.Text, tag.Bytes, tag.Accent)], DeveloperBoard.ItemNote(item), "1 place",
            ShellFormat.HeroBytes(item.Bytes), rebuilds && CanStage(item), ItemSubline(item), action,
            _selection == new Selection.Item(item.Id),
            () => Select(new Selection.Item(item.Id)), null,
            item.AbsolutePath, CanStage(item) ? () => Stage([item], confirm: false) : null);
    }

    // ---- By tool (former Regenerable Data) ----

    private static long ToolSize(int id) => id >= 0 && id < Model.Totals.Length ? Model.Totals[id] : 0;

    private FrameworkElement ToolsList(FileTree tree, string root)
    {
        if (Model.QuickWins is not { } hits)
            return LoadingState("Grouping by tool", "Matching folders against the known patterns.");
        if (hits.Count == 0)
            return EmptyState("leaf", "Nothing regenerable found", "No dependency folders, build output or tool caches in this scan.");
        var patterns = QuickWins.BundledPatterns();
        var stack = new StackPanel();
        bool first = true;
        foreach (var section in QuickWins.Sections(patterns, hits))
        {
            var group = hits.Where(h => h.Category == section.Title).OrderByDescending(h => ToolSize(h.Id)).ToList();
            if (group.Count == 0) continue;
            var card = new StackPanel();
            string category = section.Title;
            card.Children.Add(SectionHeader(section.Title,
                $"{DeveloperBoard.CountLabel(group.Count, "place")} · {ShellFormat.HeroBytes(group.Sum(h => ToolSize(h.Id)))}", "",
                Rd.SecondaryButton("Clean all", () => StageTools(group.Select(h => h.Id).ToList(), category, confirm: true))));
            if (section.Note.Length > 0)
            {
                var note = Rd.Text(section.Note, 12, null, "RdInk2", wrap: true);
                card.Children.Add(new Border
                {
                    Child = note, Padding = new Thickness(20, 10, 20, 10),
                    BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
                });
            }
            foreach (var hit in group.Take(30))
            {
                int id = hit.Id;
                string path = id < tree.Count ? tree.PathOf(id, root) : hit.Name;
                card.Children.Add(DevRow(Icons.Folder, hit.Name, CanonicalPath.DisplayPath(path), [], null, "",
                    ShellFormat.HeroBytes(ToolSize(id)), true, "rebuilds",
                    ("Clean", () => StageTools([id], category, confirm: false)),
                    _selection == new Selection.Tool(id), () => Select(new Selection.Tool(id)), null,
                    path, () => StageTools([id], category, confirm: false)));
            }
            if (group.Count > 30)
                card.Children.Add(MoreRow($"+ {DeveloperBoard.CountLabel(group.Count - 30, "smaller folder")}",
                    group.Skip(30).Sum(h => ToolSize(h.Id))));
            var boxed = Rd.Card(card);
            if (!first) boxed.Margin = new Thickness(0, _class.Gap(), 0, 0);
            first = false;
            stack.Children.Add(boxed);
        }
        return Scroll(stack);
    }

    /// <summary>Same reason prefix as the other tabs, so the basket groups them together.</summary>
    private static void StageTools(IReadOnlyList<int> ids, string category, bool confirm)
    {
        if (ids.Count == 1 && !confirm)
        {
            Model.Stage(ids[0], "Developer: " + category);
            return;
        }
        Model.ConfirmStageRequests(ids.Select(id => (id, "Developer: " + category)).ToList(), "Developer");
    }

    // ---- WSL & SDKs (Mac: XcodeStorageList) ----

    private FrameworkElement ToolStorageList(List<SystemStorageCatalog.Item> items)
    {
        if (items.Count == 0)
            return EmptyState("hammer", "No WSL, Docker or SDK storage in this scan",
                "WSL distributions, Docker's disk, Android emulators and .NET SDKs show up here once the scan includes them.");
        long total = items.Sum(i => i.Bytes);
        long freeable = items.Where(i => i.Entry.Action == SystemStorageCatalog.Action.Trash).Sum(i => i.SuggestedBytes);
        var stack = new StackPanel();
        var summary = new StackPanel { Orientation = Orientation.Horizontal };
        summary.Children.Add(Rd.Figure(ByteFormat.Format(total), 12, FontWeights.SemiBold));
        var label = Rd.Text("in WSL, Docker and SDKs", 12, null, "RdInk2");
        label.Margin = new Thickness(6, 0, 0, 0);
        summary.Children.Add(label);
        if (freeable > 0)
        {
            var dot = Rd.Text("·", 12, null, "RdInk3");
            dot.Margin = new Thickness(6, 0, 0, 0);
            summary.Children.Add(dot);
            var free = Rd.Text($"{ByteFormat.Format(freeable)} rebuilds by itself", 12, null, "AppAccent");
            free.Margin = new Thickness(6, 0, 0, 0);
            summary.Children.Add(free);
        }
        stack.Children.Add(summary);
        var rows = new StackPanel { Margin = new Thickness(16) };
        for (int i = 0; i < items.Count; i++)
        {
            var row = ToolStorageRow(items[i]);
            if (i > 0) row.Margin = new Thickness(0, 12, 0, 0);
            rows.Children.Add(row);
        }
        var card = Rd.Card(rows);
        card.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(card);
        return Scroll(stack);
    }

    /// <summary>FoldingRow: icon, title, size, chevron; the explanation, versions, steps, command and add link fold open.</summary>
    private FrameworkElement ToolStorageRow(SystemStorageCatalog.Item item)
    {
        bool open = _openTools.Contains(item.Id);
        bool trash = item.Entry.Action == SystemStorageCatalog.Action.Trash;
        var stack = new StackPanel();
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true };
        var chevron = Ui.Glyph("BsChevronRight", 10, Ui.Brush("RdInk3"));
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform(open ? 90 : 0);
        chevron.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        var size = Rd.Figure(ByteFormat.Format(item.Bytes) + (item.IsPartial ? "+" : ""), 12, FontWeights.SemiBold);
        size.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(size, Dock.Right);
        head.Children.Add(size);
        var icon = Ui.Glyph(item.Entry.Action switch
        {
            SystemStorageCatalog.Action.Never => "BsLockFill",
            SystemStorageCatalog.Action.Guide => "BsGearFill",
            _ => "BsTrash3Fill",
        }, 11, Ui.Brush(trash ? "AppAccent" : "RdInk3"));
        var iconBox = new Border { Width = 12, Child = icon, Margin = new Thickness(0, 0, 10, 0) };
        iconBox.ToolTip = trash ? "The tool rebuilds this; it can go in the basket." : "Removed with a command or in the tool.";
        DockPanel.SetDock(iconBox, Dock.Left);
        head.Children.Add(iconBox);
        head.Children.Add(Rd.Text(item.Entry.Title, 13));
        void toggle()
        {
            if (!_openTools.Remove(item.Id)) _openTools.Add(item.Id);
            Rebuild();
        }
        head.MouseLeftButtonUp += (_, _) => toggle();
        head.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { toggle(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(head, $"{item.Entry.Title}, {ByteFormat.Format(item.Bytes)}");
        System.Windows.Automation.AutomationProperties.SetHelpText(head, open ? "Hides the explanation" : "Shows what this is");
        stack.Children.Add(head);
        if (!open) return stack;

        var detail = new StackPanel { Margin = new Thickness(20, 6, 0, 0) };
        void add(FrameworkElement e) { if (detail.Children.Count > 0) e.Margin = new Thickness(e.Margin.Left, 6, e.Margin.Right, e.Margin.Bottom); detail.Children.Add(e); }
        add(Rd.Text(item.Entry.Why, 12, null, "RdInk2", wrap: true));
        if (item.Entry.PerChild)
        {
            var versions = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var found in item.FoundPaths.OrderByDescending(f => f.Bytes))
            {
                var line = new DockPanel { Margin = new Thickness(0, versions.Children.Count > 0 ? 4 : 0, 0, 0) };
                var bytes = Rd.Figure(ByteFormat.Format(found.Bytes), 12, null, "RdInk2");
                DockPanel.SetDock(bytes, Dock.Right);
                line.Children.Add(bytes);
                var name = Rd.Figure(System.IO.Path.GetFileName(found.Path.TrimEnd('\\', '/')), 12);
                DockPanel.SetDock(name, Dock.Left);
                line.Children.Add(name);
                if (item.Keep.Contains(found.Path))
                {
                    var keep = Rd.MonoLabel("Keep");
                    keep.Margin = new Thickness(8, 0, 0, 0);
                    keep.ToolTip = "The newest version for this tool stays.";
                    keep.HorizontalAlignment = HorizontalAlignment.Left;
                    line.Children.Add(keep);
                }
                else line.Children.Add(new Border());
                versions.Children.Add(line);
            }
            add(versions);
        }
        foreach (var step in item.Entry.Steps) add(Rd.Text(step, 12, null, "RdInk", wrap: true));
        if (item.Entry.Command is { } command)
        {
            var box = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
            var copy = Rd.Link("Copy", () =>
            {
                CopyText(command);
                Model.Toast("Copied — run it in a terminal");
            }, 12);
            System.Windows.Automation.AutomationProperties.SetName(copy, $"Copy {command}");
            DockPanel.SetDock(copy, Dock.Right);
            box.Children.Add(copy);
            box.Children.Add(SelectableCommand(command, 1));
            add(Rd.Surface(box, "RdSoft", 8, null));
        }
        var targets = ToolStageTargets(item);
        if (targets.Count > 0)
        {
            string title = ToolStageTitle(item, targets.Count);
            var link = Rd.Link(title, () =>
                Model.ConfirmStageRequests(targets.Select(t => (t.NodeId, item.Entry.Title)).ToList(), item.Entry.Title), 12);
            link.HorizontalAlignment = HorizontalAlignment.Left;
            add(link);
        }
        stack.Children.Add(detail);
        return stack;
    }

    // ---- Inspector ----

    private FrameworkElement Inspector(FileTree tree, string root, DeveloperCatalogResult catalog)
    {
        var sel = _selection ?? DefaultSelection(tree, catalog);
        switch (sel)
        {
            case Selection.Folder f when Folders(catalog, tree).Folders.TryGetValue(f.Id, out var folder):
                return FolderPanel(folder, tree, root, catalog);
            case Selection.Project p when catalog.Projects.FirstOrDefault(x => x.Id == p.Id) is { } project:
                return ProjectPanel(project, catalog);
            case Selection.Item i when catalog.Items.FirstOrDefault(x => x.Id == i.Id) is { } item:
                return ItemPanel(item, catalog.Projects.FirstOrDefault(x => x.Key == item.ProjectKey));
            case Selection.Tool t when t.Id >= 0 && t.Id < tree.Count:
                return ExploreInspector.Build(t.Id, _class);
            default:
                return EmptyState("shippingbox", "Select something", "See what it holds and what removing it costs.", background: "RdSidebar");
        }
    }

    /// <summary>Before a click: the biggest thing in the current tab.</summary>
    private Selection? DefaultSelection(FileTree tree, DeveloperCatalogResult catalog)
    {
        switch (_tab)
        {
            case Tab.WhereItLives:
                var folders = Folders(catalog, tree);
                if (!folders.Folders.TryGetValue(CurrentWhere(folders, _whereFolder), out var current)) return null;
                var top = current.Children.Select(c => folders.Folders.GetValueOrDefault(c)).OfType<DeveloperFolders.Folder>()
                    .MaxBy(f => f.RemovableBytes);
                if (top is null) return null;
                return top.ItemId is { } id ? new Selection.Item(id) : new Selection.Folder(top.Id);
            case Tab.Projects:
                return catalog.Projects.MaxBy(p => p.ReclaimableBytes) is { } project ? new Selection.Project(project.Id) : null;
            case Tab.Tools:
                return null;
            case Tab.AllItems:
                return FilteredItems(catalog).FirstOrDefault() is { } first ? new Selection.Item(first.Id) : null;
            case Tab.Opportunities:
                return Filter(catalog.Opportunities).FirstOrDefault() is { } win ? new Selection.Item(win.Id) : null;
            default:
                // The top row on screen: the first section's biggest folder.
                if (Model.QuickWins is not { Count: > 0 } hits) return null;
                var section = QuickWins.Sections(QuickWins.BundledPatterns(), hits).FirstOrDefault();
                if (section is null) return null;
                return hits.Where(h => h.Category == section.Title).MaxBy(h => ToolSize(h.Id)) is { } hit ? new Selection.Tool(hit.Id) : null;
        }
    }

    private FrameworkElement FolderPanel(DeveloperFolders.Folder folder, FileTree tree, string root, DeveloperCatalogResult catalog)
    {
        var byId = catalog.Items.ToDictionary(i => i.Id);
        var items = folder.ItemIds.Select(id => byId.GetValueOrDefault(id)).OfType<DeveloperItem>().OrderByDescending(i => i.Bytes).ToList();
        var removableIds = folder.RemovableItemIds.ToHashSet();
        var removable = items.Where(i => removableIds.Contains(i.Id)).ToList();
        int day = folder.Id < tree.Count ? tree.ModifiedDay[folder.Id] : 0;
        string path = Model.PathOf(folder.Id);
        bool canGo = folder.RemovableBytes > 0;
        return Panel("Selected folder", DisplayName(tree, root, folder.Id), DisplayPath(folder.Id),
            canGo ? folder.RemovableBytes : folder.Bytes, canGo ? folder.Bytes : null,
            [("Last changed", DeveloperBoard.LastChange(day, AgeMap.Today())), ("Places", $"{folder.ItemCount}")],
            removable.Take(6).Select(i => (i.DisplayName, DeveloperBoard.RebuildShort(i.RebuildCost), i.Bytes)).ToList(),
            removable.Count == 0 ? null : DeveloperBoard.ReinstallLine(removable),
            DeveloperBoard.FolderNote(DeveloperBoard.IsStale(day, AgeMap.Today())),
            path, canGo ? $"Add {ShellFormat.HeroBytes(folder.RemovableBytes)} to basket" : null,
            () => StageFolder(folder, catalog));
    }

    private FrameworkElement ProjectPanel(DeveloperProject project, DeveloperCatalogResult catalog)
    {
        var removable = ProjectItems(project, catalog).Where(i => i.Reclaimability != DeveloperReclaimability.Keep && CanStage(i)).ToList();
        bool canGo = project.ReclaimableBytes > 0;
        return Panel("Selected project", project.Name, project.DisplayPath,
            canGo ? project.ReclaimableBytes : project.Bytes, canGo ? project.Bytes : null,
            [("Last changed", DeveloperBoard.LastChange(project.LastSourceDay, AgeMap.Today())),
             ("Lockfile", project.Lockfile ?? "none found"),
             ("Git", project.Git.Title)],
            removable.Take(6).Select(i => (i.DisplayName, i.Lockfile ?? DeveloperBoard.RebuildShort(i.RebuildCost), i.Bytes)).ToList(),
            removable.Count == 0 ? null : DeveloperBoard.ReinstallLine(removable),
            DeveloperBoard.FolderNote(DeveloperBoard.IsStale(project.LastSourceDay, AgeMap.Today())),
            project.AbsolutePath,
            removable.Count == 0 ? null : $"Add {ShellFormat.HeroBytes(removable.Sum(i => i.Bytes))} to basket",
            () => StageProject(project, catalog));
    }

    /// <summary>The board's "Selected project / folder" panel: header, hero, facts, what rebuilds, the check, the note, actions.</summary>
    private FrameworkElement Panel(string label, string name, string path, long hero, long? of,
        IReadOnlyList<(string Label, string Value)> facts, IReadOnlyList<(string Name, string Hint, long Bytes)> rebuilds,
        (string Title, string Detail, bool Ok)? check, (string Title, string Body) note,
        string? revealPath, string? addTitle, Action add)
    {
        var parts = new List<FrameworkElement> { PanelHeader(label, name, path) };
        var heroText = Rd.Text(ShellFormat.HeroBytes(hero), 44, FontWeights.SemiBold, of is not null ? "AppAccent" : "RdInk");
        Typography.SetNumeralAlignment(heroText, FontNumeralAlignment.Tabular);
        System.Windows.Automation.AutomationProperties.SetName(heroText,
            of is { } total ? $"{ByteFormat.Format(hero)} can be freed of {ByteFormat.Format(total)}" : ByteFormat.Format(hero));
        if (of is { } whole && whole > hero)
            parts.Add(ExploreInspector.Shrinkable(Rd.BaselineRow(heroText, 44, Rd.Text($"of {ShellFormat.HeroBytes(whole)}", 12, null, "RdInk3"), 12, 8), 0.6));
        else
            parts.Add(ExploreInspector.Shrinkable(heroText, 0.6));
        parts.Add(Facts(facts));
        if (rebuilds.Count > 0) parts.Add(RebuildsCard(rebuilds));
        if (check is { } c) parts.Add(CheckLine(c.Title, c.Detail, c.Ok));
        parts.Add(NoteCard(note.Title, note.Body));

        var actions = new StackPanel();
        actions.Children.Add(ButtonPair(
            Rd.SecondaryButton("Reveal", () => { if (revealPath is not null) Explorer.Reveal(revealPath); }, fullWidth: true),
            Rd.SecondaryButton("Open in Terminal", () => { if (revealPath is not null) OpenTerminal(revealPath); }, fullWidth: true),
            revealPath is not null));
        if (addTitle is not null)
        {
            var button = Rd.AccentButton(addTitle, add, large: true, fullWidth: true, hard: true);
            button.Margin = new Thickness(0, 10, 3, 0);
            actions.Children.Add(button);
        }
        return PanelFrame(parts, actions, $"{label} {name}");
    }

    private FrameworkElement ItemPanel(DeveloperItem item, DeveloperProject? project)
    {
        bool staged = Model.IsStaged(item.AbsolutePath);
        bool trashUnsafe = item.Recipe?.TrashIsUnsafe == true;
        bool rebuilds = item.Reclaimability == DeveloperReclaimability.Reclaimable;
        var parts = new List<FrameworkElement> { PanelHeader("Selected item", item.DisplayName, item.DisplayPath) };
        var heroText = Rd.Text(ShellFormat.HeroBytes(item.Bytes), 44, FontWeights.SemiBold, rebuilds && !trashUnsafe ? "AppAccent" : "RdInk");
        Typography.SetNumeralAlignment(heroText, FontNumeralAlignment.Tabular);
        System.Windows.Automation.AutomationProperties.SetName(heroText, ByteFormat.Format(item.Bytes));
        parts.Add(ExploreInspector.Shrinkable(heroText, 0.6));
        var facts = new List<(string, string)> { ("Tool", item.Ecosystem.Title()), ("Rebuild", item.RebuildCost.Title()) };
        if (item.Lockfile is { } lockfile) facts.Add(("Lockfile", lockfile));
        if (project is not null)
        {
            facts.Add(("Project", project.Name));
            facts.Add(("Git", project.Git.Title));
            facts.Add(("Last source change", DeveloperBoard.LastChange(project.LastSourceDay, AgeMap.Today())));
        }
        else facts.Add(("Last changed", DeveloperBoard.LastChange(item.ModifiedDay, AgeMap.Today())));
        parts.Add(Facts(facts));
        if (item.Recipe is { } recipe)
        {
            var block = new StackPanel();
            block.Children.Add(Rd.Tag(recipe.TrashIsUnsafe ? "Don’t move to the Recycle Bin" : recipe.Title,
                recipe.TrashIsUnsafe ? Rd.TagTone.Ink : Rd.TagTone.Neutral));
            var why = Rd.Text(recipe.Why, 13, null, "RdInk2", wrap: true);
            why.Margin = new Thickness(0, 8, 0, 0);
            block.Children.Add(why);
            var commandRow = new DockPanel { Margin = new Thickness(10) };
            var copy = Rd.IconButton(Icons.Copy, $"Copy command {recipe.Command}", () =>
            {
                CopyText(recipe.Command);
                Model.Toast("Command copied — freedisk.space never runs it for you");
            }, 24);
            copy.ToolTip = "Copy command";
            copy.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(copy, Dock.Right);
            commandRow.Children.Add(copy);
            commandRow.Children.Add(SelectableCommand(recipe.Command, 3));
            var box = Rd.Surface(commandRow, "RdSoft", 8, null);
            box.Margin = new Thickness(0, 8, 0, 0);
            block.Children.Add(box);
            parts.Add(block);
        }
        parts.Add(NoteCard("What is this?", item.WhyLarge + " " + item.RebuildCost.Explanation()));
        parts.Add(ExploreInspector.SafetyLine(item.Safety));

        var actions = new StackPanel();
        actions.Children.Add(ButtonPair(
            Rd.SecondaryButton("Reveal", () => Explorer.Reveal(item.AbsolutePath), fullWidth: true),
            Rd.SecondaryButton("Show in Explore", () =>
            {
                Model.DrillTo(item.NodeID);
                Model.Select(item.NodeID);
                Model.ShowPage("Visualize");
            }, fullWidth: true), true));
        if (!trashUnsafe && !item.IsProtected)
        {
            var button = Rd.AccentButton(staged ? "In the basket" : $"Add {ShellFormat.HeroBytes(item.Bytes)} to basket", () =>
            {
                if (staged) Model.OpenBasket();
                else Stage([item], confirm: false);
            }, large: true, fullWidth: true, hard: !staged);
            button.Margin = new Thickness(0, 10, 3, 0);
            actions.Children.Add(button);
        }
        return PanelFrame(parts, actions, $"Selected item {item.DisplayName}");
    }

    private static FrameworkElement PanelHeader(string label, string name, string path)
    {
        var header = new StackPanel();
        header.Children.Add(Rd.Text(label, 12, null, "RdInk3"));
        var title = Rd.Text(name, 22, FontWeights.SemiBold, "RdInk", wrap: true);
        title.Margin = new Thickness(0, 6, 0, 0);
        header.Children.Add(ExploreInspector.Fitted(title, 2, middle: false));
        var place = Rd.Figure(path, 11, null, "RdInk3");
        place.TextWrapping = TextWrapping.Wrap;
        place.Margin = new Thickness(0, 6, 0, 0);
        place.ToolTip = path;
        header.Children.Add(ExploreInspector.Fitted(place, 2, middle: true));
        return header;
    }

    /// <summary>Scrolling content (padding 20, spacing 16) above the actions pinned at the bottom (padding 20).</summary>
    private static FrameworkElement PanelFrame(List<FrameworkElement> parts, FrameworkElement actions, string name)
    {
        var stack = new StackPanel { Margin = new Thickness(20) };
        for (int i = 0; i < parts.Count; i++)
        {
            var m = parts[i].Margin;
            parts[i].Margin = new Thickness(m.Left, m.Top + (i > 0 ? 16 : 0), m.Right, m.Bottom);
            stack.Children.Add(parts[i]);
        }
        var grid = new Grid { Background = Ui.Brush("RdSidebar") };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        System.Windows.Automation.AutomationProperties.SetName(scroll, "Inspector: " + name);
        grid.Children.Add(scroll);
        actions.Margin = new Thickness(20);
        Grid.SetRow(actions, 1);
        grid.Children.Add(actions);
        return grid;
    }

    private static FrameworkElement Facts(IReadOnlyList<(string Label, string Value)> facts)
    {
        var stack = new StackPanel();
        foreach (var (label, value) in facts)
        {
            var row = new DockPanel { Margin = new Thickness(0, stack.Children.Count > 0 ? 8 : 0, 0, 0) };
            var figure = Rd.Figure(value, 12);
            figure.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(figure, Dock.Right);
            row.Children.Add(figure);
            row.Children.Add(Rd.Text(label, 13, null, "RdInk2"));
            System.Windows.Automation.AutomationProperties.SetName(row, $"{label}: {value}");
            stack.Children.Add(row);
        }
        return stack;
    }

    private static FrameworkElement RebuildsCard(IReadOnlyList<(string Name, string Hint, long Bytes)> rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(Rd.Text("Rebuilds by itself", 12, null, "RdInk3"));
        var list = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            var (name, hint, bytes) = rows[i];
            var grid = new Grid { Margin = new Thickness(12, 0, 12, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 6 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = Rd.Text(name, 13);
            var hintText = Rd.Text(hint, 12, null, "RdInk3");
            hintText.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(hintText, 1);
            var size = Rd.Figure(ShellFormat.HeroBytes(bytes), 12);
            Grid.SetColumn(size, 3);
            grid.Children.Add(title);
            grid.Children.Add(hintText);
            grid.Children.Add(size);
            // The name gives way first; the hint and the size keep their width.
            grid.SizeChanged += (_, e) =>
            {
                hintText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                size.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                title.MaxWidth = Math.Max(20, e.NewSize.Width - hintText.DesiredSize.Width - 8 - size.DesiredSize.Width - 6);
            };
            var host = new Border
            {
                Height = 38, Child = grid,
                BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, i > 0 ? 1 : 0, 0, 0),
            };
            System.Windows.Automation.AutomationProperties.SetName(host, $"{name}, {hint}, {ShellFormat.HeroBytes(bytes)}");
            list.Children.Add(host);
        }
        var card = Rd.Surface(list, "RdCard", 10);
        card.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(card);
        return stack;
    }

    private static FrameworkElement CheckLine(string title, string detail, bool ok)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Ui.Glyph(ok ? "BsCheckCircleFill" : "BsExclamationCircleFill", 16, Ui.Brush(ok ? "AppAccent" : "AppWarning"));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 10, 0);
        grid.Children.Add(icon);
        var text = Rd.Text("", 13, null, "RdInk2", wrap: true);
        text.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("RdInk") });
        text.Inlines.Add(new Run(" " + detail));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        System.Windows.Automation.AutomationProperties.SetName(grid, $"{title} {detail}");
        return grid;
    }

    /// <summary>The note card: card fill, 1 px line, radius 10, a 3 pt accent rule on the leading edge (padding 14, spacing 6).</summary>
    private static FrameworkElement NoteCard(string title, string body)
    {
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(Rd.Text(title, 13, FontWeights.Medium));
        var text = Rd.Text(body, 13, null, "RdInk2", wrap: true);
        text.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(text);
        var layers = new Grid();
        layers.Children.Add(stack);
        layers.Children.Add(new Border
        {
            Width = 3, HorizontalAlignment = HorizontalAlignment.Left,
            Background = Ui.Brush("AppAccent"), CornerRadius = new CornerRadius(10, 0, 0, 10),
        });
        var card = Rd.Surface(layers, "RdCard", 10);
        System.Windows.Automation.AutomationProperties.SetName(card, $"{title} {body}");
        return card;
    }

    private static FrameworkElement ButtonPair(Button left, Button right, bool enabled)
    {
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        left.IsEnabled = enabled;
        right.IsEnabled = enabled;
        Grid.SetColumn(right, 2);
        pair.Children.Add(left);
        pair.Children.Add(right);
        return pair;
    }

    // ---- Pieces ----

    /// <summary>"IN ~\PROJECTS  14 PROJECTS · 21.4 GB REMOVABLE        PLACES · REMOVABLE": mono 11 medium, 44 tall, an ink rule below.</summary>
    private static FrameworkElement SectionHeader(string title, string detail, string trailing, FrameworkElement? actions)
    {
        var dock = new DockPanel { LastChildFill = true };
        if (trailing.Length > 0)
        {
            var t = Rd.Figure(trailing.ToUpperInvariant(), 11, FontWeights.Medium, "RdInk3");
            t.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(t, Dock.Right);
            dock.Children.Add(t);
        }
        if (actions is not null)
        {
            actions.Margin = new Thickness(8, 0, 0, 0);
            actions.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(actions, Dock.Right);
            dock.Children.Add(actions);
        }
        var head = Rd.Figure(title.ToUpperInvariant(), 11, FontWeights.Medium);
        head.MaxWidth = 420;
        DockPanel.SetDock(head, Dock.Left);
        dock.Children.Add(head);
        var sub = Rd.Figure(detail.ToUpperInvariant(), 11, FontWeights.Medium, "RdInk3");
        sub.Margin = new Thickness(12, 0, 0, 0);
        dock.Children.Add(sub);
        return new Border
        {
            Height = 44, Padding = new Thickness(20, 0, 20, 0), Child = dock,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>"DerivedData 4.2" — violet when it rebuilds by itself (mono 11 medium, 22 tall, radius 5).</summary>
    private static FrameworkElement Chip(string text, long? bytes, bool accent)
    {
        string ink = accent ? "RdAccentDeep" : "RdInk2";
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Rd.Figure(text, 11, FontWeights.Medium, ink));
        if (bytes is { } b)
        {
            var size = Rd.Figure(DeveloperBoard.ChipShort(b), 11, FontWeights.Medium, ink);
            size.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(size);
        }
        return new Border
        {
            Height = 22, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(5), Child = row,
            Background = accent ? Ui.Brush("RdAccentWash") : Brushes.Transparent,
            BorderBrush = accent ? Rd.WithOpacity(Ui.Brush("AppAccent"), 0.35) : Ui.Brush("RdLine"),
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>
    /// DevRow: icon tile, name and path, chips and a note; places; the figure over its subline;
    /// the action (76 wide). Selected rows get the accent wash and a 3 pt rule.
    /// </summary>
    private FrameworkElement DevRow(string icon, string name, string path,
        IReadOnlyList<(string Text, long? Bytes, bool Accent)> chips, string? note, string places,
        string figure, bool figureAccent, string subline, (string Title, Action Run)? action,
        bool selected, Action select, Action? open, string actionsPath, Action? stage)
    {
        bool compact = _class == Rd.WidthClass.Compact;
        var grid = new Grid { Margin = new Thickness(18, 12, 18, 12) };
        void col(GridLength w) => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        col(GridLength.Auto); col(new GridLength(1, GridUnitType.Star)); col(GridLength.Auto); col(GridLength.Auto); col(GridLength.Auto);

        var tile = Rd.IconTile(icon, 36);
        tile.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(tile);

        var main = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = Rd.Text(name, 14, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Bottom;
        line.Children.Add(title);
        var place = Rd.Figure(path, 11, null, "RdInk3");
        place.VerticalAlignment = VerticalAlignment.Bottom;
        place.Margin = new Thickness(8, 0, 0, 1.5);
        place.ToolTip = path;
        var fitted = ExploreInspector.Fitted(place, 1, middle: true);
        Grid.SetColumn(fitted, 1);
        line.Children.Add(fitted);
        // The name keeps its width (layoutPriority 1) up to most of the row.
        line.SizeChanged += (_, e) => title.MaxWidth = Math.Max(40, e.NewSize.Width * 0.75);
        main.Children.Add(line);
        if (chips.Count > 0 || note is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), ClipToBounds = true };
            foreach (var (text, bytes, accent) in chips)
            {
                var chip = Chip(text, bytes, accent);
                if (row.Children.Count > 0) chip.Margin = new Thickness(6, 0, 0, 0);
                row.Children.Add(chip);
            }
            if (note is not null)
            {
                var n = Rd.Text(note, 12, null, "RdInk2");
                n.Margin = new Thickness(row.Children.Count > 0 ? 6 : 0, 0, 0, 0);
                n.ToolTip = note;
                row.Children.Add(n);
            }
            main.Children.Add(row);
        }
        Grid.SetColumn(main, 1);
        grid.Children.Add(main);

        if (!compact && places.Length > 0)
        {
            var p = Rd.Figure(places, 11.5, null, "RdInk3");
            p.Margin = new Thickness(14, 0, 0, 0);
            Grid.SetColumn(p, 2);
            grid.Children.Add(p);
        }
        var figures = new StackPanel { Margin = new Thickness(14, 0, 0, 0), MinWidth = compact ? 70 : 96, VerticalAlignment = VerticalAlignment.Center };
        var big = Rd.Figure(figure, 13, FontWeights.Medium, figureAccent ? "RdAccentDeep" : "RdInk");
        big.HorizontalAlignment = HorizontalAlignment.Right;
        figures.Children.Add(big);
        var small = Rd.Figure(subline, 10.5, null, "RdInk3");
        small.HorizontalAlignment = HorizontalAlignment.Right;
        small.Margin = new Thickness(0, 2, 0, 0);
        figures.Children.Add(small);
        Grid.SetColumn(figures, 3);
        grid.Children.Add(figures);
        var actionBox = new Border { Width = 76, Margin = new Thickness(14, 0, 0, 0) };
        if (action is { } a)
        {
            var button = Rd.SecondaryButton(a.Title, a.Run);
            button.HorizontalAlignment = HorizontalAlignment.Right;
            System.Windows.Automation.AutomationProperties.SetName(button, $"{a.Title} {name}");
            actionBox.Child = button;
        }
        Grid.SetColumn(actionBox, 4);
        grid.Children.Add(actionBox);

        var layers = new Grid();
        layers.Children.Add(grid);
        if (selected)
            layers.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Brush("AppAccent") });
        var host = new Border
        {
            Child = layers, Cursor = Cursors.Hand, Focusable = true,
            Background = selected ? Ui.Brush("RdAccentWash") : Brushes.Transparent,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
        };
        host.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            if (e.ClickCount >= 2 && open is not null) open();
            else select();
            e.Handled = true;
        };
        host.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { select(); e.Handled = true; }
            else if (e.Key == Key.Enter) { (open ?? select)(); e.Handled = true; }
        };
        var menu = new ContextMenu();
        if (stage is not null) menu.Items.Add(MenuEntry("Add to basket", stage));
        menu.Items.Add(MenuEntry("Show in File Explorer", () => Explorer.Reveal(actionsPath)));
        menu.Items.Add(MenuEntry("Open", () => OpenFolder(actionsPath)));
        if (open is not null) menu.Items.Add(MenuEntry("Open in Where it lives", open));
        host.ContextMenu = menu;
        System.Windows.Automation.AutomationProperties.SetName(host, $"{name}, {figure}{(selected ? ", selected" : "")}");
        return host;
    }

    private static MenuItem MenuEntry(string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        System.Windows.Automation.AutomationProperties.SetName(item, title);
        return item;
    }

    /// <summary>"+ 3 smaller folders ……… 1.2 GB": body ink2 (a link when it has an action), 44 tall, 68 in.</summary>
    private static FrameworkElement MoreRow(string text, long? bytes, Action? action = null)
    {
        var dock = new DockPanel();
        if (bytes is { } b && b > 0)
        {
            var size = Rd.Figure(ShellFormat.HeroBytes(b), 12, null, "RdAccentDeep");
            DockPanel.SetDock(size, Dock.Right);
            dock.Children.Add(size);
        }
        var label = Rd.Text(text, 13, null, "RdInk2");
        label.HorizontalAlignment = HorizontalAlignment.Left;
        if (action is not null)
        {
            label.Cursor = Cursors.Hand;
            label.Focusable = true;
            label.MouseLeftButtonUp += (_, _) => action();
            label.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { action(); e.Handled = true; } };
        }
        System.Windows.Automation.AutomationProperties.SetName(label, text);
        dock.Children.Add(label);
        return new Border { Height = 44, Padding = new Thickness(68, 0, 18, 0), Child = dock };
    }

    /// <summary>QuietButtonStyle: ink2 13 medium text, 28 tall, hover fill.</summary>
    private static FrameworkElement QuietButton(string text, Action action)
    {
        var label = Rd.Text(text, 13, FontWeights.Medium, "RdInk2");
        var host = new Border
        {
            Height = 28, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent, Child = label, Cursor = Cursors.Hand, Focusable = true,
        };
        host.MouseEnter += (_, _) => { host.Background = Ui.Brush("RdHover"); label.Foreground = Ui.Brush("RdInk"); };
        host.MouseLeave += (_, _) => { host.Background = Brushes.Transparent; label.Foreground = Ui.Brush("RdInk2"); };
        host.MouseLeftButtonUp += (_, _) => action();
        host.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { action(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(host, text);
        return host;
    }

    /// <summary>A command shown in mono, selectable for copying by hand (Mac .textSelection(.enabled)).</summary>
    private static TextBox SelectableCommand(string command, int maxLines) => new()
    {
        Text = command, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = Ui.Scaled(12), Foreground = Ui.Brush("RdInk"),
        TextWrapping = TextWrapping.Wrap, MaxLines = maxLines, Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private FrameworkElement Scroll(FrameworkElement content)
    {
        var scroll = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        _listScroll = scroll;
        return scroll;
    }

    /// <summary>DiskMapEmptyState: glyph (or Dusty), title, message, an optional primary button.</summary>
    private static FrameworkElement EmptyState(string symbol, string title, string message, bool dusty = false,
        (string Title, Action Action)? primary = null, string background = "RdPage")
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        FrameworkElement mark = dusty && AppSettings.Load().ShowDusty
            ? Ui.BrandIcon(92)
            : Ui.Glyph(symbol switch
            {
                "leaf" => "BsCheckCircle",
                "hammer" => "BsHammer",
                _ => Rd.IconForSymbol(symbol),
            }, 20, Ui.Brush("RdInk3"));
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(mark);
        var head = Rd.Text(title, 13, FontWeights.Medium);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        head.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(head);
        var body = Rd.Text(message, 12, null, "RdInk2", wrap: true);
        body.TextAlignment = TextAlignment.Center;
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.MaxWidth = 420;
        body.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(body);
        if (primary is { } p)
        {
            var button = Rd.PrimaryButton(p.Title, p.Action);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(button);
        }
        System.Windows.Automation.AutomationProperties.SetName(stack, $"{title}. {message}");
        return new Border { Background = Ui.Brush(background), Padding = new Thickness(32), Child = stack };
    }

    /// <summary>DiskMapLoadingState: an indeterminate bar, the title, the detail.</summary>
    private static FrameworkElement LoadingState(string title, string detail)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4 });
        var head = Rd.Text(title, 13, FontWeights.Medium);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        head.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(head);
        if (detail.Length > 0)
        {
            var body = Rd.Text(detail, 12, null, "RdInk2", wrap: true);
            body.TextAlignment = TextAlignment.Center;
            body.HorizontalAlignment = HorizontalAlignment.Center;
            body.MaxWidth = 420;
            body.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(body);
        }
        System.Windows.Automation.AutomationProperties.SetName(stack, title);
        return new Border { Background = Brushes.Transparent, Padding = new Thickness(32), Child = stack };
    }

    // ---- Helpers ----

    /// <summary>The scan root by its last segment ("you", "C:"), "~" for the profile itself.</summary>
    private static string DisplayName(FileTree tree, string root, int id)
    {
        if (id != 0) return tree.NameOf(id);
        string display = CanonicalPath.DisplayPath(root);
        if (display == "~") return display;
        string leaf = System.IO.Path.GetFileName(root.TrimEnd('\\', '/'));
        return leaf.Length > 0 ? leaf : root;
    }

    private static string DisplayPath(int id) => CanonicalPath.DisplayPath(Model.PathOf(id));

    /// <summary>
    /// Stages developer items. One item is a single add (risky ones — WSL and Docker disks —
    /// ask first); several are listed once for confirmation, each with its own reason.
    /// Items whose recipe says the Recycle Bin is wrong are never staged.
    /// </summary>
    private static void Stage(IReadOnlyList<DeveloperItem> items, bool confirm)
    {
        var allowed = items.Where(CanStage).ToList();
        if (allowed.Count == 0)
        {
            if (items.FirstOrDefault()?.Recipe is { TrashIsUnsafe: true } recipe)
                Model.Toast($"Use the tool instead: {recipe.Command}");
            return;
        }
        if (allowed.Count == 1 && !confirm)
        {
            Model.Stage(allowed[0].NodeID, "Developer: " + allowed[0].DisplayName);
            return;
        }
        Model.ConfirmStageRequests(allowed.Select(i => (i.NodeID, "Developer: " + i.DisplayName)).ToList(), "Developer");
    }

    private static void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* the clipboard can be locked by another app */ }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else Explorer.Reveal(path);
        }
        catch { /* explorer missing is not fatal */ }
    }

    /// <summary>Opens a terminal in the folder (Windows Terminal, else Command Prompt). Nothing runs; the person types the command.</summary>
    private static void OpenTerminal(string path)
    {
        string folder = System.IO.Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? path;
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", folder }, UseShellExecute = false });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true }); }
            catch { /* no terminal is not fatal */ }
        }
    }
}
