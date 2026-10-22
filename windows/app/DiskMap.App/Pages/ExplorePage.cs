using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.App.Controls;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-107: Explore → the map (Mac: VisualizeView). Back/forward and the path, Map | Rings |
/// List with the other charts under More, Color and Size menus and the inspector toggle;
/// the folder's name and size with the "Can be freed" legend; the canvas; what's in this
/// folder as a table; and the selected item's inspector on the right.
/// </summary>
public sealed class ExplorePage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    /// <summary>The map's colouring and the inspector's visibility outlive the page (Mac @State on the shell).</summary>
    internal static ExploreColorMode ColorMode = ExploreColorMode.Freeable;
    private static bool _showInspector = true;
    private bool _drawerOpen;

    private readonly Grid _root = new();
    private FrameworkElement? _canvas;
    private ExploreMap? _map;
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private int _builtZoom = -2;
    private string _builtMode = "";
    private object? _builtTree;

    public ExplorePage()
    {
        Background = Ui.Brush("RdPage");
        Content = _root;
        ModelEvents.WhileLoaded(this, Rebuild);
        Loaded += (_, _) => { Model.PropertyChanged += OnModelProperty; Model.StagedChanged += OnStaged; };
        Unloaded += (_, _) => { Model.PropertyChanged -= OnModelProperty; Model.StagedChanged -= OnStaged; };
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != _class) Rebuild(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _drawerOpen) { _drawerOpen = false; Rebuild(); e.Handled = true; }
        };
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    /// <summary>Selection changes redraw the map and the inspector without rebuilding the canvas.</summary>
    private void OnModelProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanModel.SelectedNode)) Dispatcher.InvokeAsync(Rebuild);
    }

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    private int CurrentId => Model.ZoomedNode;

    // ---- Build ----

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        if (Model.Tree is not { } tree || Model.Totals.Length != tree.Count)
        {
            Model.ShowPage("Overview");
            return;
        }
        var main = MainColumn(tree);
        if (!_showInspector)
        {
            _root.Children.Add(main);
            return;
        }
        var inspector = new Border
        {
            Background = Ui.Brush("RdSidebar"),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = ExploreInspector.Build(Model.SelectedNode >= 0 ? Model.SelectedNode : CurrentId, _class),
                Focusable = false,
            },
        };
        if (WindowWidth >= 1200)
        {
            // Side by side: main | hairline | inspector (AdaptiveInspectorSplit).
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ExploreInspector.ColumnWidth(WindowWidth)) });
            _root.Children.Add(main);
            var line = new Border { Background = Ui.Brush("RdLine") };
            Grid.SetColumn(line, 1);
            _root.Children.Add(line);
            Grid.SetColumn(inspector, 2);
            _root.Children.Add(inspector);
            return;
        }
        // Narrow: a quiet "Inspector" bar over the page; the inspector slides in as a drawer.
        var stack = new DockPanel();
        var bar = new DockPanel { Height = 38, Background = Ui.Brush("RdPage") };
        if (!_drawerOpen)
        {
            var open = Rd.Link("Inspector", () => { _drawerOpen = true; Rebuild(); }, 13, "RdInk2");
            open.Margin = new Thickness(0, 0, 14, 0);
            DockPanel.SetDock(open, Dock.Right);
            bar.Children.Add(open);
        }
        bar.Children.Add(new Border());
        var barBox = new Border { Child = bar, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1) };
        DockPanel.SetDock(barBox, Dock.Top);
        stack.Children.Add(barBox);
        stack.Children.Add(main);
        _root.Children.Add(stack);
        if (_drawerOpen)
        {
            var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(31, 0, 0, 0)) };
            scrim.MouseLeftButtonUp += (_, _) => { _drawerOpen = false; Rebuild(); };
            _root.Children.Add(scrim);
            inspector.Width = Math.Min(320, Math.Max(280, WindowWidth * 0.34));
            inspector.HorizontalAlignment = HorizontalAlignment.Right;
            inspector.BorderBrush = Ui.Brush("RdLine");
            inspector.BorderThickness = new Thickness(1, 0, 0, 0);
            _root.Children.Add(inspector);
        }
    }

    private FrameworkElement MainColumn(FileTree tree)
    {
        var totals = Model.Totals;
        double pad = _class.PagePadding();
        var grid = new Grid { Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 12 : 16, pad, pad) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // toolbar
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // title
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // canvas
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // table

        grid.Children.Add(Toolbar(tree));
        var title = TitleRow(tree, totals);
        Grid.SetRow(title, 2);
        grid.Children.Add(title);

        var canvasHost = new Grid();
        canvasHost.Children.Add(new Border { Background = Rd.Grid24(null, 20), Opacity = 0.5 });
        canvasHost.Children.Add(Canvas(tree));
        Grid.SetRow(canvasHost, 4);
        grid.Children.Add(canvasHost);

        var table = ContentsTable(tree, totals);
        Grid.SetRow(table, 6);
        grid.Children.Add(table);
        // The table takes up to 36% of the height, at least 150, at most 8 rows + header.
        grid.SizeChanged += (_, e) =>
        {
            int rows = Math.Min(LargestRows(tree, totals).Count, 8);
            table.Height = Math.Max(150, Math.Min(e.NewSize.Height * 0.36, rows * RowHeight + 34));
        };
        return grid;
    }

    private double RowHeight => _class == Rd.WidthClass.Compact ? 34 : 38;

    private FrameworkElement Canvas(FileTree tree)
    {
        string mode = Model.VisualizeMode;
        // Rebuild the chart only when the folder, chart or scan changed; selection just redraws it.
        if (_canvas is not null && _builtZoom == CurrentId && _builtMode == mode + ColorMode && ReferenceEquals(_builtTree, tree))
        {
            if (_canvas.Parent is Panel old) old.Children.Remove(_canvas);
            _map?.Refresh();
            return _canvas;
        }
        _builtZoom = CurrentId;
        _builtMode = mode + ColorMode;
        _builtTree = tree;
        _map = null;
        _canvas = mode switch
        {
            "Sunburst" => new SunburstControl(),
            "Flame" => new ScrollViewer { Content = new FlameControl { MinHeight = 400 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            "Bubbles" => new BubblesControl(),
            "Mind Map" => new ScrollViewer { Content = new MindMapControl { MinHeight = 400 }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            _ => _map = new ExploreMap(ColorMode, new FreeableIndex(Model.Reviewables ?? [], Model.Totals)),
        };
        return _canvas;
    }

    // ---- Toolbar ----

    private FrameworkElement Toolbar(FileTree tree)
    {
        var dock = new DockPanel();
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var views = ViewSwitch();
        views.Margin = new Thickness(0, 0, 6, 0);
        right.Children.Add(views);
        if (Model.VisualizeMode != "Age Map")
            right.Children.Add(Menu("Color", Enum.GetValues<ExploreColorMode>(), ColorMode, m => m.Title(), m =>
            {
                ColorMode = m;
                Model.ColoringMode = m switch { ExploreColorMode.Type => "type", ExploreColorMode.Age => "age", _ => "folder" };
                _canvas = null;
                Rebuild();
            }));
        right.Children.Add(Menu("Size", [SizeBasis.Allocated, SizeBasis.Logical], Model.SizeBasis,
            b => b == SizeBasis.Allocated ? "On disk" : "Logical", b => Model.SizeBasis = b));
        right.Children.Add(Rd.IconButton("BsLayoutSidebarInsetReverse", _showInspector ? "Hide inspector" : "Show inspector", () =>
        {
            _showInspector = !_showInspector;
            Rebuild();
        }));
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var back = Rd.IconButton("BsChevronLeft", "Back", () => Model.GoBack());
        back.IsEnabled = Model.CanGoBack;
        back.Opacity = Model.CanGoBack ? 1 : 0.35;
        var forward = Rd.IconButton("BsChevronRight", "Forward", () => Model.GoForward());
        forward.IsEnabled = Model.CanGoForward;
        forward.Opacity = Model.CanGoForward ? 1 : 0.35;
        left.Children.Add(back);
        left.Children.Add(forward);
        var crumbs = Breadcrumb(tree);
        crumbs.Margin = new Thickness(10, 0, 8, 0);
        left.Children.Add(crumbs);
        dock.Children.Add(left);
        return dock;
    }

    /// <summary>BreadcrumbBar: the path, "/"-separated, the current folder in ink.</summary>
    private static FrameworkElement Breadcrumb(FileTree tree)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var chain = tree.AncestorIds(Model.ZoomedNode);
        if (chain.Count == 0 || chain[^1] != Model.ZoomedNode) chain = [.. chain, Model.ZoomedNode];
        for (int i = 0; i < chain.Count; i++)
        {
            int id = chain[i];
            if (i > 0)
            {
                var slash = Rd.Figure("/", 11, null, "RdInk3");
                slash.Margin = new Thickness(6, 0, 6, 0);
                row.Children.Add(slash);
            }
            bool current = id == Model.ZoomedNode;
            var crumb = Rd.Text(tree.NameOf(id), 13, current ? FontWeights.Medium : FontWeights.Normal, current ? "RdInk" : "RdInk2");
            crumb.Cursor = Cursors.Hand;
            crumb.MouseLeftButtonUp += (_, _) => { Model.DrillToAncestor(id); Model.Select(id); };
            System.Windows.Automation.AutomationProperties.SetName(crumb, tree.NameOf(id));
            row.Children.Add(crumb);
        }
        return new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
    }

    private static readonly string[] MoreModes = ["Flame", "Bubbles", "Mind Map", "Age Map"];

    /// <summary>
    /// ExploreViewSwitch: Map | Rings | List, then the other charts under More. On a list page
    /// (<paramref name="onList"/>) List is the one lit and More reads "More".
    /// </summary>
    internal static FrameworkElement ViewSwitch(bool onList = false)
    {
        string mode = onList ? "" : Model.VisualizeMode;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Rd.SegmentSwitch(
        [
            new Rd.Segment("Map", !onList && mode is "Treemap" or "", () => Show("Treemap")),
            new Rd.Segment("Rings", mode == "Sunburst", () => Show("Sunburst")),
            new Rd.Segment("List", onList, () => { if (!onList) Model.ShowPage("Biggest Files"); }),
        ]));
        string moreTitle = MoreModes.Contains(mode) ? mode : "More";
        var more = new StackPanel { Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent, Margin = new Thickness(8, 0, 0, 0) };
        more.Children.Add(Rd.Text(moreTitle, 13, FontWeights.Medium));
        var chevron = Ui.Glyph(Icons.ChevronDown, 8, Ui.Brush("RdInk"));
        chevron.Margin = new Thickness(4, 1, 0, 0);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        more.Children.Add(chevron);
        more.ToolTip = "Flame, Bubbles, Mind Map and Age Map";
        more.MouseLeftButtonUp += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (string m in MoreModes)
            {
                var item = new MenuItem { Header = (mode == m ? "✓  " : "     ") + m };
                string captured = m;
                item.Click += (_, _) => Show(captured);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = more;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        System.Windows.Automation.AutomationProperties.SetName(more, moreTitle);
        row.Children.Add(more);
        return row;
    }

    private static void Show(string mode)
    {
        Model.VisualizeMode = mode;
        Model.ShowPage("Visualize");
    }

    /// <summary>DiskMapMenu: "Label Value ⌃⌄", 28 tall, 9 side padding; a menu with a tick on the current value.</summary>
    private static FrameworkElement Menu<T>(string label, IEnumerable<T> options, T selection, Func<T, string> title, Action<T> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Rd.Text(label, 13, FontWeights.Medium, "RdInk3"));
        var value = Rd.Text(title(selection), 13, FontWeights.Medium);
        value.Margin = new Thickness(5, 0, 5, 0);
        row.Children.Add(value);
        var chevrons = Ui.Glyph("BsChevronExpand", 8.5, Ui.Brush("RdInk3"));
        chevrons.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(chevrons);
        var host = new Border
        {
            Height = 28, Padding = new Thickness(9, 0, 9, 0), Background = Brushes.Transparent,
            Cursor = Cursors.Hand, Child = row,
        };
        var items = options.ToList();
        host.MouseLeftButtonUp += (_, _) =>
        {
            var menu = new ContextMenu();
            foreach (var option in items)
            {
                var item = new MenuItem { Header = (EqualityComparer<T>.Default.Equals(option, selection) ? "✓  " : "     ") + title(option) };
                var captured = option;
                item.Click += (_, _) => pick(captured);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = host;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        System.Windows.Automation.AutomationProperties.SetName(host, $"{label}: {title(selection)}");
        return host;
    }

    // ---- Title row ----

    private FrameworkElement TitleRow(FileTree tree, long[] totals)
    {
        int id = CurrentId;
        var freeable = new FreeableIndex(Model.Reviewables ?? [], totals);
        long folderBytes = id >= 0 && id < totals.Length ? totals[id] : 0;
        long freeBytes = freeable.Bytes(id, tree, totals);
        int parent = id > 0 ? tree.Parent[id] : -1;
        double? share = parent >= 0 && parent < totals.Length && totals[parent] > 0 ? (double)folderBytes / totals[parent] : null;
        int files = id >= 0 && id < Model.Counts.Files.Length ? Model.Counts.Files[id] : 0;

        var dock = new DockPanel();
        if (ColorMode == ExploreColorMode.Freeable && Model.VisualizeMode is "Treemap" or "")
        {
            var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 3) };
            var hatch = new Border
            {
                Width = 12, Height = 12, BorderBrush = Ui.Brush("AppAccent"), BorderThickness = new Thickness(1),
                Background = Rd.HatchBrush(Ui.Brush("AppAccent"), Ui.Brush("RdAccentWash"), 4, 1.2),
            };
            legend.Children.Add(hatch);
            var can = Rd.Text("Can be freed", 12, null, "RdInk2");
            can.Margin = new Thickness(7, 0, 0, 0);
            legend.Children.Add(can);
            var amount = Rd.Figure(ByteFormat.Format(freeBytes), 12, null, "AppAccent");
            amount.Margin = new Thickness(7, 0, 0, 0);
            legend.Children.Add(amount);
            legend.ToolTip = "Things that rebuild by themselves, as in Clean up";
            if (_class != Rd.WidthClass.Compact)
            {
                var yours = new Border
                {
                    Width = 12, Height = 12, Background = Ui.Brush("RdCard"), BorderBrush = Ui.Brush("RdInk"),
                    BorderThickness = new Thickness(1), Margin = new Thickness(15, 0, 0, 0),
                };
                legend.Children.Add(yours);
                var text = Rd.Text("Your files and app data", 12, null, "RdInk2");
                text.Margin = new Thickness(7, 0, 0, 0);
                legend.Children.Add(text);
            }
            DockPanel.SetDock(legend, Dock.Right);
            dock.Children.Add(legend);
        }

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var name = Rd.TightText(tree.NameOf(id), _class == Rd.WidthClass.Compact ? 22 : 26, -0.6);
        var size = Rd.Figure(ByteFormat.Format(folderBytes), 12, FontWeights.Medium);
        var line = Rd.BaselineRow(name, _class == Rd.WidthClass.Compact ? 22 : 26, size, 12, 12);
        left.Children.Add(line);
        if (_class != Rd.WidthClass.Compact)
        {
            string detail = $"{files:N0} files" + (share is { } s ? $" · {Math.Round(s * 100, MidpointRounding.AwayFromZero)}% of {tree.NameOf(parent)}" : "");
            var info = Rd.Text(detail, 12, null, "RdInk3");
            var withInfo = Rd.BaselineRow(new Border(), 26, info, 12, 12);
            left.Children.Add(withInfo);
        }
        dock.Children.Add(left);
        return dock;
    }

    // ---- Contents table ----

    private List<(int Id, long Size)> LargestRows(FileTree tree, long[] totals) =>
        tree.ChildrenOf(CurrentId, totals).Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();

    private FrameworkElement ContentsTable(FileTree tree, long[] totals)
    {
        var rows = LargestRows(tree, totals);
        long largest = Math.Max(1, rows.FirstOrDefault().Size);
        var freeable = new FreeableIndex(Model.Reviewables ?? [], totals);
        int today = AgeMap.Today();
        bool showWhat = _class != Rd.WidthClass.Compact;
        double barWidth = _class == Rd.WidthClass.Compact ? 80 : 120;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = TableRow(showWhat, barWidth,
            Rd.Text("Name", 12, null, "RdInk3"), new Border(), Rd.Text("Size ↓", 12, null, "RdInk3"),
            Rd.Text("Last used", 12, null, "RdInk3"), Rd.Text("Can be freed", 12, null, "RdInk3"),
            showWhat ? Rd.Text("What it is", 12, null, "RdInk3") : null);
        ((FrameworkElement)header.Children[2]).HorizontalAlignment = HorizontalAlignment.Right;
        var headBox = new Border
        {
            Child = header, Padding = new Thickness(10, 0, 10, 0),
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(0, 0, 0, 1),
        };
        grid.Children.Add(headBox);

        var list = new StackPanel();
        foreach (var (id, size) in rows)
        {
            long free = freeable.Bytes(id, tree, totals);
            bool isDir = tree.IsDirectory[id];
            bool rebuilds = freeable.Contains(id, tree);
            string path = Model.PathOf(id);
            string what = WhatItIs(rebuilds, StorageClassifier.Classify(path, isDir).Class);
            bool selected = id == Model.SelectedNode;
            list.Children.Add(ContentRow(id, tree.NameOf(id), isDir, size, (double)size / largest,
                size > 0 ? (double)free / size : 0, LastUsed(tree.ModifiedDay[id], today), free, what, rebuilds,
                selected, showWhat, barWidth));
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        System.Windows.Automation.AutomationProperties.SetName(grid, $"Contents of {tree.NameOf(CurrentId)}");
        return grid;
    }

    /// <summary>Name (flex) · bar · Size (84, right) · Last used (92, 14 in) · Can be freed (96) · What it is (flex).</summary>
    private static Grid TableRow(bool showWhat, double barWidth, FrameworkElement name, FrameworkElement bar,
        FrameworkElement size, FrameworkElement lastUsed, FrameworkElement freeable, FrameworkElement? what)
    {
        var grid = new Grid();
        void col(GridLength w) => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        col(new GridLength(1, GridUnitType.Star)); col(new GridLength(10));
        col(new GridLength(barWidth)); col(new GridLength(10));
        col(new GridLength(84)); col(new GridLength(10 + 14));
        col(new GridLength(92)); col(new GridLength(10));
        col(new GridLength(96));
        if (showWhat) { col(new GridLength(10)); col(new GridLength(1, GridUnitType.Star)); }
        void put(FrameworkElement e, int c) { Grid.SetColumn(e, c); e.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(e); }
        put(name, 0); put(bar, 2); put(size, 4); put(lastUsed, 6); put(freeable, 8);
        if (what is not null) put(what, 10);
        return grid;
    }

    private FrameworkElement ContentRow(int id, string name, bool isDir, long bytes, double fraction, double freeFraction,
        string lastUsed, long freeable, string what, bool whatIsAccent, bool selected, bool showWhat, double barWidth)
    {
        var nameText = Rd.Text(name, 13, selected ? FontWeights.Medium : FontWeights.Normal);
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        var bar = new Grid { Height = 5, Width = barWidth };
        bar.Children.Add(new Border { Background = Ui.Brush("RdSoft") });
        bar.Children.Add(new Border { Background = Ui.Brush("RdInk"), Width = barWidth * Math.Min(1, fraction), HorizontalAlignment = HorizontalAlignment.Left });
        bar.Children.Add(new Border { Background = Ui.Brush("AppAccent"), Width = barWidth * Math.Min(1, fraction * freeFraction), HorizontalAlignment = HorizontalAlignment.Left });
        var size = Rd.Figure(ByteFormat.Format(bytes), 12);
        size.HorizontalAlignment = HorizontalAlignment.Right;
        var used = Rd.Text(lastUsed, 13, null, "RdInk2");
        var free = Rd.Figure(freeable > 0 ? ByteFormat.Format(freeable) : "—", 12, null, freeable > 0 ? "AppAccent" : "RdInk3");
        var whatText = showWhat ? Rd.Text(what, 13, null, whatIsAccent ? "RdAccentDeep" : "RdInk2") : null;
        var row = TableRow(showWhat, barWidth, nameText, bar, size, used, free, whatText);
        var host = new Border
        {
            Height = RowHeight, Padding = new Thickness(10, 0, 10, 0), Child = row, Cursor = Cursors.Hand,
            Background = selected ? Ui.Brush("RdAccentWash") : Brushes.Transparent,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.6), BorderThickness = new Thickness(0, 0, 0, 1),
            ToolTip = isDir ? "Double-click to open" : name,
        };
        host.MouseEnter += (_, _) => { if (!selected) host.Background = Ui.Brush("RdHover"); };
        host.MouseLeave += (_, _) => { if (!selected) host.Background = Brushes.Transparent; };
        host.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                if (isDir) Model.DrillTo(id);
                Model.Select(id);
            }
            else Model.Select(id);
            e.Handled = true;
        };
        System.Windows.Automation.AutomationProperties.SetName(host,
            $"{name}, {ByteFormat.Format(bytes)}{(freeable > 0 ? $", {ByteFormat.Format(freeable)} can be freed" : "")}");
        return host;
    }

    /// <summary>The table's "What it is": the storage class, nothing for "Other".</summary>
    public static string WhatItIs(bool rebuilds, StorageClass storageClass) =>
        rebuilds ? "Rebuilds by itself" : storageClass.Id == "other" ? "—" : storageClass.Title;

    /// <summary>"Today", "Yesterday", "3 days ago", "2 weeks ago", "5 months ago", "2 years ago".</summary>
    public static string LastUsed(int day, int today)
    {
        if (day <= 0) return "—";
        int age = today - day;
        if (age <= 0) return "Today";
        if (age == 1) return "Yesterday";
        if (age < 7) return $"{age} days ago";
        if (age < 60) return $"{age / 7} week{(age / 7 == 1 ? "" : "s")} ago";
        if (age < 730) return $"{age / 30} months ago";
        return $"{age / 365} years ago";
    }
}
