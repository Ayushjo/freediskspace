using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-111: Applications (Mac: AppsView.swift, redesign board 09). Every installed program with
/// its own size and the data it keeps in AppData, ProgramData and Documents, beside an
/// inspector that shows what uninstalling frees. Windows never removes a program's own files
/// here: the program is uninstalled from Settings › Apps or its own uninstaller (its registry
/// command can be copied, never run), and Clean up puts the leftovers you tick in the basket.
/// </summary>
public sealed class AppsBoardPage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    public enum SortKey { Size, LastOpened, Name }

    private readonly Grid _layout = new();
    private readonly Grid _main = new();
    private readonly Grid _inspectorHost = new();
    private readonly ScrollViewer _tableScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly ScrollViewer _inspectorScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly Dictionary<string, FrameworkElement> _rowsById = [];
    private readonly DispatcherTimer _flush = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ConcurrentQueue<ApplicationEntry> _measured = new();

    private List<ApplicationEntry> _apps = [];
    private Dictionary<string, InstalledApp> _sources = [];
    private bool _isLoading = true;
    private bool _measuring;
    private string? _selectedId;
    private SortKey _sortKey = SortKey.Size;
    private bool _onlyStale;
    private string _query = "";
    private CancellationTokenSource? _load;
    /// <summary>Leftover paths the person unticked or ticked, over the defaults.</summary>
    private readonly Dictionary<string, bool> _leftoverChoice = new(StringComparer.OrdinalIgnoreCase);
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private readonly FrameworkElement _searchField;
    private TextBox? _search;
    private DispatcherTimer? _debounce;

    public AppsBoardPage()
    {
        Background = Ui.Brush("RdPage");
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        _layout.Children.Add(_main);
        var line = new Border { Background = Ui.Brush("RdLine") };
        Grid.SetColumn(line, 1);
        _layout.Children.Add(line);
        _inspectorHost.Background = Ui.Brush("RdSidebar");
        Grid.SetColumn(_inspectorHost, 2);
        _layout.Children.Add(_inspectorHost);
        Content = _layout;
        Focusable = true;
        _searchField = SearchField();

        _flush.Tick += (_, _) => Drain();
        Loaded += (_, _) =>
        {
            Model.StagedChanged += OnStaged;
            Reload();
        };
        Unloaded += (_, _) =>
        {
            Model.StagedChanged -= OnStaged;
            _load?.Cancel();
            _flush.Stop();
        };
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != _class) Rebuild(); };
        PreviewKeyDown += OnKey;
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    // ---- Words (pure; tested) ----

    public static string SortTitle(SortKey key) => key switch
    {
        SortKey.Size => "Size",
        SortKey.LastOpened => "Last opened",
        _ => "Name",
    };

    /// <summary>Last opened puts the longest-unused first: that is what you look for.</summary>
    public static ApplicationSort CatalogSort(SortKey key) => key switch
    {
        SortKey.Size => ApplicationSort.SizeDesc,
        SortKey.LastOpened => ApplicationSort.LastUsedAsc,
        _ => ApplicationSort.NameAsc,
    };

    /// <summary>"45 apps use 2.89 GB, and the data they keep uses another 1.76 GB."</summary>
    public static string Subtitle(bool loading, IReadOnlyCollection<ApplicationEntry> apps) =>
        loading && apps.Count == 0
            ? "Finding your apps…"
            : $"{apps.Count} apps use {ShellFormat.HeroBytes(apps.Sum(a => a.AppBytes))}, and the data they keep uses another {ShellFormat.HeroBytes(apps.Sum(a => a.RelatedBytes))}.";

    /// <summary>The "Not opened in 6 months" pill's figure: "none" or "3 · 4.20 GB".</summary>
    public static string StaleText(IReadOnlyCollection<ApplicationEntry> stale) =>
        stale.Count == 0 ? "none" : $"{stale.Count} · {ShellFormat.HeroBytes(stale.Sum(a => a.TotalBytes))}";

    public static string PlacesLabel(int count) => $"LEFTOVERS · {count} PLACE{(count == 1 ? "" : "S")}";

    /// <summary>"Valve Corporation · version 2.10.91.91".</summary>
    public static string PublisherLine(string? publisher, string? version) =>
        string.Join(" · ", new[] { publisher, version is null ? null : "version " + version }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>The inspector's big button.</summary>
    public static string PrimaryTitle(bool staged, int tickedCount, long tickedBytes) =>
        staged ? "In the basket"
        : tickedCount == 0 ? "Tick the leftovers to clean up"
        : $"Clean up · add {ShellFormat.HeroBytes(tickedBytes)} to basket";

    /// <summary>"Installed  Sep 2026" (the Mac's month and year).</summary>
    public static string MonthYear(DateTimeOffset date) =>
        date.ToLocalTime().ToString("MMM yyyy", CultureInfo.CurrentCulture);

    public static string Guidance(ApplicationEntry app) => app.Source == ApplicationSource.Store
        ? $"Microsoft Store apps are managed by Windows and keep their data with them. Uninstall {app.Name} from Settings › Apps."
        : $"freedisk.space never removes a program itself. Uninstall {app.Name} from Settings › Apps or with its own uninstaller; Clean up puts the leftovers you tick in the basket.";

    // ---- State ----

    private bool IsTicked(ApplicationRelatedItem item) =>
        _leftoverChoice.TryGetValue(item.Path, out bool on) ? on : ApplicationsCatalog.TickedByDefault(item.Kind);

    private static bool IsStaged(ApplicationEntry app) => app.Related.Any(r => Model.IsStaged(r.Path));

    private List<ApplicationEntry> Visible() =>
        ApplicationsCatalog.Visible(_apps, _onlyStale, _query, CatalogSort(_sortKey), DateTimeOffset.Now);

    private ApplicationEntry? Active(List<ApplicationEntry> visible) =>
        (_selectedId is { } id ? _apps.FirstOrDefault(a => a.Id == id) : null) ?? visible.FirstOrDefault();

    // ---- Load / measure ----

    private async void Reload()
    {
        _load?.Cancel();
        var cts = new CancellationTokenSource();
        _load = cts;
        _isLoading = true;
        Rebuild();
        var (installed, stubs, places, broad) = await Task.Run(() =>
        {
            var list = new List<InstalledApp>();
            try
            {
                list.AddRange(AppLeftoverFinder.InstalledApplications());
                list.AddRange(AppLeftoverFinder.StoreApplications());
            }
            catch { /* no registry to read: no apps */ }
            // One row per id (a key name can repeat across the registry views).
            list = list.GroupBy(ApplicationsCatalog.EntryId).Select(g => g.First()).ToList();
            var broadFolders = ApplicationsCatalog.BroadFolders();
            var launches = ProgramUsage.ReadUserAssist();
            var built = list.Select(a => ApplicationsCatalog.Stub(a, ProgramUsage.LastUsed(a, launches, broadFolders), broadFolders)).ToList();
            return (list, built, ApplicationsCatalog.Places.Current(), broadFolders);
        });
        if (cts.IsCancellationRequested) return;
        // Keep what was measured last time on screen until it is measured again.
        var previous = _apps.Where(a => !a.SizePending).ToDictionary(a => a.Id);
        _apps = ApplicationsCatalog.Sorted(
            stubs.Select(s => previous.TryGetValue(s.Id, out var old) ? old with { LastUsed = s.LastUsed } : s),
            CatalogSort(_sortKey));
        _sources = installed.ToDictionary(ApplicationsCatalog.EntryId);
        if (_selectedId is { } sel && _apps.All(a => a.Id != sel)) _selectedId = null;
        _isLoading = false;
        Rebuild();

        // Every installed program, so one program's leftovers never claim a sibling's data.
        var programs = installed.Where(a => !a.IsStorePackage).ToList();
        var work = stubs.Select(s => (Stub: s, App: _sources[s.Id])).ToList();
        _measuring = true;
        _flush.Start();
        try
        {
            await Task.Run(() => Parallel.ForEachAsync(work,
                new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cts.Token },
                (item, _) =>
                {
                    ApplicationEntry measured;
                    try { measured = ApplicationsCatalog.Measure(item.Stub, item.App, programs, places, broad); }
                    catch { measured = item.Stub with { AppBytes = item.App.EstimatedBytes ?? 0, SizePending = false }; }
                    _measured.Enqueue(measured);
                    return ValueTask.CompletedTask;
                }));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* a program that can't be measured keeps its "…" */ }
        if (_load == cts) _measuring = false;
    }

    /// <summary>Puts measured programs in place a few times a second (one rebuild for many).</summary>
    private void Drain()
    {
        bool any = false;
        while (_measured.TryDequeue(out var entry))
        {
            int at = _apps.FindIndex(a => a.Id == entry.Id);
            if (at < 0) continue;
            _apps[at] = entry with { LastUsed = _apps[at].LastUsed };
            any = true;
        }
        if (any)
        {
            _apps = ApplicationsCatalog.Sorted(_apps, CatalogSort(_sortKey));
            Rebuild();
        }
        else if (!_measuring) _flush.Stop();
    }

    // ---- Build ----

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        _layout.ColumnDefinitions[2].Width = new GridLength(_class switch
        {
            Rd.WidthClass.Compact => 300, Rd.WidthClass.Regular => 330, _ => 370,
        });
        var visible = Visible();
        var active = Active(visible);
        BuildMain(visible, active);
        BuildInspector(active);
    }

    private void BuildMain(List<ApplicationEntry> visible, ApplicationEntry? active)
    {
        _main.Children.Clear();
        _main.RowDefinitions.Clear();
        double pad = _class.PagePadding();
        _main.Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, pad);
        double gap = _class.Gap();
        void add(FrameworkElement e, GridLength height, double top)
        {
            _main.RowDefinitions.Add(new RowDefinition { Height = height });
            e.Margin = new Thickness(0, top, 0, 0);
            Grid.SetRow(e, _main.RowDefinitions.Count - 1);
            _main.Children.Add(e);
        }

        var titles = new StackPanel();
        titles.Children.Add(Rd.PageTitle("Applications"));
        var sub = Rd.Text(Subtitle(_isLoading, _apps), 13, null, "RdInk2");
        sub.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(sub);
        add(titles, GridLength.Auto, 0);
        add(Controls(), GridLength.Auto, gap);

        if (_isLoading && _apps.Count == 0)
        {
            add(LoadingState("Finding applications", "Reading the installed programs Windows lists in the registry."),
                new GridLength(1, GridUnitType.Star), gap);
        }
        else if (visible.Count == 0)
        {
            add(EmptyState(Icons.Applications, _apps.Count == 0 ? "No applications found" : "No apps match",
                    _apps.Count == 0 ? "freedisk.space couldn’t find installed programs in the Windows registry."
                                     : "Try another name, or show every app."),
                new GridLength(1, GridUnitType.Star), gap);
        }
        else
        {
            add(Table(visible, active), new GridLength(1, GridUnitType.Star), gap);
            if (_class == Rd.WidthClass.Wide)
                add(Rd.Text("Uninstall a program from Settings › Apps. Clean up puts the leftovers you tick in the basket. Nothing moves until you review it there.",
                    12, null, "RdInk3"), GridLength.Auto, gap);
        }
    }

    private FrameworkElement Controls()
    {
        var dock = new DockPanel();
        // The field moves to the new row; typing keeps its focus and caret.
        bool typing = _search?.IsKeyboardFocusWithin == true;
        int caret = _search?.CaretIndex ?? 0;
        if (_searchField.Parent is Panel old) old.Children.Remove(_searchField);
        _searchField.Width = _class == Rd.WidthClass.Compact ? 160 : 220;
        _searchField.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_searchField, Dock.Right);
        dock.Children.Add(_searchField);
        if (typing && _search is { } box)
            Dispatcher.InvokeAsync(() => { box.Focus(); box.CaretIndex = Math.Min(caret, box.Text.Length); }, DispatcherPriority.Input);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Rd.Figure("SORT", 10.5, FontWeights.Medium, "RdInk3"));
        var segments = Rd.SegmentSwitch(Enum.GetValues<SortKey>().Select(key =>
            new Rd.Segment(SortTitle(key), _sortKey == key, () =>
            {
                _sortKey = key;
                _apps = ApplicationsCatalog.Sorted(_apps, CatalogSort(key));
                Rebuild();
            })));
        segments.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(segments);
        if (_class != Rd.WidthClass.Compact)
        {
            var show = Rd.Figure("SHOW", 10.5, FontWeights.Medium, "RdInk3");
            show.Margin = new Thickness(12 + 8, 0, 0, 0);
            row.Children.Add(show);
        }
        var toggle = StaleToggle();
        toggle.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(toggle);
        dock.Children.Add(row);
        return dock;
    }

    /// <summary>"☐ Not opened in 6 months  3 · 4.20 GB": a card pill, an ink border when on.</summary>
    private Border StaleToggle()
    {
        var now = DateTimeOffset.Now;
        var stale = _apps.Where(a => a.IsNotRecentlyUsed(now)).ToList();
        void flip() { _onlyStale = !_onlyStale; Rebuild(); }
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var box = CleanUpPage.TickBox(_onlyStale ? CleanUpPage.TickState.On : CleanUpPage.TickState.Off, flip);
        System.Windows.Automation.AutomationProperties.SetName(box, "Not opened in 6 months");
        row.Children.Add(box);
        var title = Rd.Text("Not opened in 6 months", 13);
        title.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(title);
        var count = Rd.Figure(StaleText(stale), 11, null, "RdInk3");
        count.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(count);
        var pill = new Border
        {
            Height = 30, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(8),
            Background = Ui.Brush("RdCard"), BorderBrush = Ui.Brush(_onlyStale ? "RdInk" : "RdLine"),
            BorderThickness = new Thickness(1), Child = row, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
        };
        pill.MouseLeftButtonUp += (_, e) => { if (!e.Handled) flip(); };
        System.Windows.Automation.AutomationProperties.SetName(pill,
            $"Not opened in 6 months, {StaleText(stale)}, {(_onlyStale ? "on" : "off")}");
        return pill;
    }

    /// <summary>DiskMapSearchField: card fill, hairline (accent when focused), magnifier, clear, Esc clears.</summary>
    private FrameworkElement SearchField()
    {
        var box = new TextBox
        {
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = Rd.FontFor(13), FontSize = Ui.Scaled(13), Foreground = Ui.Brush("RdInk"), CaretBrush = Ui.Brush("RdInk"),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Find an app");
        var hint = new TextBlock
        {
            Text = "Find an app", FontFamily = Rd.FontFor(13), FontSize = Ui.Scaled(13), Foreground = Ui.Brush("RdInk3"),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
        };
        var glass = Ui.Glyph(Icons.Search, 12, Ui.Brush("RdInk3"));
        glass.Margin = new Thickness(0, 0, 8, 0);
        glass.VerticalAlignment = VerticalAlignment.Center;
        var clear = Rd.IconButton(Icons.Cancel, "Clear search", () => { box.Text = ""; box.Focus(); }, 18);
        clear.Visibility = Visibility.Collapsed;
        clear.ToolTip = "Clear (Esc)";
        var face = new Border
        {
            Height = 32, CornerRadius = new CornerRadius(8), Background = Ui.Brush("RdCard"),
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1), Cursor = Cursors.IBeam,
        };
        box.TextChanged += (_, _) =>
        {
            _query = box.Text;
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            clear.Visibility = box.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            DebouncedRebuild();
        };
        box.GotKeyboardFocus += (_, _) =>
        {
            face.BorderBrush = Rd.WithOpacity(Ui.Brush("AppAccent"), 0.75);
            Ui.SetIconBrush(glass, Ui.Brush("RdInk2"));
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            face.BorderBrush = Ui.Brush("RdLine");
            Ui.SetIconBrush(glass, Ui.Brush("RdInk3"));
        };
        box.KeyDown += (_, e) => { if (e.Key == Key.Escape) { box.Text = ""; e.Handled = true; } };
        face.MouseLeftButtonDown += (_, _) => box.Focus();
        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(box);
        var row = new DockPanel { Margin = new Thickness(11, 0, 11, 0) };
        DockPanel.SetDock(glass, Dock.Left);
        row.Children.Add(glass);
        DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(clear);
        row.Children.Add(field);
        face.Child = row;
        _search = box;
        return face;
    }

    private void DebouncedRebuild()
    {
        _debounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Stop();
        _debounce.Tick -= OnDebounce;
        _debounce.Tick += OnDebounce;
        _debounce.Start();
    }

    private void OnDebounce(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        Rebuild();
    }

    // ---- Table ----

    private double LastOpenedWidth => _class == Rd.WidthClass.Compact ? 110 : 190;

    /// <summary>App | App | Data | Total | Last opened | action, the same widths as the Mac.</summary>
    private Grid Columns()
    {
        var grid = new Grid { Margin = new Thickness(16, 0, 16, 0) };
        void col(GridLength w) => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        col(new GridLength(1, GridUnitType.Star));
        col(new GridLength(84));
        if (_class != Rd.WidthClass.Compact) col(new GridLength(84));
        col(new GridLength(90));
        col(new GridLength(LastOpenedWidth + 24));
        col(new GridLength(96));
        return grid;
    }

    private static void Put(Grid grid, FrameworkElement e, int column, bool right = false)
    {
        e.VerticalAlignment = VerticalAlignment.Center;
        if (right)
        {
            e.HorizontalAlignment = HorizontalAlignment.Right;
            if (e is TextBlock t) t.TextAlignment = TextAlignment.Right;
        }
        Grid.SetColumn(e, column);
        grid.Children.Add(e);
    }

    private FrameworkElement Table(List<ApplicationEntry> items, ApplicationEntry? active)
    {
        bool showData = _class != Rd.WidthClass.Compact;
        var head = Columns();
        int c = 0;
        Put(head, Rd.Text("App", 12, null, "RdInk3"), c++);
        Put(head, Rd.Text("App", 12, null, "RdInk3"), c++, right: true);
        if (showData) Put(head, Rd.Text("Data", 12, null, "RdInk3"), c++, right: true);
        Put(head, Rd.Text(_sortKey == SortKey.Size ? "Total ↓" : "Total", 12, null, _sortKey == SortKey.Size ? "RdInk" : "RdInk3"), c++, right: true);
        var last = Rd.Text(_sortKey == SortKey.LastOpened ? "Last opened ↑" : "Last opened", 12, null,
            _sortKey == SortKey.LastOpened ? "RdInk" : "RdInk3");
        last.Margin = new Thickness(24, 0, 0, 0);
        Put(head, last, c);
        var header = new Border
        {
            Height = 40, Child = head,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var rows = new StackPanel();
        _rowsById.Clear();
        foreach (var app in items)
        {
            var row = Row(app, app.Id == active?.Id, showData);
            _rowsById[app.Id] = row;
            rows.Children.Add(row);
        }
        double offset = _tableScroll.VerticalOffset;
        if (_tableScroll.Parent is Panel old) old.Children.Remove(_tableScroll);
        _tableScroll.Content = rows;
        _tableScroll.ScrollToVerticalOffset(offset);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(header);
        Grid.SetRow(_tableScroll, 1);
        grid.Children.Add(_tableScroll);
        return Rd.Card(grid);
    }

    private FrameworkElement Row(ApplicationEntry app, bool selected, bool showData)
    {
        bool staged = IsStaged(app);
        bool compact = _class == Rd.WidthClass.Compact;
        string lastOpened = ApplicationsCatalog.LastOpenedText(app.LastUsed, DateTimeOffset.Now);
        var grid = Columns();
        int c = 0;

        var first = new StackPanel { Orientation = Orientation.Horizontal };
        first.Children.Add(AppTile(28));
        var name = Rd.Text(app.Name, 13.5, FontWeights.Medium);
        name.Margin = new Thickness(12, 0, 0, 0);
        first.Children.Add(name);
        Border? store = null;
        if (app.Source == ApplicationSource.Store)
        {
            store = Rd.Tag("Store");
            store.Margin = new Thickness(12, 0, 0, 0);
            store.VerticalAlignment = VerticalAlignment.Center;
            first.Children.Add(store);
        }
        // The name trims and the tag stays beside it (lineLimit(1) in a flexible frame).
        var firstCell = new Grid { ClipToBounds = true };
        firstCell.Children.Add(first);
        firstCell.SizeChanged += (_, e) =>
        {
            store?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tag = store is null ? 0 : store.DesiredSize.Width + 12;
            name.MaxWidth = Math.Max(0, e.NewSize.Width - 28 - 12 - tag - 8);
        };
        Put(grid, firstCell, c++);

        Put(grid, Rd.Figure(app.SizePending ? "…" : ShellFormat.HeroBytes(app.AppBytes), 12), c++, right: true);
        if (showData)
            Put(grid, Rd.Figure(app.SizePending ? "…" : app.RelatedBytes > 0 ? ShellFormat.HeroBytes(app.RelatedBytes) : "—", 12, null,
                app.RelatedBytes >= app.AppBytes ? "RdInk" : "RdInk3"), c++, right: true);
        Put(grid, Rd.Figure(app.SizePending ? "…" : ShellFormat.HeroBytes(app.TotalBytes), 12, FontWeights.SemiBold), c++, right: true);

        var opened = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0) };
        opened.Children.Add(Rd.Text(lastOpened, 13, null, "RdInk2"));
        if (app.IsNotRecentlyUsed(DateTimeOffset.Now) && !compact)
        {
            var tag = Rd.Tag("6 mo+", Rd.TagTone.Accent);
            tag.Margin = new Thickness(8, 0, 0, 0);
            tag.VerticalAlignment = VerticalAlignment.Center;
            opened.Children.Add(tag);
        }
        opened.Width = LastOpenedWidth;
        opened.HorizontalAlignment = HorizontalAlignment.Left;
        opened.ClipToBounds = true;
        Put(grid, opened, c++);

        FrameworkElement? action = null;
        if (!app.CanStageForCleanup) { }
        else if (staged) action = Rd.Tag("In basket", Rd.TagTone.Accent);
        else
        {
            var button = selected ? Rd.AccentButton("Clean up", () => CleanUp(app)) : Rd.SecondaryButton("Clean up", () => CleanUp(app));
            button.ToolTip = "Put the leftovers you tick in the basket";
            System.Windows.Automation.AutomationProperties.SetName(button, $"Clean up {app.Name}");
            action = button;
        }
        if (action is not null) Put(grid, action, c, right: true);

        var host = new Border
        {
            Height = 50, Child = grid, Cursor = Cursors.Hand,
            Background = selected ? Ui.Brush("RdAccentWash") : Brushes.Transparent,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
        };
        host.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Reveal(app);
            else Select(app.Id);
        };
        host.ContextMenu = RowMenu(app);
        System.Windows.Automation.AutomationProperties.SetName(host,
            $"{app.Name}, {(app.SizePending ? "size pending" : ByteFormat.Format(app.TotalBytes))}, opened {lastOpened}");
        return host;
    }

    /// <summary>The row's menu: reveal the program and its leftovers, copy its uninstall command, clean up.</summary>
    private ContextMenu RowMenu(ApplicationEntry app)
    {
        var menu = new ContextMenu();
        void item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            System.Windows.Automation.AutomationProperties.SetName(mi, header);
            menu.Items.Add(mi);
        }
        if (app.CanStageForCleanup && !IsStaged(app)) item("Add leftovers to basket", () => CleanUp(app));
        item("Show in File Explorer", () => Reveal(app), app.InstallPath.Length > 0);
        foreach (var r in app.Related.Take(5))
        {
            string path = r.Path;
            item($"Reveal {ApplicationsCatalog.LastSegment(path)}", () => Explorer.Reveal(path));
        }
        if (app.UninstallCommand is { } cmd)
        {
            menu.Items.Add(new Separator());
            item("Copy uninstall command", () => CopyUninstall(app.Name, cmd));
        }
        return menu;
    }

    /// <summary>The app's tile: a card square with the app glyph (radius 22% of its size, like the Mac's icons).</summary>
    private static Border AppTile(double size)
    {
        var glyph = Ui.Glyph(Icons.Applications, Math.Round(size * 0.5), Ui.Brush("RdInk2"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.22),
            Background = Ui.Brush("RdCard"), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1),
            Child = glyph, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void Select(string id)
    {
        Focus();
        if (_selectedId == id) return;
        _selectedId = id;
        Rebuild();
    }

    // ---- Inspector ----

    private void BuildInspector(ApplicationEntry? app)
    {
        _inspectorHost.Children.Clear();
        _inspectorHost.RowDefinitions.Clear();
        if (app is null)
        {
            _inspectorHost.Children.Add(EmptyState(Icons.Applications, "Select an app", "See its data and what uninstalling frees."));
            return;
        }
        long tickedBytes = app.Related.Where(IsTicked).Sum(r => r.Bytes);
        var ticked = app.Related.Where(IsTicked).ToList();
        var unticked = app.Related.Where(r => !IsTicked(r)).ToList();
        long frees = ApplicationsCatalog.UninstallBytes(app, IsTicked);
        bool staged = IsStaged(app);

        var stack = new StackPanel { Margin = new Thickness(20) };
        void add(FrameworkElement e) { e.Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 16, 0, 0); stack.Children.Add(e); }

        // Icon, name, publisher · version, path.
        var head = new DockPanel();
        var tile = AppTile(56);
        tile.VerticalAlignment = VerticalAlignment.Top;
        tile.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var names = new StackPanel();
        var title = Rd.Text(app.Name, 20, FontWeights.SemiBold, wrap: true);
        title.MaxHeight = Math.Ceiling(Ui.Scaled(20) * Rd.TextLineHeight) * 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        names.Children.Add(title);
        string publisher = PublisherLine(app.Publisher, app.Version);
        if (publisher.Length > 0)
        {
            var p = Rd.Text(publisher, 12, null, "RdInk2");
            p.Margin = new Thickness(0, 3, 0, 0);
            names.Children.Add(p);
        }
        var where = ExploreInspector.Fitted(Rd.Figure(app.InstallPath.Length > 0 ? CanonicalPath.DisplayPath(app.InstallPath) : "No install folder recorded",
            11, null, "RdInk3"), 1, middle: true);
        where.Margin = new Thickness(0, 3, 0, 0);
        names.Children.Add(where);
        head.Children.Add(names);
        add(head);

        // Uninstall frees.
        var frame = new StackPanel();
        frame.Children.Add(Rd.Figure("UNINSTALL FREES", 10.5, FontWeights.Medium, "RdInk3"));
        var hero = Rd.TightText(app.SizePending ? "Measuring…" : ShellFormat.HeroBytes(frees), 44, 0);
        frame.Children.Add(new Viewbox
        {
            Child = hero, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0),
        });
        add(frame);
        add(UninstallBar(app.AppBytes, tickedBytes, unticked.Sum(r => r.Bytes)));

        var legend = new StackPanel();
        legend.Children.Add(Legend(new Border { Background = Ui.Brush("RdInk") }, "The app", app.AppBytes, accent: false));
        var hatch = Legend(new Border
        {
            Background = Rd.HatchBrush(Ui.Brush("AppAccent"), Ui.Brush("RdAccentWash"), 4, 1.5),
            BorderBrush = Ui.Brush("AppAccent"), BorderThickness = new Thickness(1),
        }, "Leftovers ticked", tickedBytes, accent: true);
        hatch.Margin = new Thickness(0, 8, 0, 0);
        legend.Children.Add(hatch);
        if (unticked.Count > 0)
        {
            var kept = Legend(new Border
            {
                Background = Ui.Brush("RdSoft"), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1),
            }, "Kept", unticked.Sum(r => r.Bytes), accent: false);
            kept.Margin = new Thickness(0, 8, 0, 0);
            legend.Children.Add(kept);
        }
        add(legend);

        if (app.Related.Count > 0) add(Leftovers(app));
        else if (app.SizePending) add(Rd.Text("Looking for its data…", 12, null, "RdInk3"));

        if (unticked.Any(r => ApplicationsCatalog.MayHoldYourFiles(r.Kind))) add(FilesCallout(app.Name));

        var facts = new StackPanel();
        facts.Children.Add(Fact("Last opened", ApplicationsCatalog.LastOpenedText(app.LastUsed, DateTimeOffset.Now)));
        if (app.Installed is { } installed) facts.Children.Add(Fact("Installed", MonthYear(installed)));
        facts.Children.Add(Fact("From", ApplicationsCatalog.SourceTitle(app.Source)));
        for (int i = 1; i < facts.Children.Count; i++) ((FrameworkElement)facts.Children[i]).Margin = new Thickness(0, 8, 0, 0);
        add(facts);

        add(Rd.Text(Guidance(app), 12, null, "RdInk2", wrap: true));

        double offset = _inspectorScroll.VerticalOffset;
        _inspectorScroll.Content = stack;
        _inspectorScroll.ScrollToVerticalOffset(offset);

        // Footer: reveal, copy the uninstall command, and the big button.
        var footer = new StackPanel { Margin = new Thickness(20) };
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var reveal = Rd.SecondaryButton("Show in File Explorer", () => Reveal(app), fullWidth: true);
        reveal.IsEnabled = app.InstallPath.Length > 0;
        reveal.ToolTip = app.InstallPath.Length > 0 ? app.InstallPath : "The registry names no install folder for this program";
        pair.Children.Add(reveal);
        if (app.UninstallCommand is { } cmd)
        {
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var copy = Rd.SecondaryButton("Copy uninstall command", () => CopyUninstall(app.Name, cmd), fullWidth: true);
            copy.ToolTip = "Copies the program's own uninstall command. freedisk.space never runs it.";
            Grid.SetColumn(copy, 2);
            pair.Children.Add(copy);
        }
        footer.Children.Add(pair);
        if (app.CanStageForCleanup)
        {
            var primary = Rd.AccentButton(PrimaryTitle(staged, ticked.Count, tickedBytes), () =>
            {
                if (staged) Model.OpenBasket(); else CleanUp(app);
            }, large: true, fullWidth: true, hard: !staged);
            primary.IsEnabled = !app.SizePending && (staged || ticked.Count > 0);
            primary.Margin = new Thickness(0, 10, 3, 3);
            footer.Children.Add(primary);
        }

        _inspectorHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _inspectorHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (_inspectorScroll.Parent is Panel old) old.Children.Remove(_inspectorScroll);
        _inspectorHost.Children.Add(_inspectorScroll);
        Grid.SetRow(footer, 1);
        _inspectorHost.Children.Add(footer);
    }

    /// <summary>The inspector's bar: the app in ink, ticked leftovers hatched, kept data pale.</summary>
    private static FrameworkElement UninstallBar(long app, long ticked, long kept)
    {
        double total = Math.Max(1, app + ticked + kept);
        var grid = new Grid { Height = 12 };
        void segment(Brush fill, double share, double min = 0)
        {
            if (grid.ColumnDefinitions.Count > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 1e-6), GridUnitType.Star), MinWidth = min });
            var block = new Border { Background = fill };
            Grid.SetColumn(block, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(block);
        }
        segment(Ui.Brush("RdInk"), app / total, 3);
        if (ticked > 0) segment(Rd.HatchBrush(Ui.Brush("AppAccent"), Ui.Brush("RdAccentWash"), 5, 2), ticked / total);
        if (kept > 0) segment(Ui.Brush("RdSoft"), kept / total);
        var clip = new Border { CornerRadius = new CornerRadius(3), Child = grid, Height = 12 };
        clip.SizeChanged += (_, e) => clip.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 3, 3);
        return clip;
    }

    private static FrameworkElement Legend(Border swatch, string title, long bytes, bool accent)
    {
        swatch.Width = 12;
        swatch.Height = 12;
        swatch.CornerRadius = new CornerRadius(2);
        swatch.VerticalAlignment = VerticalAlignment.Center;
        swatch.Margin = new Thickness(0, 0, 10, 0);
        var row = new DockPanel();
        DockPanel.SetDock(swatch, Dock.Left);
        row.Children.Add(swatch);
        var size = Rd.Figure(ShellFormat.HeroBytes(bytes), 12, null, accent ? "RdAccentDeep" : "RdInk");
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        row.Children.Add(Rd.Text(title, 13, null, accent ? "RdAccentDeep" : "RdInk"));
        System.Windows.Automation.AutomationProperties.SetName(row, $"{title}, {ByteFormat.Format(bytes)}");
        return row;
    }

    private FrameworkElement Leftovers(ApplicationEntry app)
    {
        var stack = new StackPanel();
        stack.Children.Add(Rd.Figure(PlacesLabel(app.Related.Count), 10.5, FontWeights.Medium, "RdInk3"));
        var list = new StackPanel();
        int index = 0;
        foreach (var item in app.Related.OrderByDescending(r => r.Bytes).Take(12))
        {
            bool on = IsTicked(item);
            string path = item.Path;
            void toggle() { _leftoverChoice[path] = !on; Rebuild(); }
            var box = CleanUpPage.TickBox(on ? CleanUpPage.TickState.On : CleanUpPage.TickState.Off, toggle);
            box.VerticalAlignment = VerticalAlignment.Top;
            box.Margin = new Thickness(-6, 2 - 6, 10 - 6, -6);
            System.Windows.Automation.AutomationProperties.SetName(box, $"{ApplicationsCatalog.KindTitle(item.Kind)}: {(on ? "ticked" : "not ticked")}");
            var top = new DockPanel();
            var size = Rd.Figure(ShellFormat.HeroBytes(item.Bytes), 12);
            size.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(size, Dock.Right);
            top.Children.Add(size);
            top.Children.Add(Rd.Text(ApplicationsCatalog.KindTitle(item.Kind), 13, FontWeights.Medium, on ? "RdInk" : "RdInk2"));
            var texts = new StackPanel();
            texts.Children.Add(top);
            var where = ExploreInspector.Fitted(Rd.Figure(CanonicalPath.DisplayPath(item.Path), 10.5, null, "RdInk3"), 1, middle: true);
            where.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(where);
            var row = new DockPanel();
            DockPanel.SetDock(box, Dock.Left);
            row.Children.Add(box);
            row.Children.Add(texts);
            var host = new Border
            {
                Padding = new Thickness(12, 10, 12, 10), Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, index > 0 ? 1 : 0, 0, 0),
                ToolTip = item.Path,
            };
            host.MouseLeftButtonUp += (_, e) => { if (!e.Handled) toggle(); };
            var menu = new ContextMenu();
            var reveal = new MenuItem { Header = "Show in File Explorer" };
            reveal.Click += (_, _) => Explorer.Reveal(path);
            menu.Items.Add(reveal);
            host.ContextMenu = menu;
            System.Windows.Automation.AutomationProperties.SetName(host,
                $"{ApplicationsCatalog.KindTitle(item.Kind)}, {ByteFormat.Format(item.Bytes)}, {(on ? "will be removed" : "kept")}");
            list.Children.Add(host);
            index++;
        }
        var card = Rd.Surface(list, "RdCard", 10);
        card.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(card);
        return stack;
    }

    /// <summary>"Your files may be in here": a card with a violet rule on its leading edge.</summary>
    private static FrameworkElement FilesCallout(string name)
    {
        var text = new StackPanel { Margin = new Thickness(14) };
        text.Children.Add(Rd.Text("Your files may be in here", 13, FontWeights.Medium));
        var body = Rd.Text($"AppData and Documents can hold documents, saves or libraries you made in {name}. We left them unticked; tick them to remove them too.",
            13, null, "RdInk2", wrap: true);
        body.Margin = new Thickness(0, 6, 0, 0);
        text.Children.Add(body);
        var rule = new Border
        {
            Width = 3, HorizontalAlignment = HorizontalAlignment.Left,
            Background = Ui.Brush("AppAccent"), CornerRadius = new CornerRadius(10, 0, 0, 10),
        };
        var layers = new Grid();
        layers.Children.Add(text);
        layers.Children.Add(rule);
        return Rd.Surface(layers, "RdCard", 10);
    }

    private static FrameworkElement Fact(string label, string value)
    {
        var row = new DockPanel();
        var v = Rd.Figure(value, 12);
        v.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(v);
        row.Children.Add(Rd.Text(label, 13, null, "RdInk2"));
        return row;
    }

    // ---- States ----

    /// <summary>DiskMapEmptyState: a quiet glyph, title, message, centred in the space.</summary>
    private static FrameworkElement EmptyState(string glyph, string title, string message)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        var icon = Ui.Glyph(glyph, 20, Ui.Brush("RdInk3"));
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(icon);
        var t = Rd.Text(title, 13, FontWeights.Medium);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(t);
        var m = Rd.Text(message, 12, null, "RdInk2", wrap: true);
        m.TextAlignment = TextAlignment.Center;
        m.MaxWidth = 420;
        m.HorizontalAlignment = HorizontalAlignment.Center;
        m.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(m);
        return stack;
    }

    /// <summary>DiskMapLoadingState: a spinner, the title and what is being read.</summary>
    private static FrameworkElement LoadingState(string title, string detail)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4 });
        var t = Rd.Text(title, 13, FontWeights.Medium);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(t);
        var d = Rd.Text(detail, 12, null, "RdInk2", wrap: true);
        d.TextAlignment = TextAlignment.Center;
        d.MaxWidth = 420;
        d.HorizontalAlignment = HorizontalAlignment.Center;
        d.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(d);
        System.Windows.Automation.AutomationProperties.SetName(stack, title);
        return stack;
    }

    // ---- Actions ----

    /// <summary>
    /// The ticked leftovers, through the one confirmation, into the basket. The program itself
    /// is never staged: Windows uninstalls it (Settings › Apps or its own uninstaller).
    /// </summary>
    private void CleanUp(ApplicationEntry app)
    {
        if (!app.CanStageForCleanup || app.SizePending) return;
        var picked = app.Related.Where(IsTicked).ToList();
        if (picked.Count == 0)
        {
            _selectedId = app.Id;
            Rebuild();
            Model.Toast($"Tick the leftovers of {app.Name} you want to remove");
            return;
        }
        var items = picked.Select(r => new Dialogs.ConfirmItem(ApplicationsCatalog.KindTitle(r.Kind), r.Path, r.Bytes)).ToList();
        bool ok = Model.ConfirmBulk?.Invoke($"Clean up {app.Name}",
            $"These leftovers of {app.Name} will be added to the basket. Nothing moves yet — you review the basket and confirm again before anything goes to the Recycle Bin.",
            items, $"freedisk.space never removes {app.Name} itself. Uninstall it from Settings › Apps or with its own uninstaller.") == true;
        if (!ok) return;
        int added = 0;
        foreach (var r in picked)
            if (Model.StagePath(r.Path, r.Bytes, "Application: " + app.Name, notify: false)) added++;
        if (added > 0) Model.ToastAdded(added);
        else Model.Toast("Already in the basket");
    }

    private static void Reveal(ApplicationEntry app)
    {
        if (app.InstallPath.Length > 0) Explorer.Reveal(app.InstallPath);
    }

    /// <summary>Windows' Quick Look stand-in: the install folder opens in File Explorer.</summary>
    private static void OpenFolder(ApplicationEntry app)
    {
        if (app.InstallPath.Length == 0 || !System.IO.Directory.Exists(app.InstallPath)) return;
        try { Process.Start("explorer.exe", $"\"{app.InstallPath}\""); }
        catch { /* explorer missing is not fatal */ }
    }

    /// <summary>The registry's own uninstall command line — copied, never executed.</summary>
    private static void CopyUninstall(string name, string command)
    {
        try
        {
            Clipboard.SetText(command);
            Model.Toast($"Copied {name}’s uninstall command");
        }
        catch { /* clipboard busy */ }
    }

    // ---- Keyboard (Mac: listKeyboard) ----

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or System.Windows.Controls.Primitives.ButtonBase) return;
        var rows = Visible();
        if (rows.Count == 0) return;
        var active = Active(rows);
        int index = active is null ? -1 : rows.FindIndex(r => r.Id == active.Id);
        switch (e.Key)
        {
            case Key.Down or Key.J when Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                Move(rows[Math.Min(rows.Count - 1, index + 1)].Id);
                e.Handled = true;
                break;
            case Key.Up or Key.K when Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                Move(rows[Math.Max(0, index - 1)].Id);
                e.Handled = true;
                break;
            case Key.Enter when active is not null:
                Reveal(active);
                e.Handled = true;
                break;
            case Key.Space when active is not null:
                OpenFolder(active);
                e.Handled = true;
                break;
            case Key.Delete when active is not null:
                CleanUp(active);
                e.Handled = true;
                break;
        }
    }

    private void Move(string id)
    {
        _selectedId = id;
        Rebuild();
        if (_rowsById.TryGetValue(id, out var row)) row.BringIntoView();
    }
}
