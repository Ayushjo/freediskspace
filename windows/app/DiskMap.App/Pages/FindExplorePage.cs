using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-107: Find (Mac: FindView, TASK-060), from Ctrl+K or "Show all results in Find": one list
/// over the whole scan, narrowed by chips and the query language. A chip is only a shortcut
/// for a query token, so the box always shows exactly what is being asked. One bare word runs
/// through the fast name search (<see cref="FileSearch"/>); anything else through
/// <see cref="FileQuery"/>. Keeps what Windows Find had: Smallest and Name orders, saved
/// searches with their live totals, removing one.
/// </summary>
public sealed class FindExplorePage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    /// <summary>The Mac's three orders, then Windows Find's Smallest and Name.</summary>
    public enum FindSort { Largest, Oldest, Newest, Smallest, Name }

    public static string SortTitle(FindSort sort) => sort switch
    {
        FindSort.Oldest => "Oldest",
        FindSort.Newest => "Recently changed",
        FindSort.Smallest => "Smallest",
        FindSort.Name => "Name",
        _ => "Largest",
    };

    /// <summary>The engine's order for a menu choice (Smallest and Name re-sort the largest matches).</summary>
    public static FileQuery.Sort EngineSort(FindSort sort) => sort switch
    {
        FindSort.Oldest => FileQuery.Sort.Oldest,
        FindSort.Newest => FileQuery.Sort.Newest,
        _ => FileQuery.Sort.Largest,
    };

    public sealed record QueryChip(string Id, string Title, string Token);

    public static readonly QueryChip[] Chips =
    [
        new("large", "Large", "size>500MB"),
        new("old", "Old", "age>1y"),
        new("duplicated", "Duplicated", "is:duplicate"),
        new("cached", "Cached", "in:caches"),
        new("media", "Media", "kind:media"),
        new("downloads", "Downloads", "in:downloads"),
        new("folders", "Folders", "type:folder"),
    ];

    /// <summary>The Mac's examples; "in:library" is AppData here.</summary>
    public static readonly (string Query, string Meaning)[] Examples =
    [
        ("ext:mp4,mov size>500MB", "Videos over 500 MB"),
        ("in:downloads age>6m", "Downloads untouched for six months"),
        ("name:*.log size>50MB", "Log files over 50 MB"),
        ("type:folder node_modules", "Every node_modules folder"),
        ("kind:archive in:appdata", "Archives inside AppData"),
        ("is:hardlink", "Files with more than one hard link"),
    ];

    public static readonly (string Key, string Detail)[] KeyReference =
    [
        ("ext:  name:", "extension or name pattern"),
        ("kind:", "video audio image media document developer archive"),
        ("size>  size<", "500MB, 2GB"),
        ("age>  age<", "30d, 6m, 1y"),
        ("in:", "downloads desktop documents appdata caches"),
        ("is:", "duplicate hardlink cloud"),
        ("type:", "file folder any"),
        ("word  -word", "name contains / excludes"),
    ];

    public sealed record Row(int Id, string Name, string AbsolutePath, FileKind Kind, long Bytes, int ModifiedDay, bool IsDirectory);

    /// <summary>The sort outlives the page (Mac: model.findSort).</summary>
    private static FindSort _sort = FindSort.Largest;

    private string _text = Model.SearchQuery;
    private string _syncedQuery = Model.SearchQuery;
    private List<Row> _rows = [];
    private FileTree? _rowsTree;
    private int _matchCount;
    /// <summary>Null when the name search answered and the bytes can't be summed honestly.</summary>
    private long? _matchedBytes = 0;
    private List<string> _notes = [];
    private bool _running;
    private string? _runKey;
    private int _runVersion;
    private int? _selected;
    private readonly ScrollMemory _scroll = new();
    private Dictionary<Guid, SavedSearches.Total> _savedTotals = [];
    private string? _savedKey;

    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private readonly Grid _root = new();
    private TextBox? _search;

    public FindExplorePage()
    {
        Background = Ui.Brush("RdPage");
        Content = _root;
        Focusable = true;
        ModelEvents.WhileLoaded(this, Rebuild);
        Loaded += (_, _) =>
        {
            Model.StagedChanged += OnStaged;
            // The field has focus when Find opens (Mac: onAppear { searchFocused = true }).
            Dispatcher.InvokeAsync(() => { _search?.Focus(); if (_search is not null) _search.CaretIndex = _search.Text.Length; },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
        Unloaded += (_, _) => Model.StagedChanged -= OnStaged;
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != _class) Rebuild(); };
        PreviewKeyDown += OnKey;
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    private string Trimmed => Model.SearchQuery.Trim();

    // ---- Words (pure) ----

    /// <summary>Tokens the query understood, shown as violet tags in the field.</summary>
    public static List<string> Tokens(string query) =>
        query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Contains(':') || t.Contains('>') || t.Contains('<')).ToList();

    /// <summary>
    /// The name search answers a single bare word ranked by size; everything else needs
    /// <see cref="FileQuery"/>. Returns the word and whether only files or folders count.
    /// </summary>
    public static (string Word, FileQuery.NodeType? Type)? IndexNeedle(FileQuery query, FindSort sort)
    {
        if (sort != FindSort.Largest || query.Words.Count != 1) return null;
        var plain = new FileQuery();
        plain.Words.AddRange(query.Words);
        plain.ExplicitType = query.ExplicitType;
        if (!query.Equals(plain)) return null;
        return (query.Words[0], query.ExplicitType);
    }

    /// <summary>"N matches · 12 GB", or "Searching…" before the first answer.</summary>
    public static List<string> SummaryParts(bool running, int rows, int matchCount, long? matchedBytes)
    {
        if (running && rows == 0) return ["Searching…"];
        var parts = new List<string> { $"{matchCount:N0} match{(matchCount == 1 ? "" : "es")}" };
        if (matchedBytes is { } bytes && matchCount > 0) parts.Add(ShellFormat.HeroBytes(bytes));
        return parts;
    }

    public static string FooterLine(int rows, int matchCount, long? matchedBytes) =>
        matchCount > rows
            ? $"Showing the first {rows:N0} of {matchCount:N0} — narrow the query to see the rest."
            : $"{rows:N0} shown" + (matchedBytes is { } b ? $" · {ShellFormat.HeroBytes(b)}" : "");

    // ---- Running ----

    private static Row MakeRow(int id, FileTree tree, string root, long[] totals)
    {
        string path = tree.PathOf(id, root);
        string name = tree.NameOf(id);
        return new Row(id, name, path, FileKinds.Classify(name, path), id < totals.Length ? totals[id] : 0,
            tree.ModifiedDay[id], tree.IsDirectory[id]);
    }

    private async void Run(FileTree tree, string root, string key)
    {
        _runKey = key;
        // Rows name nodes of the tree they came from: after a rescan they'd stage other files.
        if (!ReferenceEquals(tree, _rowsTree)) { _rows = []; _selected = null; _rowsTree = tree; }
        string text = Trimmed;
        int version = ++_runVersion;
        if (text.Length == 0)
        {
            _rows = []; _matchCount = 0; _matchedBytes = 0; _notes = []; _running = false;
            return;
        }
        _running = true;
        var totals = Model.Totals;
        var sort = _sort;
        var duplicates = Model.DuplicateFileIDs;
        var parsed = FileQuery.Parse(text, UserFolders.Home, root);
        var result = await Task.Run(() =>
        {
            if (IndexNeedle(parsed.Query, sort) is { } needle)
            {
                var (ids, total) = FileSearch.SearchCounted(tree, totals, needle.Word, limit: 500,
                    filesOnly: needle.Type == FileQuery.NodeType.Files, foldersOnly: needle.Type == FileQuery.NodeType.Folders);
                var found = ids.Select(id => MakeRow(id, tree, root, totals)).ToList();
                // A folder's size already holds its matching children, so only a complete,
                // files-only answer can be summed.
                long? bytes = total == found.Count && !found.Any(r => r.IsDirectory) ? found.Sum(r => r.Bytes) : null;
                return (Rows: found, Count: total, Bytes: bytes, Notes: new List<string>());
            }
            var run = parsed.Query.Run(tree, root, totals, new FileQuery.Context(DuplicateFileIDs: duplicates),
                EngineSort(sort), limit: 500, isCancelled: () => version != _runVersion);
            var rows = run.Ids.Select(id => MakeRow(id, tree, root, totals));
            rows = sort switch
            {
                FindSort.Smallest => rows.OrderBy(r => r.Bytes),
                FindSort.Name => rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => rows,
            };
            return (Rows: rows.ToList(), Count: run.MatchCount, Bytes: (long?)run.MatchedBytes, Notes: run.Notes);
        });
        if (version != _runVersion) return;
        _rows = result.Rows;
        _matchCount = result.Count;
        _matchedBytes = result.Bytes;
        _notes = result.Notes;
        _running = false;
        Rebuild();
    }

    private async void LoadSavedTotals(FileTree tree, string root, List<SavedSearch> saved, string key)
    {
        _savedKey = key;
        var totals = Model.Totals;
        var duplicates = Model.DuplicateFileIDs;
        var computed = await Task.Run(() =>
            SavedSearches.Totals(saved, tree, root, totals, new FileQuery.Context(DuplicateFileIDs: duplicates)));
        if (_savedKey != key) return;
        _savedTotals = computed;
        Rebuild();
    }

    // ---- Build ----

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        if (Model.Tree is not { } tree || Model.RootPath is not { } root) { Model.ShowPage("Overview"); return; }
        if (Model.SearchQuery != _syncedQuery)
        {
            // Set from elsewhere (Ctrl+K, Overview): the box follows.
            _syncedQuery = Model.SearchQuery;
            _text = Model.SearchQuery;
        }
        string key = $"{Model.SearchQuery}|{_sort}|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tree)}|"
            + $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Model.Totals)}|{Model.DuplicatesSearched}|{Model.Duplicates.Count}";
        if (key != _runKey) Run(tree, root, key);
        bool searchFocused = _search?.IsKeyboardFocusWithin == true;
        int caret = _search?.CaretIndex ?? 0;

        double pad = _class.PagePadding();
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // One layout whether or not there's a query: switching layouts on the first keystroke
        // rebuilt the field and dropped focus after one letter (Mac note, same here).
        var top = new StackPanel { Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, 10) };
        top.Children.Add(Header());
        var searchRow = SearchRow();
        searchRow.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(searchRow);
        var chips = ChipRow();
        chips.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(chips);
        if (Trimmed.Length > 0)
        {
            var meaning = Meaning(root);
            meaning.Margin = new Thickness(0, 12, 0, 0);
            top.Children.Add(meaning);
        }
        _root.Children.Add(top);

        FrameworkElement body;
        if (Trimmed.Length == 0) body = StartState(tree, root, pad);
        else if (_rows.Count == 0 && !_running)
            body = ListKit.EmptyState(Icons.Search, "Nothing matches", "Remove a chip or loosen a size or age.");
        else body = Table(tree, root, pad);
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        if (ListKit.SelectionToolbar("Find") is { } bar)
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

    private FrameworkElement Header()
    {
        var dock = new DockPanel();
        var views = ExplorePage.ViewSwitch(onList: true);
        views.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(views, Dock.Right);
        dock.Children.Add(views);
        var title = Rd.PageTitle("Find");
        if (Trimmed.Length > 0)
        {
            var stats = Rd.Figure(string.Join(" · ", SummaryParts(_running, _rows.Count, _matchCount, _matchedBytes)), 12);
            dock.Children.Add(Rd.BaselineRow(title, 26, stats, 12, 12));
        }
        else if (_class != Rd.WidthClass.Compact)
        {
            var lede = Rd.Text("Type a name, or narrow by size, age, kind and place.", 12, null, "RdInk3");
            dock.Children.Add(Rd.BaselineRow(title, 26, lede, 12, 12));
        }
        else dock.Children.Add(title);
        return dock;
    }

    private FrameworkElement SearchRow()
    {
        var field = ListKit.SearchField(_text, Tokens(_text), _text.Trim().Length == 0 ? null : _matchCount,
            "Name, or ext:mp4 size>500MB age>1y in:downloads", "Find query",
            text => { _text = text; DebouncedApply(); }, out var input);
        _search = input;
        var sort = ListKit.Dropdown("Sort", Enum.GetValues<FindSort>(), _sort, SortTitle, s => { _sort = s; Rebuild(); });
        var save = Rd.SecondaryButton("Save…", OpenSave);
        save.IsEnabled = Trimmed.Length > 0;
        save.Opacity = save.IsEnabled ? 1 : 0.4;
        save.ToolTip = "Keep this search in the sidebar, with its size kept up to date (Ctrl+S)";
        return BiggestFoldersExplorePage.FieldRow(field, null, 10, sort, save);
    }

    private FrameworkElement ChipRow()
    {
        var row = new WrapPanel();
        foreach (var chip in Chips.Take(_class == Rd.WidthClass.Compact ? 5 : 7))
        {
            bool on = FileQuery.ContainsToken(chip.Token, Model.SearchQuery);
            var c = ListKit.Chip(chip.Title, null, on, () => SetQuery(FileQuery.Toggling(chip.Token, Model.SearchQuery)));
            c.ToolTip = chip.Token;
            c.Margin = new Thickness(0, 0, 8, 0);
            System.Windows.Automation.AutomationProperties.SetName(c, $"{chip.Title}, adds {chip.Token}");
            row.Children.Add(c);
        }
        return row;
    }

    /// <summary>What the query means, in words, plus anything it could not use.</summary>
    private FrameworkElement Meaning(string root)
    {
        var parsed = FileQuery.Parse(Model.SearchQuery, UserFolders.Home, root);
        var stack = new StackPanel();
        var describe = Rd.Figure(string.Join("  ·  ", parsed.Query.Describe()), 11, null, "RdInk3");
        describe.TextWrapping = TextWrapping.Wrap;
        describe.MaxHeight = Ui.Scaled(11) * 1.4 * 2;
        stack.Children.Add(describe);
        foreach (var problem in parsed.Problems)
        {
            var t = Rd.Text($"Ignoring {problem.Token} — {problem.Message}", 12, null, "AppWarning");
            t.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(t);
        }
        foreach (string note in _notes)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            line.Children.Add(Rd.Text(note, 12, null, "AppWarning"));
            if (note.StartsWith("Duplicates", StringComparison.Ordinal))
            {
                var link = Rd.Link("Find duplicates", () => Model.ShowPage("Duplicates"), 12, "RdAccentDeep", FontWeights.Normal);
                link.Margin = new Thickness(8, 0, 0, 0);
                line.Children.Add(link);
            }
            stack.Children.Add(line);
        }
        return stack;
    }

    // ---- Start state ----

    private FrameworkElement StartState(FileTree tree, string root, double pad)
    {
        var tryCard = StartCard("Try", ExampleRows());
        var right = new StackPanel();
        right.Children.Add(StartCard("Keys", KeyGrid()));
        var saved = SavedSearches.Load();
        var keep = StartCard("Keep one in the sidebar", StarterRows(saved));
        keep.Margin = new Thickness(0, 16, 0, 0);
        right.Children.Add(keep);
        if (saved.Count > 0)
        {
            // Windows Find's saved searches, with their live totals over this scan.
            string key = $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Model.Totals)}|"
                + string.Join("\n", saved.Select(s => s.Id + s.Query + s.Sort));
            if (key != _savedKey) LoadSavedTotals(tree, root, saved, key);
            var mine = StartCard("Saved searches", SavedRows(saved));
            mine.Margin = new Thickness(0, 16, 0, 0);
            right.Children.Add(mine);
        }

        FrameworkElement layout;
        if (_class == Rd.WidthClass.Compact)
        {
            var stack = new StackPanel();
            stack.Children.Add(tryCard);
            right.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(right);
            layout = stack;
        }
        else
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tryCard.VerticalAlignment = VerticalAlignment.Top;
            right.VerticalAlignment = VerticalAlignment.Top;
            grid.Children.Add(tryCard);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            layout = grid;
        }
        layout.Margin = new Thickness(pad, 0, pad, pad);
        return new ScrollViewer { Content = layout, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    /// <summary>A start card: a 44-tall titled header over its rows, card fill, 1 px line, radius 12.</summary>
    private static Border StartCard(string title, UIElement content)
    {
        var stack = new StackPanel();
        stack.Children.Add(Rd.CardHeader(title));
        stack.Children.Add(content);
        return Rd.Card(stack);
    }

    private static Border Hairline() => new() { Height = 1, Background = Ui.Brush("RdLine") };

    private StackPanel ExampleRows()
    {
        var rows = new StackPanel();
        for (int i = 0; i < Examples.Length; i++)
        {
            var (query, meaning) = Examples[i];
            if (i > 0) rows.Children.Add(Hairline());
            rows.Children.Add(ExampleRow(query, meaning));
        }
        return rows;
    }

    /// <summary>An example query: mono query (230 wide), what it means, an arrow on hover.</summary>
    private Border ExampleRow(string query, string meaning)
    {
        var dock = new DockPanel { Margin = new Thickness(16, 0, 16, 0) };
        var arrow = Ui.Glyph("BsArrowRight", 10, Brushes.Transparent);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(arrow, Dock.Right);
        dock.Children.Add(arrow);
        var q = Rd.Figure(query, 12);
        q.Width = 230;
        DockPanel.SetDock(q, Dock.Left);
        dock.Children.Add(q);
        var m = Rd.Text(meaning, 12, null, "RdInk2");
        m.Margin = new Thickness(16, 0, 0, 0);
        dock.Children.Add(m);
        var host = new Border { Height = 46, Background = Brushes.Transparent, Child = dock, Cursor = Cursors.Hand, Focusable = true };
        host.MouseEnter += (_, _) => { host.Background = Rd.WithOpacity(Ui.Brush("RdSoft"), 0.6); Ui.SetIconBrush(arrow, Ui.Brush("AppAccent")); };
        host.MouseLeave += (_, _) => { host.Background = Brushes.Transparent; Ui.SetIconBrush(arrow, Brushes.Transparent); };
        host.MouseLeftButtonUp += (_, _) => SetQuery(query);
        host.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { SetQuery(query); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(host, $"{meaning}: {query}");
        return host;
    }

    private static Grid KeyGrid()
    {
        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < KeyReference.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (key, detail) = KeyReference[i];
            var k = Rd.Figure(key, 12);
            k.Margin = new Thickness(0, i > 0 ? 8 : 0, 20, 0);
            Grid.SetRow(k, i);
            grid.Children.Add(k);
            var d = Rd.Text(detail, 12, null, "RdInk2");
            d.Margin = new Thickness(0, i > 0 ? 8 : 0, 0, 0);
            Grid.SetRow(d, i);
            Grid.SetColumn(d, 1);
            grid.Children.Add(d);
        }
        return grid;
    }

    private StackPanel StarterRows(List<SavedSearch> saved)
    {
        var rows = new StackPanel();
        for (int i = 0; i < SavedSearches.Starters.Length; i++)
        {
            var (name, query) = SavedSearches.Starters[i];
            if (i > 0) rows.Children.Add(Hairline());
            bool kept = saved.Any(s => s.Query == query);
            var keep = Rd.SecondaryButton(kept ? "Kept" : "Keep", () => Keep(name, query, FindSort.Largest));
            keep.IsEnabled = !kept;
            keep.Opacity = kept ? 0.4 : 1;
            System.Windows.Automation.AutomationProperties.SetName(keep, $"{(kept ? "Kept" : "Keep")} {name}");
            rows.Children.Add(SearchRowItem(name, query, null, keep));
        }
        return rows;
    }

    private StackPanel SavedRows(List<SavedSearch> saved)
    {
        var rows = new StackPanel();
        for (int i = 0; i < saved.Count; i++)
        {
            var search = saved[i];
            if (i > 0) rows.Children.Add(Hairline());
            string? total = _savedTotals.TryGetValue(search.Id, out var t)
                ? $"{t.Count:N0} · {ShellFormat.HeroBytes(t.Bytes)}" : null;
            var remove = Rd.IconButton(Icons.Cancel, $"Remove saved search {search.Name}", () =>
            {
                SavedSearches.Save(SavedSearches.Load().Where(s => s.Id != search.Id));
                Rebuild();
            }, 26);
            var row = SearchRowItem(search.Name, search.Query, total, remove);
            row.Cursor = Cursors.Hand;
            row.Background = Brushes.Transparent;
            row.MouseEnter += (_, _) => row.Background = Rd.WithOpacity(Ui.Brush("RdSoft"), 0.6);
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, _) =>
            {
                _sort = search.FileSort switch { FileQuery.Sort.Oldest => FindSort.Oldest, FileQuery.Sort.Newest => FindSort.Newest, _ => FindSort.Largest };
                SetQuery(search.Query);
            };
            row.ToolTip = "Run this search";
            System.Windows.Automation.AutomationProperties.SetName(row, $"Run {search.Name}: {search.Query}");
            rows.Children.Add(row);
        }
        return rows;
    }

    /// <summary>A 48-tall row: name (13 ink), query (mono 11 ink3), an optional figure, a trailing control.</summary>
    private static Border SearchRowItem(string name, string query, string? figure, FrameworkElement trailing)
    {
        var dock = new DockPanel { Margin = new Thickness(16, 0, 16, 0) };
        trailing.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(trailing, Dock.Right);
        dock.Children.Add(trailing);
        if (figure is not null)
        {
            var f = Rd.Figure(figure, 12, null, "RdInk2");
            f.Margin = new Thickness(8, 0, 12, 0);
            DockPanel.SetDock(f, Dock.Right);
            dock.Children.Add(f);
        }
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Rd.Text(name, 13));
        var q = Rd.Figure(query, 11, null, "RdInk3");
        q.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(q);
        dock.Children.Add(left);
        return new Border { Height = 48, Child = dock };
    }

    // ---- Results ----

    private FrameworkElement Table(FileTree tree, string root, double pad)
    {
        var columns = ListKit.Columns.For(_class);
        var index = new FreeableIndex(Model.Reviewables ?? [], Model.Totals);
        var staged = Model.StagedItems.Select(s => System.IO.Path.GetFullPath(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        long largest = Math.Max(1, _rows.Count == 0 ? 1 : _rows.Max(r => r.Bytes));
        int today = AgeMap.Today();
        string reason = "Find: " + Trimmed;

        var grid = new Grid { Margin = new Thickness(pad, 0, pad, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string size = _sort switch { FindSort.Largest => "Size ↓", FindSort.Smallest => "Size ↑", _ => "Size" };
        string changed = _sort is FindSort.Oldest or FindSort.Newest ? "Last changed ↓" : "Last changed";
        grid.Children.Add(ListKit.ColumnHeader(columns, "Kind", size, false, changed, "Can be freed"));

        var list = new StackPanel { Opacity = _running ? 0.6 : 1 };
        foreach (var row in _rows)
        {
            bool freeable = index.Contains(row.Id, tree);
            var verdict = StorageClassifier.Classify(row.AbsolutePath, row.IsDirectory);
            var r = row;
            var element = ListKit.FileRow(row.Name, BiggestFilesExplorePage.RelativeParent(row.AbsolutePath, root),
                row.IsDirectory ? "DIR" : ListKit.Badge(row.Name, row.Kind),
                row.IsDirectory ? BiggestFoldersExplorePage.FolderKind(verdict.Class) : ListKit.KindText(row.Kind, verdict.Class),
                (double)row.Bytes / largest, row.Bytes, ExplorePage.LastUsed(row.ModifiedDay, today),
                ListKit.FreeNote(freeable, row.Bytes, verdict), freeable, staged.Contains(row.AbsolutePath),
                row.Id == _selected || Model.MultiSelection.Contains(row.Id), columns,
                select: () => SelectRow(r.Id),
                add: () => Model.Stage(r.Id, reason),
                open: row.IsDirectory ? () => { Model.DrillTo(r.Id); Model.Select(r.Id); Model.ShowPage("Biggest Folders"); } : null);
            var showInExplore = new MenuItem { Header = "Show in Explore" };
            showInExplore.Click += (_, _) =>
            {
                Model.Select(r.Id);
                Model.DrillTo(r.IsDirectory ? r.Id : Math.Max(0, tree.Parent[r.Id]));
                Model.ShowPage("Visualize");
            };
            element.ContextMenu = new ContextMenu { Items = { showInExplore } };
            list.Children.Add(element);
        }
        var scroll = _scroll.Wrap(list, _runKey ?? "");
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var text = ListKit.FooterText((FooterLine(_rows.Count, _matchCount, _matchedBytes), "RdInk2"));
        var footer = _rows.Count > 1
            ? ListKit.FooterBar(text, Rd.SecondaryButton($"Select all {_rows.Count:N0}", SelectAll))
            : ListKit.FooterBar(text);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        return grid;
    }

    // ---- Actions ----

    private void SetQuery(string query)
    {
        _text = query;
        _syncedQuery = query;
        Model.SearchQuery = query;   // StateChanged rebuilds and runs it
        Rebuild();
    }

    private void SelectRow(int id)
    {
        Focus();   // a click gives the list the keyboard (the search box kept it, so ↑↓ and Delete did nothing)
        _selected = id;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Model.ToggleMulti(id);
        else { Model.ClearMulti(); Model.Select(id); }
        Rebuild();
    }

    private void SelectAll()
    {
        Model.ClearMulti();
        foreach (var r in _rows) Model.ToggleMulti(r.Id);
        Rebuild();
    }

    private void Keep(string name, string query, FindSort sort)
    {
        string trimmed = query.Trim();
        if (trimmed.Length == 0) return;
        var list = SavedSearches.Load();
        int at = list.FindIndex(s => s.Query == trimmed);
        string engine = EngineSort(sort).ToString();
        if (at >= 0) list[at] = list[at] with { Name = name, Sort = engine };
        else
        {
            if (list.Count >= SavedSearches.Limit) return;
            list.Add(SavedSearch.New(name, trimmed, EngineSort(sort)));
        }
        SavedSearches.Save(list);
        _savedKey = null;
        Rebuild();
    }

    /// <summary>Save to the Sidebar (Mac: SaveSearchSheet): the query, a name, Cancel / Save.</summary>
    private void OpenSave()
    {
        if (Trimmed.Length == 0 || Model.RootPath is not { } root) return;
        string query = Trimmed;
        var saved = SavedSearches.Load();
        bool full = saved.Count >= SavedSearches.Limit && saved.All(s => s.Query != query);
        var body = new StackPanel();
        body.Children.Add(Rd.Text("Save to the Sidebar", 15, FontWeights.SemiBold));
        var q = Rd.Figure(query, 12, null, "RdInk2");
        q.TextWrapping = TextWrapping.Wrap;
        q.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(q);
        var name = new TextBox
        {
            Text = SavedSearches.DefaultName(query, root), Width = 320, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(4, 3, 4, 3), FontSize = Ui.Scaled(13),
        };
        System.Windows.Automation.AutomationProperties.SetName(name, "Name");
        body.Children.Add(name);
        if (full)
        {
            var warn = Rd.Text($"The sidebar holds {SavedSearches.Limit} saved searches; remove one first.", 12, null, "AppWarning", wrap: true);
            warn.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(warn);
        }
        Window? window = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = Rd.SecondaryButton("Cancel", () => window?.Close());
        cancel.IsCancel = true;
        var save = Rd.PrimaryButton("Save", () =>
        {
            Keep(name.Text.Trim(), query, _sort);
            window?.Close();
        });
        save.IsDefault = true;
        save.Margin = new Thickness(8, 0, 0, 0);
        void refresh() { save.IsEnabled = name.Text.Trim().Length > 0 && !full; save.Opacity = save.IsEnabled ? 1 : 0.4; }
        name.TextChanged += (_, _) => refresh();
        refresh();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        body.Children.Add(buttons);
        window = Dialogs.CardWindow(body, 360);
        window.Owner = Window.GetWindow(this);
        window.WindowStartupLocation = window.Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        window.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        window.ShowDialog();
    }

    private System.Windows.Threading.DispatcherTimer? _debounce;

    /// <summary>Typing settles before the query runs (Mac: 120 ms).</summary>
    private void DebouncedApply()
    {
        _debounce ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Stop();
        _debounce.Tick -= OnDebounce;
        _debounce.Tick += OnDebounce;
        _debounce.Start();
    }

    private void OnDebounce(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        if (_text == Model.SearchQuery) return;
        _syncedQuery = _text;
        Model.SearchQuery = _text;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Ctrl+S keeps this query (Mac ⌘S), from the field too.
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnDebounce(null, EventArgs.Empty);
            if (Trimmed.Length > 0) OpenSave();
            e.Handled = true;
            return;
        }
        if (Model.Tree is null || Trimmed.Length == 0) return;
        var rows = _rows;
        var ids = rows.Select(r => r.Id).ToList();
        int? selected = _selected;
        string reason = "Find: " + Trimmed;
        if (ListKit.ListKey(e, ids, ref selected, id => Model.Stage(id, reason),
                id => rows.FirstOrDefault(r => r.Id == id)?.AbsolutePath, SelectAll))
        {
            _selected = selected;
            e.Handled = true;
            Rebuild();
        }
    }
}
