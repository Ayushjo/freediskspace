using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App;

public partial class MainWindow : Window
{
    private readonly ScanModel _model = ScanModel.Shared;
    private TrayIcon? _tray;
    private readonly Dictionary<string, Func<UserControl>> _pageFactories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _navItems = new(StringComparer.Ordinal);
    // One instance per page for the session: a page keeps its state
    // (filters, results) and isn't rebuilt on every sidebar click.
    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.Ordinal);
    private string _currentPage = "Overview";
    private double _textScale = 1;
    private bool _inspectorOpen;
    private bool _compactSidebarOpen;
    private bool? _lastSidebarCompact;
    private readonly DispatcherTimer _relativeTimeTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public MainWindow()
    {
        InitializeComponent();
        // Render harness: pin the content area to an exact size so shots match
        // the macOS reference renders pixel for pixel.
        if (Harness.ContentSize is { } size && Content is FrameworkElement content)
        {
            content.Width = size.Width;
            content.Height = size.Height;
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        ApplyTextScale(AppSettings.Load());
        BuildNav();
        BuildTopBar();
        BuildCaptionButtons();
        StateChanged += (_, _) => RefreshMaximizedInset();
        InspectorHost.Content = new Controls.InspectorPanel();
        ModelEvents.WhileLoaded(this, RefreshShell);
        _relativeTimeTimer.Tick += (_, _) => RefreshScanStatus();
        _relativeTimeTimer.Start();
        Closed += (_, _) => _relativeTimeTimer.Stop();
        _tray = new TrayIcon(this);
        Closed += (_, _) => _tray.Dispose();
        _model.PageRequested += SelectPage;
        _model.BasketRequested += () => BasketWindow.Show(this);
        _model.ToastRequested += ShowToast;
        _model.ConfirmRisky = (name, verdict) => Dialogs.ConfirmRisky(this, name, verdict);
        _model.ConfirmBulk = (title, message, items, footnote) =>
            Dialogs.Confirm(this, title, message, items, "Add to basket", footnote: footnote);
        _model.ScanRequested += async () => await PickAndScan();
        _model.StagedChanged += (_, _) => RefreshBasket();
        _model.PropertyChanged += OnModelChanged;
        SizeChanged += (_, _) => RefreshAdaptiveLayout();
        SelectPage("Overview");
        // WIN-051: the shared keyboard map — destination shortcuts,
        // rescan, list navigation, stage/activate — one handler, routed
        // to the active page when it's a list.
        PreviewKeyDown += OnGlobalKey;
        // WIN-055: a directory argv beats the dev/screenshot env hook —
        // it is the explicit user action ("Scan with DiskMap").
        Loaded += async (_, _) =>
        {
            try
            {
                string? scanPath = App.StartupPath
                    ?? Environment.GetEnvironmentVariable("DISKMAP_AUTOSCAN");
                Trace("loaded");
                if (scanPath is { Length: > 0 }
                    && Directory.Exists(scanPath)
                    && !_model.IsScanning && _model.Tree is null)
                {
                    Trace($"scan:{scanPath}");
                    await _model.ScanAsync(scanPath);
                    Trace("scanned");
                    if (Environment.GetEnvironmentVariable("DISKMAP_PAGE") is { Length: > 0 } page)
                        SelectPage(page);
                }
                // WIN-080: render-to-file screenshot — the macOS --snapshot-dir
                // counterpart. Waits a beat for async page content (the
                // Overview's deferred Compute) before encoding.
                if (Environment.GetEnvironmentVariable("DISKMAP_SHOT") is { Length: > 0 } shotPath)
                {
                    await Task.Delay(
                        int.TryParse(Environment.GetEnvironmentVariable("DISKMAP_SHOT_SETTLE"), out int s) ? s : 1200);
                    SaveShot(shotPath);
                    // The popups too: the tray flyout and the risky-item
                    // warning, rendered next to the window shot.
                    if (Environment.GetEnvironmentVariable("DISKMAP_SHOT_POPUPS") == "1")
                    {
                        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(shotPath))!,
                            Path.GetFileNameWithoutExtension(shotPath));
                        _tray?.SaveFlyoutShot(stem + "-tray.png");
                        var wsl = StorageClassifier.Classify(@"C:\Users\you\AppData\Local\wsl\{id}\ext4.vhdx", false);
                        var risky = Dialogs.RiskyWindow(this, "ext4.vhdx", wsl, _ => { });
                        SaveWindowShot(risky, stem + "-risky.png");
                        risky.Close();
                        if (_model.SpaceAccount is { } account)
                        {
                            var rest = Dialogs.SpaceAccountWindow(this, account);
                            SaveWindowShot(rest, stem + "-rest.png");
                            rest.Close();
                        }
                    }
                    Trace("shot");
                    if (Environment.GetEnvironmentVariable("DISKMAP_SHOT_QUIT") != "0")
                        Close();
                }
            }
            catch (Exception ex) { Trace("ERR " + ex); }
        };
    }

    /// <summary>
    /// WIN-055: drop a folder anywhere on the window to scan it. Files
    /// get the ⊘ cursor — only directories are scannable.
    /// </summary>
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedDirectory(e.Data) is not null
            ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (DroppedDirectory(e.Data) is { } dir && !_model.IsScanning)
            await _model.ScanAsync(dir);
    }

    private static string? DroppedDirectory(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] paths) return null;
        return paths.FirstOrDefault(Directory.Exists);
    }

    // ---- Caption (the Mac's hidden title bar) ----

    private void BuildCaptionButtons()
    {
        // The render harness shoots the window like the Mac's, without its window controls.
        if (Harness.IsActive)
        {
            CaptionButtonsHost.Content = null;
            return;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(CaptionButton("", "Minimize", false, () => WindowState = WindowState.Minimized));
        row.Children.Add(CaptionButton(WindowState == WindowState.Maximized ? "" : "",
            WindowState == WindowState.Maximized ? "Restore" : "Maximize", false,
            () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        row.Children.Add(CaptionButton("", "Close", true, Close));
        WindowChrome.SetIsHitTestVisibleInChrome(row, true);
        CaptionButtonsHost.Content = row;
    }

    private static Border CaptionButton(string glyph, string label, bool isClose, Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            Foreground = Ui.Brush("RdInk2"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Border
        {
            Width = 46, Height = 32,
            Background = System.Windows.Media.Brushes.Transparent,
            Child = text,
            ToolTip = label,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        var hover = isClose
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC4, 0x2B, 0x1C))
            : Ui.Brush("RdHover");
        button.MouseEnter += (_, _) =>
        {
            button.Background = hover;
            if (isClose) text.Foreground = System.Windows.Media.Brushes.White;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = System.Windows.Media.Brushes.Transparent;
            text.Foreground = Ui.Brush("RdInk2");
        };
        button.MouseLeftButtonUp += (_, _) => onClick();
        return button;
    }

    /// <summary>A maximised chrome-less window hangs past the screen by its resize border; inset the content.</summary>
    private void RefreshMaximizedInset()
    {
        ShellRoot.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : new Thickness(0);
        BuildCaptionButtons();
    }

    // ---- Sidebar (AppShellView.sidebar) ----

    /// <summary>One sidebar destination: the Mac nav item and the Windows page it opens.</summary>
    private sealed record NavDestination(string Title, string Icon, string PageKey);

    /// <summary>AppDestination.primaryNav, then MORE, then moreNav.</summary>
    private static readonly NavDestination[] PrimaryNav =
    [
        new("Overview", Icons.NavOverview, "Overview"),
        new("Clean up", Icons.NavCleanUp, "Safe to Review"),
        new("Explore", Icons.NavExplore, "Visualize"),
        new("Developer", Icons.NavDeveloper, "Developer Storage"),
        new("Duplicates", Icons.NavDuplicates, "Duplicates"),
        new("What changed", Icons.NavWhatChanged, "Snapshots"),
    ];

    private static readonly NavDestination[] MoreNav =
    [
        new("Applications", Icons.NavApplications, "Applications"),
        new("History & Put Back", Icons.NavHistory, "Cleanup"),
    ];

    /// <summary>
    /// AppDestination.navParent: the pages folded into a sidebar item keep their own key (the
    /// harness and in-app links still open them) and light up their parent.
    /// </summary>
    private static string NavParent(string pageKey) => pageKey switch
    {
        "Caches" or "Old Downloads" or "Large Media" or "Safe to Review" => "Clean up",
        "Find" or "Biggest Files" or "Biggest Folders" or "File Browser" or "Forgotten Files" or "Visualize" => "Explore",
        "Developer Storage" => "Developer",
        "Snapshots" => "What changed",
        "Cleanup" => "History & Put Back",
        _ => pageKey,
    };

    private void BuildNav()
    {
        _pageFactories["Overview"] = () => new OverviewPage();
        _pageFactories["Find"] = () => new FindExplorePage();   // Mac: FindView
        _pageFactories["Biggest Files"] = () => new BiggestFilesExplorePage();   // Mac: Explore → List → Files
        _pageFactories["Biggest Folders"] = () => new BiggestFoldersExplorePage("Biggest Folders");   // Mac: Explore → List → Folders
        _pageFactories["Forgotten Files"] = () => new ForgottenExplorePage();   // Mac: Explore → List → Forgotten
        _pageFactories["Duplicates"] = () => new DuplicatesBoardPage();   // Mac: DuplicatesView
        _pageFactories["Safe to Review"] = () => new CleanUpPage();   // Mac cleanSafe: Clean up
        _pageFactories["Caches"] = () => new CleanCachesPage();   // Mac cleanCaches: CachesReviewView
        _pageFactories["Old Downloads"] = () => new CleanDownloadsPage();   // Mac cleanDownloads: OldDownloadsView
        _pageFactories["Large Media"] = () => new CleanMediaPage();   // Mac cleanMedia: LargeMediaView
        _pageFactories["File Browser"] = () => new BiggestFoldersExplorePage("File Browser");   // Mac shows BiggestFoldersView here
        _pageFactories["Visualize"] = () => new ExplorePage();   // Mac: Explore → Map
        _pageFactories["Developer Storage"] = () => new DeveloperBoardPage();
        _pageFactories["Applications"] = () => new AppsBoardPage();   // Mac: ApplicationsView
        _pageFactories["Snapshots"] = () => new WhatChangedPage();   // Mac: What changed
        _pageFactories["Cleanup"] = () => new HistoryPage();   // Mac: History & Put Back (the basket is its own window)

        NavPanel.Children.Clear();
        _navItems.Clear();
        foreach (var dest in PrimaryNav) AddNavRow(dest);
        var more = Rd.MonoLabel("More");
        // 14 above and 6 below like the Mac; +1.5 below for SF Mono's taller line (measured).
        more.Margin = new Thickness(10, 14, 10, 7.5);
        more.HorizontalAlignment = HorizontalAlignment.Left;
        NavPanel.Children.Add(more);
        foreach (var dest in MoreNav) AddNavRow(dest);
        BuildSidebarFooter();
        RefreshNav();
    }

    private void AddNavRow(NavDestination dest)
    {
        var item = new Border
        {
            Height = Ui.Scaled(30),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 0, 0, 1),
            Tag = dest,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        item.MouseLeftButtonUp += (_, _) =>
        {
            if (dest.PageKey != "Overview" && _model.Tree is null) return;
            SelectPage(dest.PageKey);
        };
        _navItems[dest.Title] = item;
        NavPanel.Children.Add(item);
    }

    /// <summary>Repaints every row: selection, locked state, the quiet figure on the right.</summary>
    private void RefreshNav()
    {
        string parent = NavParent(_currentPage);
        bool scanned = _model.Tree is not null;
        foreach (var (title, item) in _navItems)
        {
            var dest = (NavDestination)item.Tag;
            bool locked = dest.PageKey != "Overview" && !scanned;
            bool selected = parent == title && !locked;
            string ink = locked ? "RdInk3" : selected ? "RdInk" : "RdInk2";

            var grid = new Grid { Margin = new Thickness(10, 0, 10, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = Ui.Glyph(dest.Icon, 12.5, Ui.Brush(ink));
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(icon);
            var label = Rd.Text(dest.Title, 13, selected ? FontWeights.Medium : FontWeights.Normal, ink);
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);
            string a11y = locked ? $"{dest.Title}, scan first" : dest.Title;
            if (!locked && NavFigure(dest.Title) is { } figure)
            {
                var text = Rd.Figure(figure.Text, 11, null, figure.Accent ? "AppAccent" : "RdInk3");
                text.Margin = new Thickness(4, 0, 0, 0);
                Grid.SetColumn(text, 3);
                grid.Children.Add(text);
                a11y = $"{dest.Title}, {figure.Text}";
            }

            var body = new Grid();
            body.Children.Add(grid);
            if (selected)
            {
                body.Children.Add(new Border
                {
                    Width = 2,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(7, 0, 0, 7),
                    Background = Ui.Brush("AppAccent"),
                });
            }
            item.Child = body;
            item.Background = selected ? Ui.Brush("RdSoft") : System.Windows.Media.Brushes.Transparent;
            item.Cursor = locked ? Cursors.Arrow : Cursors.Hand;
            item.ToolTip = locked ? "Scan your PC first." : dest.Title;
            System.Windows.Automation.AutomationProperties.SetName(item, a11y);
        }
    }

    /// <summary>
    /// A quiet figure beside a nav item, read from results already built — never builds one
    /// (AppShellView.navFigure).
    /// </summary>
    private (string Text, bool Accent)? NavFigure(string title)
    {
        static (string, bool)? Bytes(long value, bool accent = false) =>
            value > 0 ? (Rd.CompactBytes(value), accent) : null;
        switch (title)
        {
            case "Clean up":
                // What Clean up ticks for you (AppShellView: safeReclaimableBytes).
                if (_model.Reviewables is null) return null;
                return Bytes(_model.SafeReclaimableBytes, accent: true);
            case "Duplicates":
                if (!_model.DuplicatesSearched) return null;
                // Mac: physicalCopies − 1 — copies sharing blocks free nothing, so a clone family is one copy.
                return Bytes(_model.Duplicates.Sum(g => (long)Math.Max(0, g.PhysicalCopies - 1) * g.SizeEach));
            case "What changed":
                if (_model.HistoryComparison is not { } c || Math.Abs(c.FreeDelta) < GrowthNoise) return null;
                long used = -c.FreeDelta;
                return ((used >= 0 ? "+" : "−") + Rd.CompactBytes(Math.Abs(used)), false);
            default:
                return null;
        }
    }

    /// <summary>OverviewView.growthNoise: a week's change under this is noise, not news.</summary>
    private const long GrowthNoise = 50_000_000;

    /// <summary>What's staged for the Recycle Bin, and what free space it leaves (AppShellView.basketCard).</summary>
    private void RefreshBasket()
    {
        if (_model.Tree is null)
        {
            BasketHost.Content = null;
            return;
        }
        int count = _model.StagedItems.Count;
        long bytes = _model.ReclaimEstimate.Bytes;
        var stack = new StackPanel();
        var head = new DockPanel();
        var countLabel = Rd.MonoLabel(count == 0 ? "Empty" : $"{count} item{(count == 1 ? "" : "s")}");
        DockPanel.SetDock(countLabel, Dock.Right);
        head.Children.Add(countLabel);
        var basket = Rd.MonoLabel("Basket");
        basket.HorizontalAlignment = HorizontalAlignment.Left;
        head.Children.Add(basket);
        stack.Children.Add(head);
        var figure = Rd.Text(count == 0 ? "Nothing yet" : ByteFormat.Format(bytes),
            count == 0 ? 15 : 21, FontWeights.SemiBold, count == 0 ? "RdInk3" : "RdInk");
        figure.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(figure);
        if (count > 0 && (_model.Volume ?? Harness.VolumeFor(null)) is { } vol)
        {
            var after = Rd.MonoLabel($"Free after · {ByteFormat.Format(vol.FreeBytes + bytes)}");
            after.Margin = new Thickness(0, 3, 0, 0);
            stack.Children.Add(after);
        }
        var open = Rd.PrimaryButton(count == 0 ? "Open basket" : "Review basket",
            () => BasketWindow.Show(this), fullWidth: true);
        open.Margin = new Thickness(0, 9, 0, 0);
        stack.Children.Add(open);
        BasketHost.Content = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Background = Ui.Brush("RdCard"),
            BorderBrush = Ui.Brush("RdLine"),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        System.Windows.Automation.AutomationProperties.SetName((Border)BasketHost.Content,
            count == 0 ? "Basket, empty" : $"Basket, {count} items, {ByteFormat.Format(bytes)}");
    }

    /// <summary>"Ctrl+K Find anything" and the Settings gear (AppShellView.sidebarFooter).</summary>
    private void BuildSidebarFooter()
    {
        bool scanned = _model.Tree is not null;
        var find = new StackPanel { Orientation = Orientation.Horizontal, Background = System.Windows.Media.Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
        var glass = Ui.Glyph(Icons.Search, 10, Ui.Brush("RdInk3"));
        glass.VerticalAlignment = VerticalAlignment.Center;
        find.Children.Add(glass);
        var label = Rd.MonoLabel("Ctrl+K Find anything");
        label.Margin = new Thickness(6, 0, 0, 0);
        find.Children.Add(label);
        find.Opacity = scanned ? 1 : 0.5;
        find.Cursor = scanned ? Cursors.Hand : Cursors.Arrow;
        find.ToolTip = scanned ? "Find files, folders and actions (Ctrl+K)" : "Scan first to search";
        find.MouseLeftButtonUp += (_, _) => { if (_model.Tree is not null) OpenCommandPalette(); };

        var gear = Ui.Glyph(Icons.Settings, 11.5, Ui.Brush("RdInk3"));
        var gearHost = new Border
        {
            Child = gear, Background = System.Windows.Media.Brushes.Transparent,
            Cursor = Cursors.Hand, ToolTip = "Settings", VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(gearHost, "Settings");
        gearHost.MouseLeftButtonUp += (_, _) => OpenSettings();

        // 16.5 tall: the Mac row's height (its gear glyph), measured from the reference renders.
        var row = new DockPanel { Margin = new Thickness(6, 10, 6, 0), Height = 16.5 };
        DockPanel.SetDock(gearHost, Dock.Right);
        row.Children.Add(gearHost);
        row.Children.Add(find);
        SidebarFooterHost.Content = row;
    }

    private void SelectPage(string name)
    {
        // A sidebar title opens its page ("Clean up" → Safe to Review).
        if (PrimaryNav.Concat(MoreNav).FirstOrDefault(d => d.Title == name) is { } dest) name = dest.PageKey;
        if (!_pageFactories.TryGetValue(name, out var factory)) return;
        _currentPage = name;
        if (!_pages.TryGetValue(name, out var page)) _pages[name] = page = factory();
        PageHost.Content = page;
        if (page is ListPage listPage) listPage.ResetScrollPosition();
        if (_lastSidebarCompact == true)
        {
            _compactSidebarOpen = false;
            RefreshAdaptiveLayout();
        }
        RefreshNav();
        RefreshPageContext();
    }

    /// <summary>The sidebar in order — the Ctrl+1..8 destination map.</summary>
    private static readonly string[] DestinationOrder =
        PrimaryNav.Concat(MoreNav).Select(d => d.PageKey).ToArray();

    // ---- Toasts (WIN-063) ----

    private DispatcherTimer? _toastTimer;

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastHost.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        // Long toasts (skipped risky items) need time to read.
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(text.Length > 60 ? 6 : 2.4) };
        _toastTimer.Tick += (_, _) =>
        {
            ToastHost.Visibility = Visibility.Collapsed;
            _toastTimer.Stop();
        };
        _toastTimer.Start();
    }

    private void OnGlobalKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        // Ctrl+K / Ctrl+F — the palette.
        if (e.Key is Key.K or Key.F && mods == ModifierKeys.Control)
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }
        // Ctrl+I — the inspector (no standing column in the redesign).
        if (e.Key == Key.I && mods == ModifierKeys.Control)
        {
            _inspectorOpen = !_inspectorOpen;
            RefreshAdaptiveLayout();
            e.Handled = true;
            return;
        }
        // Ctrl+O — Scan Folder… (the window menu's old duty).
        if (e.Key == Key.O && mods == ModifierKeys.Control)
        {
            _ = PickAndScan();
            e.Handled = true;
            return;
        }
        // Ctrl+, — Settings (the ⌘, convention, WIN-060).
        if (e.Key == Key.OemComma && mods == ModifierKeys.Control)
        {
            OpenSettings();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            BasketWindow.Show(this);
            e.Handled = true;
            return;
        }
        // Ctrl+1..N — destinations in sidebar order.
        if (mods == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9)
        {
            int index = e.Key - Key.D1;
            if (index < DestinationOrder.Length) SelectPage(DestinationOrder[index]);
            e.Handled = true;
            return;
        }
        // Ctrl+R rescan; Ctrl+Shift+R full rescan (drops the baseline so
        // the journal can't replay — a from-scratch walk).
        if (e.Key == Key.R && mods is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            if (mods.HasFlag(ModifierKeys.Shift) && _model.RootPath is { } rp)
                ScanCache.Remove(rp);
            _ = _model.RescanAsync();
            return;
        }
        if (e.OriginalSource is TextBox) return;   // let inputs own their keys

        // List navigation — the shared row behavior on every list page.
        if (PageHost.Content is FileListPage list)
        {
            if (e.Key == Key.A && mods == ModifierKeys.Control)
            {
                list.SelectAll(); e.Handled = true; return;
            }
            if (e.Key == Key.Escape)
            {
                _model.ClearMulti(); list.ClearChecked(); e.Handled = true; return;
            }
            bool handled = e.Key switch
            {
                Key.Down or Key.J => list.NavigateSelection(1),
                Key.Up or Key.K => list.NavigateSelection(-1),
                Key.Enter => list.ActivateSelection(),
                Key.Delete or Key.Back when mods == ModifierKeys.Control || e.Key == Key.Delete
                    => list.StageSelection(),
                _ => false,
            };
            // Ctrl+Down/Up: drill into / out of the selected folder.
            if (!handled && mods == ModifierKeys.Control
                && _model.Tree is { } t && _model.SelectedNode >= 0)
            {
                if (e.Key == Key.Down && t.IsDirectory[_model.SelectedNode])
                {
                    _model.DrillTo(_model.SelectedNode);
                    handled = true;
                }
                else if (e.Key == Key.Up)
                {
                    int parent = t.Parent[_model.SelectedNode];
                    if (parent >= 0) { _model.DrillToAncestor(parent); handled = true; }
                }
            }
            if (handled) e.Handled = true;
        }
    }

    // ---- Top bar ----

    /// <summary>Scan Folder… — the tray and Ctrl+O route here.</summary>
    internal Task ScanFolder() => PickAndScan();

    /// <summary>Export the current scan (JSON / NDJSON / CSV / ncdu) — the Snapshots header has the same.</summary>
    internal void ExportScan()
    {
        if (_model.Tree is not { } t || _model.RootPath is not { } root) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export scan", FileName = "diskmap-scan",
            Filter = "JSON (nested)|*.json|NDJSON|*.ndjson|CSV|*.csv|ncdu|*.ncdu",
        };
        if (dlg.ShowDialog(this) != true) return;
        var format = dlg.FilterIndex switch
        {
            2 => TreeExporter.Format.Ndjson,
            3 => TreeExporter.Format.Csv,
            4 => TreeExporter.Format.Ncdu,
            _ => TreeExporter.Format.Json,
        };
        try
        {
            File.WriteAllText(dlg.FileName, TreeExporter.Export(t, _model.Totals, root, format));
            _model.Toast($"Exported to {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't write the export: {ex.Message}",
                "freedisk.space", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>WIN-080: launch-harness trace — one line per milestone when DISKMAP_TRACE is set.</summary>
    private static void Trace(string line)
    {
        if (Environment.GetEnvironmentVariable("DISKMAP_TRACE") is { Length: > 0 } path)
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
    }

    /// <summary>WIN-080: PNG of the whole window at device pixels — no z-order dependency.</summary>
    /// <summary>Shows <paramref name="window"/> off to the side, lays it out and saves it as PNG (harness only).</summary>
    internal static void SaveWindowShot(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000;
        window.Top = 0;
        window.ShowActivated = false;
        window.Show();
        window.UpdateLayout();
        var element = (FrameworkElement)window.Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void SaveShot(string path)
    {
        // The content area only (no window frame), at DISKMAP_SHOT_SCALE when
        // set, so a 1440x900 shot at scale 2 is 2880x1800 like the Mac's.
        var element = Content as FrameworkElement ?? this;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double scale = Harness.ShotScale ?? dpi.DpiScaleX;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale)),
            96 * scale, 96 * scale,
            System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>WIN-060: the Settings window — one instance, re-activated if open.</summary>
    private SettingsWindow? _settingsWindow;

    internal void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(SetAppearance, SetTextScale, RebuildForTheme) { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    internal void OpenAbout()
    {
        var body = new StackPanel
        {
            Margin = new Thickness(36, 28, 36, 26),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var mark = Ui.BrandIcon(132);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(mark);
        var name = Ui.T("freedisk.space", 20, FontWeights.SemiBold);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new Thickness(0, 12, 0, 4);
        body.Children.Add(name);
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = Ui.Mono(version is null ? "Development build" : $"Version {version}",
            11, null, Ui.Brush("AppFaint"));
        versionText.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(versionText);
        var description = Ui.Subtle("See where your space went. Nothing leaves your PC.", 12);
        description.HorizontalAlignment = HorizontalAlignment.Center;
        description.Margin = new Thickness(0, 12, 0, 8);
        body.Children.Add(description);
        var promise = Ui.MonoLabel("LOCAL  ·  FAST  ·  PRIVATE");
        promise.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(promise);
        new Window
        {
            Title = "About freedisk.space",
            Owner = this,
            Content = body,
            Width = 360,
            Height = 390,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        }.ShowDialog();
    }

    /// <summary>Appearance switch — palette + Fluent theme + rebuild cached pages.</summary>
    private void SetAppearance(string mode)
    {
        var settings = AppSettings.Load();
        settings.Appearance = mode;
        settings.Save();
        bool dark = settings.WantsDark;
#pragma warning disable WPF0001
        Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        Theme.Apply(dark);
        RebuildForTheme();
    }

    internal void RebuildForTheme()
    {
        if (_tray is not null) _tray.Visible = AppSettings.Load().ShowTray;
        _pages.Clear();
        _pageFactories.Clear();
        BuildNav();
        BuildTopBar();
        BuildCaptionButtons();
        InspectorHost.Content = new Controls.InspectorPanel();
        SelectPage(_currentPage);
        BasketWindow.ThemeChanged();
        _settingsWindow?.Rebuild();
    }

    /// <summary>WIN-059: rebuild code-built typography at the selected text scale.</summary>
    private void SetTextScale(double scale)
    {
        var settings = AppSettings.Load();
        settings.TextScale = scale;
        settings.Save();
        ApplyTextScale(settings);
        RebuildForTheme();
    }

    private void ApplyTextScale(AppSettings settings)
    {
        _textScale = settings.TextScale;
        Ui.TextScale = settings.TextScale;
        RefreshAdaptiveLayout();
    }

    private void RefreshAdaptiveLayout()
    {
        double width = Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth : ActualWidth;
        if (Harness.ContentSize is { } pinned) width = pinned.Width;

        // The inspector: off by default (the Mac redesign has none standing); Ctrl+I shows it,
        // docked when there's room, floating over the page's right edge when there isn't.
        bool compactInspector = width > 0 && width < 1200;
        if (!_inspectorOpen)
        {
            InspectorColumn.Width = new GridLength(0);
            InspectorBorder.Visibility = Visibility.Collapsed;
        }
        else if (compactInspector)
        {
            InspectorColumn.Width = new GridLength(0);
            Grid.SetColumn(InspectorBorder, 2);
            InspectorBorder.HorizontalAlignment = HorizontalAlignment.Right;
            InspectorBorder.Width = 320 * Math.Max(1, _textScale);
            InspectorBorder.Visibility = Visibility.Visible;
        }
        else
        {
            Grid.SetColumn(InspectorBorder, 3);
            InspectorBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            InspectorBorder.Width = double.NaN;
            InspectorBorder.Visibility = Visibility.Visible;
            InspectorColumn.Width = new GridLength((width >= 1450 ? 320 : 290) * Math.Max(1, _textScale));
        }

        // Under 1000 the sidebar folds behind a button in the page bar (Mac: compactSidebar).
        bool compactSidebar = width > 0 && width < 1000;
        if (_lastSidebarCompact != compactSidebar)
        {
            _compactSidebarOpen = false;
            _lastSidebarCompact = compactSidebar;
        }
        SidebarButtonHost.Visibility = compactSidebar ? Visibility.Visible : Visibility.Collapsed;
        double sideWidth = ShellFormat.SidebarWidth(width, _textScale);
        if (compactSidebar)
        {
            SidebarColumn.Width = new GridLength(0);
            SidebarLineColumn.Width = new GridLength(0);
            Grid.SetColumn(SidebarBorder, 2);
            SidebarBorder.HorizontalAlignment = HorizontalAlignment.Left;
            SidebarBorder.Width = Math.Round(212 * Math.Max(1, _textScale));
            SidebarBorder.Visibility = _compactSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
            SidebarBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 32, ShadowDepth = 5, Direction = 0, Opacity = 0.14,
            };
        }
        else
        {
            Grid.SetColumn(SidebarBorder, 0);
            SidebarBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            SidebarBorder.Width = double.NaN;
            SidebarBorder.Visibility = Visibility.Visible;
            SidebarBorder.Effect = null;
            SidebarColumn.Width = new GridLength(sideWidth);
            SidebarLineColumn.Width = new GridLength(1);
        }
    }

    private void BuildTopBar()
    {
        SidebarButtonHost.Content = Ui.IconButton(Icons.List, "Show Sidebar", () =>
        {
            _compactSidebarOpen = !_compactSidebarOpen;
            RefreshAdaptiveLayout();
        });
        // The page bar drags the window, like the Mac's; its controls stay clickable.
        foreach (var host in new FrameworkElement[] { SidebarButtonHost, PageContextHost, ScanStatusHost, RescanButtonHost })
            WindowChrome.SetIsHitTestVisibleInChrome(host, true);
        RefreshAdaptiveLayout();
        RefreshShell();
    }

    /// <summary>Everything in the frame that reads the scan: page bar, nav figures, basket, footer.</summary>
    private void RefreshShell()
    {
        RefreshPageContext();
        RefreshScanStatus();
        RefreshScanButton();
        RefreshNav();
        RefreshBasket();
        BuildSidebarFooter();
    }

    /// <summary>The left side of the page bar: what this page is looking at (AppShellView.pageContext).</summary>
    private void RefreshPageContext()
    {
        string parent = NavParent(_currentPage);
        bool scanned = _model.Tree is not null;
        if (parent == "Overview")
        {
            if (scanned && (_model.Volume ?? Harness.VolumeFor(null)) is { } vol)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Rd.Text(vol.DriveLabel, 13, FontWeights.Medium));
                var total = Rd.Figure(ByteFormat.Format(vol.TotalBytes), 11, null, "RdInk3");
                total.Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(total);
                PageContextHost.Content = row;
                return;
            }
            PageContextHost.Content = Rd.Text(scanned
                ? Path.GetFileName(_model.RootPath?.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : "Overview"
                : "freedisk.space", 13, FontWeights.Medium);
            return;
        }
        PageContextHost.Content = Rd.Text(parent, 13, FontWeights.Medium);
    }

    /// <summary>"Scanned 2 minutes ago", or "Rescanning · 12,345" while a rescan runs.</summary>
    private void RefreshScanStatus()
    {
        if (_model.IsScanning && _model.Tree is not null)
        {
            ScanStatusHost.Content = Rd.Figure($"Rescanning · {_model.ScanProgress:N0}", 11, null, "RdInk2");
            return;
        }
        if (_model.Tree is not null && _model.LastScanFinishedAt is { } finished)
        {
            ScanStatusHost.Content = Rd.Text($"Scanned {Rd.RelativeTime(finished, DateTimeOffset.Now)}", 12, null, "RdInk3");
            return;
        }
        ScanStatusHost.Content = null;
    }

    // ---- WIN-053: command palette (⌘K port) ----

    private CommandPalette? _palette;

    /// <summary>Ctrl+K: the command palette over the whole window (Mac: an overlay with a dim backdrop).</summary>
    private void OpenCommandPalette()
    {
        if (_palette is not null || _model.Tree is null) return;
        _palette = new CommandPalette("", ClosePalette);
        Grid.SetRowSpan(_palette, 5);
        Panel.SetZIndex(_palette, 20);
        WindowChrome.SetIsHitTestVisibleInChrome(_palette, true);
        ShellRoot.Children.Add(_palette);
    }

    private void ClosePalette()
    {
        if (_palette is null) return;
        ShellRoot.Children.Remove(_palette);
        _palette = null;
        PageHost.Focus();
    }

    /// <summary>
    /// Rescan (Secondary style): a click re-reads what changed; its menu (right-click, or the
    /// keyboard's menu key) offers Quick Update and Full Rescan, like the Mac's Rescan menu.
    /// Shown once a scan exists; disabled while one runs.
    /// </summary>
    private void RefreshScanButton()
    {
        if (_model.Tree is null)
        {
            RescanButtonHost.Content = null;
            return;
        }
        var button = Rd.SecondaryButton("Rescan", async () => await Rescan());
        button.IsEnabled = !_model.IsScanning;
        button.ToolTip = "Rescan — re-reads only what changed. Right-click for a full rescan.";
        var menu = new ContextMenu();
        var quick = new MenuItem { Header = "Quick Update — re-read what changed" };
        quick.Click += async (_, _) => await Rescan();
        var full = new MenuItem { Header = "Full Rescan — walk every folder" };
        full.Click += async (_, _) =>
        {
            if (_model.RootPath is { } root) ScanCache.Remove(root);
            await Rescan();
        };
        menu.Items.Add(quick);
        menu.Items.Add(full);
        button.ContextMenu = menu;
        RescanButtonHost.Content = button;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScanModel.IsScanning):
                RefreshScanButton();
                RefreshScanStatus();
                // A scan from the first launch always lands on Overview's scanning board.
                if (_model.IsScanning && _model.Tree is null && _currentPage != "Overview") SelectPage("Overview");
                break;
            case nameof(ScanModel.ScanStats):
            case nameof(ScanModel.ScanPhase):
                RefreshScanStatus();
                break;
            case nameof(ScanModel.ReclaimEstimate):
                RefreshBasket();
                break;
        }
    }

    // ---- Scanning ----

    private async Task PickAndScan()
    {
        if (_model.IsScanning) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        var path = FolderPicker.Pick(hwnd);
        if (path is null) return;
        try
        {
            await _model.ScanAsync(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Scan failed: {ex.Message}", "freedisk.space",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task Rescan()
    {
        if (_model.RootPath is null)
        {
            await PickAndScan();
            return;
        }
        if (_model.IsScanning) return;
        try
        {
            await _model.RescanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Rescan failed: {ex.Message}", "freedisk.space",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
