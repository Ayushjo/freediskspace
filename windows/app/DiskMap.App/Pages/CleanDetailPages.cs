using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-106: the Clean up detail pages (Mac: Kit/CleanDetail.swift CleanDetailPage) — Caches,
/// Old downloads and Large media as one shell built from the list kit, so they read as
/// Clean up's deeper lists: "‹ Clean up", the title and its figure, the Caches | Old downloads |
/// Large media switch with the page's chips, the search, the table, and a footer that adds
/// what is shown. One row stages through the single-item checks (risky items ask first);
/// several go through the bulk confirmation. Nothing leaves the basket without a second yes.
/// </summary>
public abstract class CleanDetailPage : UserControl
{
    protected static ScanModel Model => ScanModel.Shared;

    private readonly Grid _root = new();
    private TextBox? _search;
    private string? _selectedId;
    private List<CleanDetailRow> _shown = [];
    private System.Windows.Threading.DispatcherTimer? _debounce;

    protected string Query { get; private set; } = "";
    protected Rd.WidthClass WidthClass { get; private set; } = Rd.WidthClass.Regular;

    protected CleanDetailPage()
    {
        Background = Ui.Brush("RdPage");
        Content = _root;
        Focusable = true;
        ModelEvents.WhileLoaded(this, Rebuild);
        Loaded += (_, _) => Model.StagedChanged += OnStaged;
        Unloaded += (_, _) => Model.StagedChanged -= OnStaged;
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != WidthClass) Rebuild(); };
        PreviewKeyDown += OnKey;
    }

    // ---- What each page supplies ----

    /// <summary>The page title, also the confirmation's title and the selection toolbar's label.</summary>
    protected abstract string Title { get; }
    protected abstract string Subtitle { get; }
    /// <summary>ShowPage key: "Caches", "Old Downloads" or "Large Media".</summary>
    protected abstract string PageKey { get; }
    protected abstract string KindTitle { get; }
    protected abstract string ChangedTitle { get; }
    protected abstract string NoteTitle { get; }
    protected abstract string SearchPlaceholder { get; }
    /// <summary>The catalog gate's title ("Finding caches…").</summary>
    protected abstract string LoadingTitle { get; }
    protected abstract bool CatalogReady { get; }

    /// <summary>Rows after every filter; the header's count and size before them.</summary>
    protected abstract (List<CleanDetailRow> Rows, int TotalCount, long TotalBytes, bool NothingAtAll) Compute();
    protected abstract (string Title, string Message) EmptyText(bool nothingAtAll);
    protected abstract string BulkTitle(int count, long bytes);
    protected abstract bool BulkFilter(CleanDetailRow row);
    /// <summary>The chips after the switch.</summary>
    protected abstract IEnumerable<FrameworkElement> Chips();
    /// <summary>The filter row's right end (after the Spacer).</summary>
    protected virtual IEnumerable<FrameworkElement> FilterTrailing() => [];
    /// <summary>Beside the search, at the right edge.</summary>
    protected virtual IEnumerable<FrameworkElement> SearchTrailing() => [];

    protected bool Compact => WidthClass == Rd.WidthClass.Compact;

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    // ---- Build ----

    protected void Rebuild()
    {
        WidthClass = Rd.ClassFor(WindowWidth);
        if (Model.Tree is null) { Model.ShowPage("Overview"); return; }   // the first launch lives on Overview
        bool searchFocused = _search?.IsKeyboardFocusWithin == true;
        int caret = _search?.CaretIndex ?? 0;

        double pad = WidthClass.PagePadding();
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (!CatalogReady)
        {
            // CatalogGate: a loading state over the page until the catalog is ready, never "nothing found".
            _shown = [];
            var loading = ListKit.LoadingState(LoadingTitle, "Working from the scan you already ran — nothing is read from disk again.");
            Grid.SetRowSpan(loading, 3);
            _root.Children.Add(loading);
            return;
        }

        var (rows, totalCount, totalBytes, nothingAtAll) = Compute();
        _shown = rows.Take(CleanDetail.RowLimit).ToList();
        if (_selectedId is { } sel && !_shown.Any(r => r.Id == sel)) _selectedId = null;

        var top = new StackPanel { Margin = new Thickness(pad, Compact ? 14 : 20, pad, 10) };
        top.Children.Add(Header(totalCount, totalBytes));
        var filters = FilterRow();
        filters.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(filters);
        var search = SearchRow(rows.Count);
        search.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(search);
        _root.Children.Add(top);

        FrameworkElement body;
        if (rows.Count == 0)
        {
            var (title, message) = EmptyText(nothingAtAll);
            body = ListKit.EmptyState(Icons.Check, title, message);
        }
        else body = Table(rows, pad);
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        if (ListKit.SelectionToolbar(Title) is { } bar)
        {
            Grid.SetRow(bar, 2);
            _root.Children.Add(bar);
        }

        if (searchFocused && _search is not null)
        {
            _search.Focus();
            _search.CaretIndex = Math.Min(caret, _search.Text.Length);
        }
    }

    /// <summary>"‹ Clean up", the title, "5 · 22.9 GB" and the grey blurb on one baseline.</summary>
    private FrameworkElement Header(int totalCount, long totalBytes)
    {
        var back = new StackPanel { Orientation = Orientation.Horizontal, Background = System.Windows.Media.Brushes.Transparent, Cursor = Cursors.Hand };
        var chevron = Ui.Glyph("BsChevronLeft", 11, Ui.Brush("RdInk2"));
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.Margin = new Thickness(0, 0, 4, 0);
        back.Children.Add(chevron);
        back.Children.Add(Rd.Text("Clean up", 13, null, "RdInk2"));
        back.ToolTip = "Back to Clean up";
        back.MouseLeftButtonUp += (_, e) => { Model.ShowPage("Safe to Review"); e.Handled = true; };
        System.Windows.Automation.AutomationProperties.SetName(back, "Back to Clean up");

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Rd.BaselineRow(new Border(), 26, back, 13, 0));
        var title = Rd.PageTitle(Title);
        var titleRow = Rd.BaselineRow(title, 26, Rd.Figure(CleanDetail.HeaderFigure(totalCount, totalBytes), 12), 12, 12);
        titleRow.Margin = new Thickness(12, 0, 0, 0);
        row.Children.Add(titleRow);
        if (!Compact)
        {
            var subtitle = Rd.Text(Subtitle, 12, null, "RdInk3");
            subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(Rd.BaselineRow(new Border(), 26, subtitle, 12, 12));
        }
        return row;
    }

    /// <summary>Caches | Old downloads | Large media.</summary>
    public static Border Switch(string current) => Rd.SegmentSwitch(
    [
        new Rd.Segment("Caches", current == "Caches", () => Model.ShowPage("Caches")),
        new Rd.Segment("Old downloads", current == "Old Downloads", () => Model.ShowPage("Old Downloads")),
        new Rd.Segment("Large media", current == "Large Media", () => Model.ShowPage("Large Media")),
    ]);

    private FrameworkElement FilterRow()
    {
        var dock = new DockPanel();
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        foreach (var t in FilterTrailing())
        {
            t.Margin = new Thickness(8, 0, 0, 0);
            trailing.Children.Add(t);
        }
        DockPanel.SetDock(trailing, Dock.Right);
        dock.Children.Add(trailing);
        var left = new WrapPanel();
        var segments = Switch(PageKey);
        segments.Margin = new Thickness(0, 0, 12, 0);
        left.Children.Add(segments);
        foreach (var chip in Chips())
        {
            chip.Margin = new Thickness(0, 0, 8, 0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(chip);
        }
        dock.Children.Add(left);
        return dock;
    }

    private FrameworkElement SearchRow(int matches)
    {
        var field = ListKit.SearchField(Query, [], Query.Length == 0 ? null : matches, SearchPlaceholder, "Search the list",
            text => { Query = text; DebouncedRebuild(); }, out var input);
        _search = input;
        return BiggestFoldersExplorePage.FieldRow(field, Compact ? null : 520, 12, [.. SearchTrailing()]);
    }

    private FrameworkElement Table(List<CleanDetailRow> rows, double pad)
    {
        var columns = ListKit.Columns.For(WidthClass);
        var staged = StagedSet();
        long largest = Math.Max(1, _shown.Count == 0 ? 1 : _shown.Max(r => r.Bytes));

        var grid = new Grid { Margin = new Thickness(pad, 0, pad, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(ListKit.ColumnHeader(columns, KindTitle, "Size", false, ChangedTitle, NoteTitle));

        var list = new StackPanel();
        foreach (var r in _shown)
        {
            var row = r;
            bool canAdd = row.Requests.Count > 0;
            var element = ListKit.FileRow(row.Name, row.Place, row.Badge, row.Kind, (double)row.Bytes / largest, row.Bytes,
                row.Changed, new ListKit.Note(ListKit.NoteKind.Plain, row.Note), row.Accent,
                CleanDetail.InBasket(row, p => staged.Contains(Normalize(p))),
                row.Id == _selectedId || row.NodeId is { } n && Model.MultiSelection.Contains(n), columns,
                select: () => SelectRow(row),
                add: canAdd ? () => Add([row]) : null);
            element.ToolTip = row.Path;
            element.ContextMenu = RowMenu(row);
            list.Children.Add(element);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var footer = Footer(rows);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        return grid;
    }

    private ContextMenu RowMenu(CleanDetailRow row)
    {
        var menu = new ContextMenu();
        if (row.Requests.Count > 0)
        {
            var add = new MenuItem { Header = "Add to basket" };
            add.Click += (_, _) => Add([row]);
            menu.Items.Add(add);
        }
        var reveal = new MenuItem { Header = "Show in File Explorer" };
        reveal.Click += (_, _) => Explorer.Reveal(row.Path);
        menu.Items.Add(reveal);
        return menu;
    }

    /// <summary>"5 shown · 22.9 GB" and the page's bulk add.</summary>
    private FrameworkElement Footer(List<CleanDetailRow> rows)
    {
        var bulk = CleanDetail.Bulk(rows, BulkFilter);
        var text = ListKit.FooterText((CleanDetail.FooterLine(rows), "RdInk2"));
        if (bulk.Count == 0) return ListKit.FooterBar(text);
        long bytes = bulk.Sum(b => b.Bytes);
        return ListKit.FooterBar(text, Rd.SecondaryButton(BulkTitle(bulk.Count, bytes), () => Add(bulk)));
    }

    // ---- Actions ----

    private static string Normalize(string path)
    {
        try { return System.IO.Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception) { return path.TrimEnd('\\'); }
    }

    private static HashSet<string> StagedSet() =>
        Model.StagedItems.Select(s => Normalize(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>One row goes through the single-item checks; several through the confirm sheet.</summary>
    private void Add(IReadOnlyList<CleanDetailRow> picked)
    {
        var requests = picked.SelectMany(p => p.Requests).ToList();
        if (requests.Count == 0) return;
        if (requests.Count == 1) Model.Stage(requests[0].NodeId, requests[0].Reason);
        else Model.ConfirmStageRequests(requests.Select(r => (r.NodeId, r.Reason)).ToList(), Title);
    }

    private void SelectRow(CleanDetailRow row)
    {
        _selectedId = row.Id;
        if (row.NodeId is { } id)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Model.ToggleMulti(id);
            else { Model.ClearMulti(); Model.Select(id); }
        }
        Rebuild();
    }

    /// <summary>Typing refilters after a short pause, not on every key.</summary>
    private void DebouncedRebuild()
    {
        _debounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
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

    /// <summary>Up/Down or J/K, Delete adds, Enter shows in File Explorer, Ctrl+A selects all, Esc clears (Mac: listKeyboard).</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or System.Windows.Controls.Primitives.ButtonBase || _shown.Count == 0) return;
        int index = _selectedId is { } s ? _shown.FindIndex(r => r.Id == s) : -1;
        bool plain = Keyboard.Modifiers == ModifierKeys.None;
        switch (e.Key)
        {
            case Key.Down or Key.J when plain:
                Move(_shown[Math.Min(_shown.Count - 1, index + 1)]);
                break;
            case Key.Up or Key.K when plain:
                Move(_shown[Math.Max(0, index - 1)]);
                break;
            case Key.Delete when index >= 0:
                if (_shown[index].Requests.Count > 0) Add([_shown[index]]);
                break;
            case Key.Enter when index >= 0:
                Explorer.Reveal(_shown[index].Path);
                break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                Model.ClearMulti();
                foreach (var r in _shown) if (r.NodeId is { } id) Model.ToggleMulti(id);
                Rebuild();
                break;
            case Key.Escape:
                Model.ClearMulti();
                Rebuild();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Move(CleanDetailRow row)
    {
        _selectedId = row.Id;
        if (row.NodeId is { } id) Model.Select(id);
        Rebuild();
    }
}

/// <summary>Clean up → Caches: cache folders by the app that owns them (Mac: CachesReviewView).</summary>
public sealed class CleanCachesPage : CleanDetailPage
{
    private bool _onlySafe;

    protected override string Title => "Caches";
    protected override string Subtitle => "Temporary app data. Apps rebuild it, sometimes slower the first time.";
    protected override string PageKey => "Caches";
    protected override string KindTitle => "Suggestion";
    protected override string ChangedTitle => "Where";
    protected override string NoteTitle => "If you remove it";
    protected override string SearchPlaceholder => "Search caches by app or folder";
    protected override string LoadingTitle => "Finding caches…";
    protected override bool CatalogReady => Model.Reviewables is not null;

    /// <summary>One row per app: the APP tile, its first folder's place, Rebuilds or Look first, how many places.</summary>
    public static List<CleanDetailRow> Rows(IEnumerable<ReviewableTarget> reviewables, string? root) =>
        CleanDetail.Caches(reviewables).Select(t => new CleanDetailRow(
            t.Id, t.NodeIds.Count > 0 ? t.NodeIds[0] : null, t.DisplayName, t.PrimaryPath,
            CleanUpPage.Place(t.PrimaryPath, root), "APP", CleanDetail.CacheSuggestion(t.IsGenerallySafe), t.Bytes,
            CleanDetail.Places(t.Paths.Count), t.Consequence, t.IsGenerallySafe,
            CleanDetail.CacheRequests(t), t.Paths)).ToList();

    private List<CleanDetailRow> All() => Rows(Model.Reviewables ?? [], Model.RootPath);

    protected override (List<CleanDetailRow> Rows, int TotalCount, long TotalBytes, bool NothingAtAll) Compute()
    {
        var all = All();
        return (CleanDetail.FilterCaches(all, Query, _onlySafe), all.Count, all.Sum(r => r.Bytes), all.Count == 0);
    }

    protected override (string Title, string Message) EmptyText(bool nothingAtAll) => nothingAtAll
        ? ("No caches worth clearing", "Nothing here is big enough to bother with.")
        : ("No caches match", "Clear the search or show every cache.");

    protected override string BulkTitle(int count, long bytes) => CleanDetail.CachesBulkTitle(count, bytes);
    protected override bool BulkFilter(CleanDetailRow row) => row.Accent;

    protected override IEnumerable<FrameworkElement> Chips()
    {
        yield return ListKit.Chip("Rebuild by themselves", All().Count(r => r.Accent), _onlySafe,
            () => { _onlySafe = !_onlySafe; Rebuild(); });
    }
}

/// <summary>Clean up → Old downloads: large or older files in Downloads (Mac: OldDownloadsView).</summary>
public sealed class CleanDownloadsPage : CleanDetailPage
{
    private OldDownloadsAgeFilter _age = OldDownloadsAgeFilter.Days30;
    private OldDownloadsSizeFilter _size = OldDownloadsSizeFilter.Any;
    private OldDownloadsTypeFilter _type = OldDownloadsTypeFilter.All;
    private OldDownloadsSort _sort = OldDownloadsSort.Largest;

    protected override string Title => "Old downloads";
    protected override string Subtitle => "Large or older files in Downloads. Age is the last-modified date.";
    protected override string PageKey => "Old Downloads";
    protected override string KindTitle => "Suggestion";
    protected override string ChangedTitle => "Last changed";
    protected override string NoteTitle => "Why it's here";
    protected override string SearchPlaceholder => "Search Downloads";
    protected override string LoadingTitle => "Looking through Downloads…";
    protected override bool CatalogReady => Model.OldDownloads is not null;

    private OldDownloadsCatalogResult Catalog => Model.OldDownloads ?? OldDownloadsCatalogResult.Empty;

    /// <summary>One download: its type tile, Likely done with or Look first, when it last changed, why it's here.</summary>
    public static CleanDetailRow Row(OldDownloadsCandidate c, string? root, int today) => new(
        c.NodeID.ToString(System.Globalization.CultureInfo.InvariantCulture), c.NodeID >= 0 ? c.NodeID : null,
        c.Name, c.AbsolutePath, CleanUpPage.Place(c.AbsolutePath, root), ListKit.Badge(c.Name, c.Kind),
        CleanDetail.DownloadSuggestion(c.Status), c.Bytes, ExplorePage.LastUsed(c.ModifiedDay, today), c.WhyHere,
        c.Status == OldDownloadsStatus.LikelyDisposable, CleanDetail.DownloadRequests(c), [c.AbsolutePath]);

    protected override (List<CleanDetailRow> Rows, int TotalCount, long TotalBytes, bool NothingAtAll) Compute()
    {
        var catalog = Catalog;
        int today = AgeMap.Today();
        var filtered = OldDownloadsCatalog.Sorted(OldDownloadsCatalog.Filter(catalog.Candidates, _age, _size, _type, Query), _sort);
        return (filtered.Select(c => Row(c, Model.RootPath, today)).ToList(),
            catalog.Summary.TotalCount, catalog.Summary.TotalBytes, catalog.Candidates.Count == 0);
    }

    protected override (string Title, string Message) EmptyText(bool nothingAtAll) => nothingAtAll
        ? ("Nothing old enough to review", "Downloads has nothing matching this view. That’s a good thing.")
        : ("No files match these filters", "Try another age, size or type.");

    protected override string BulkTitle(int count, long bytes) => CleanDetail.DownloadsBulkTitle(count, bytes);
    protected override bool BulkFilter(CleanDetailRow row) => true;

    protected override IEnumerable<FrameworkElement> Chips() =>
        CleanDetail.DownloadTypeChips(Catalog.Candidates, _age, _size, Compact)
            .Select(chip => (FrameworkElement)ListKit.Chip(chip.Filter.Title(), chip.Count, _type == chip.Filter,
                () => { _type = chip.Filter; Rebuild(); }));

    protected override IEnumerable<FrameworkElement> FilterTrailing()
    {
        yield return ListKit.Dropdown("Older than", Enum.GetValues<OldDownloadsAgeFilter>(), _age, a => a.Title(),
            a => { _age = a; Rebuild(); });
    }

    protected override IEnumerable<FrameworkElement> SearchTrailing()
    {
        if (!Compact)
            yield return ListKit.Dropdown("Size", Enum.GetValues<OldDownloadsSizeFilter>(), _size, s => s.Title(),
                s => { _size = s; Rebuild(); });
        yield return ListKit.Dropdown("Sort", Enum.GetValues<OldDownloadsSort>(), _sort, s => s.Title(),
            s => { _sort = s; Rebuild(); });
        yield return Rd.SecondaryButton("Open Downloads", OpenDownloads);
    }

    /// <summary>The account's Downloads folder, opened in File Explorer (Mac: ~/Downloads in Finder).</summary>
    private static string DownloadsFolder() => System.IO.Path.Combine(UserFolders.Home, "Downloads");

    private static void OpenDownloads()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{DownloadsFolder()}\""); }
        catch (Exception) { /* explorer missing is not fatal */ }
    }
}

/// <summary>Clean up → Large media: the videos, photos, audio and media projects using the most space (Mac: LargeMediaView).</summary>
public sealed class CleanMediaPage : CleanDetailPage
{
    private MediaTypeFilter _type = MediaTypeFilter.All;
    private MediaSizeFilter _size = MediaSizeFilter.Any;
    private MediaAgeFilter _age = MediaAgeFilter.All;
    private MediaLocationFilter _location = MediaLocationFilter.Any;
    private MediaSort _sort = MediaSort.Largest;

    protected override string Title => "Large media";
    protected override string Subtitle => "The videos, photos, audio and media projects using the most space.";
    protected override string PageKey => "Large Media";
    protected override string KindTitle => "Kind";
    protected override string ChangedTitle => "Last changed";
    protected override string NoteTitle => "Before you remove it";
    protected override string SearchPlaceholder => "Search media by name or folder";
    protected override string LoadingTitle => "Finding large media…";
    protected override bool CatalogReady => Model.LargeMedia is not null;

    private MediaCatalogResult Catalog => Model.LargeMedia ?? MediaCatalogResult.Empty;

    /// <summary>One media item: DIR for a library or project folder, else its type; the kind, age and advice.</summary>
    public static CleanDetailRow Row(MediaCandidate c, string? root, int today) => new(
        c.NodeID.ToString(System.Globalization.CultureInfo.InvariantCulture), c.NodeID >= 0 ? c.NodeID : null,
        c.Name, c.AbsolutePath, CleanUpPage.Place(c.AbsolutePath, root),
        c.IsDirectory ? "DIR" : ListKit.Badge(c.Name, FileKind.Other),
        CleanDetail.MediaKindText(c.Kind), c.Bytes, ExplorePage.LastUsed(c.ModifiedDay, today), CleanDetail.MediaNote(c),
        c.Status == MediaStatus.LikelyDisposable, CleanDetail.MediaRequests(c), [c.AbsolutePath]);

    protected override (List<CleanDetailRow> Rows, int TotalCount, long TotalBytes, bool NothingAtAll) Compute()
    {
        var catalog = Catalog;
        int today = AgeMap.Today();
        var filtered = MediaCatalog.Sorted(MediaCatalog.Filter(catalog.Candidates, _type, _size, _age, _location, Query), _sort);
        return (filtered.Select(c => Row(c, Model.RootPath, today)).ToList(),
            catalog.Candidates.Count, catalog.Candidates.Sum(c => c.Bytes), catalog.Candidates.Count == 0);
    }

    protected override (string Title, string Message) EmptyText(bool nothingAtAll) => nothingAtAll
        ? ("No large media found", "Nothing here is big enough to bother with.")
        : ("No media matches these filters", "Try another kind, place or size.");

    protected override string BulkTitle(int count, long bytes) => CleanDetail.MediaBulkTitle(count, bytes);
    protected override bool BulkFilter(CleanDetailRow row) => row.Accent;

    protected override IEnumerable<FrameworkElement> Chips() =>
        CleanDetail.MediaTypeChips(Catalog.Candidates, _size, _age, _location, Compact)
            .Select(chip => (FrameworkElement)ListKit.Chip(chip.Filter.Title(), chip.Count, _type == chip.Filter,
                () => { _type = chip.Filter; Rebuild(); }));

    protected override IEnumerable<FrameworkElement> FilterTrailing()
    {
        yield return ListKit.Dropdown("Where", Enum.GetValues<MediaLocationFilter>(), _location, l => l.Title(),
            l => { _location = l; Rebuild(); });
    }

    protected override IEnumerable<FrameworkElement> SearchTrailing()
    {
        if (!Compact)
        {
            yield return ListKit.Dropdown("Older than", Enum.GetValues<MediaAgeFilter>(), _age, a => a.Title(),
                a => { _age = a; Rebuild(); });
            yield return ListKit.Dropdown("Size", Enum.GetValues<MediaSizeFilter>(), _size, s => s.Title(),
                s => { _size = s; Rebuild(); });
        }
        yield return ListKit.Dropdown("Sort", Enum.GetValues<MediaSort>(), _sort, s => s.Title(),
            s => { _sort = s; Rebuild(); });
    }
}
