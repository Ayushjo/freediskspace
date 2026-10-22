using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-107: Explore → List → Forgotten (Mac: ForgottenFilesView): the user's own files untouched
/// for a year or more, how sure we are, and why — app and system data left out on purpose.
/// The candidates are built once per scan and size basis, off the UI thread.
/// </summary>
public sealed class ForgottenExplorePage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    public enum FilterTab { All, Likely, Worth, Excluded }

    public enum SortMode { ReviewValue, Largest, Oldest, Name }

    public static string SortTitle(SortMode mode) => mode switch
    {
        SortMode.Largest => "Largest",
        SortMode.Oldest => "Oldest",
        SortMode.Name => "Name",
        _ => "Most relevant",
    };

    /// <summary>The age dropdown's choices: any, or one bucket.</summary>
    public sealed record AgeChoice(ForgottenAgeBucket? Bucket)
    {
        public string Title => Bucket?.Title() ?? "Any age";

        public static readonly IReadOnlyList<AgeChoice> All =
            [new((ForgottenAgeBucket?)null), .. Enum.GetValues<ForgottenAgeBucket>().Select(b => new AgeChoice(b))];
    }

    private string _query = "";
    private FilterTab _tab = FilterTab.All;
    private SortMode _sort = SortMode.ReviewValue;
    private int? _selected;
    private readonly ScrollMemory _scroll = new();
    private FileTree? _selectedTree;
    private ForgottenAgeBucket? _age;
    private bool _onlyLarge, _onlyDownloads, _onlyMedia;

    private List<ForgottenCandidate> _all = [];
    private ForgottenSummary _summary = ForgottenSummary.Empty;
    private object? _builtFor;
    private bool _preparing = true;
    private List<ForgottenCandidate> _visible = [];

    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private readonly Grid _root = new();
    private TextBox? _search;

    public ForgottenExplorePage()
    {
        Background = Ui.Brush("RdPage");
        Content = _root;
        Focusable = true;
        ModelEvents.WhileLoaded(this, Rebuild);
        Loaded += (_, _) => Model.StagedChanged += OnStaged;
        Unloaded += (_, _) => Model.StagedChanged -= OnStaged;
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != _class) Rebuild(); };
        PreviewKeyDown += OnKey;
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    // ---- Words (pure) ----

    /// <summary>The Why column: the reasons, without the age (the Untouched column says it).</summary>
    public static string Why(IEnumerable<string> reasons) =>
        string.Join(" · ", reasons.Where(r => !r.StartsWith("not modified", StringComparison.OrdinalIgnoreCase)));

    public static string ConfidenceLabel(ForgottenConfidence confidence) => confidence switch
    {
        ForgottenConfidence.LikelyForgotten => "Likely forgotten",
        ForgottenConfidence.WorthReviewing => "Worth a look",
        _ => "Left out",
    };

    /// <summary>"45d ago", "3 months ago", "2 years ago", "Very old" (packaging dates can be absurd).</summary>
    public static string AgeText(int ageDays)
    {
        if (ageDays >= 15 * 365) return "Very old";
        if (ageDays < 60) return $"{ageDays}d ago";
        if (ageDays < 365)
        {
            int m = Math.Max(1, ageDays / 30);
            return m == 1 ? "1 month ago" : $"{m} months ago";
        }
        int y = Math.Max(1, ageDays / 365);
        return y == 1 ? "1 year ago" : $"{y} years ago";
    }

    private static bool InDownloads(string path) => path.Replace('\\', '/').ToLowerInvariant().Contains("/downloads/");

    /// <summary>The list as shown: tab, age, chips, search, then the sort.</summary>
    public static List<ForgottenCandidate> Filter(IEnumerable<ForgottenCandidate> all, FilterTab tab, ForgottenAgeBucket? age,
        bool onlyLarge, bool onlyDownloads, bool onlyMedia, string query, SortMode sort)
    {
        string q = query.Trim().ToLowerInvariant();
        var list = all.Where(c => tab switch
        {
            FilterTab.Likely => c.Confidence == ForgottenConfidence.LikelyForgotten,
            FilterTab.Worth => c.Confidence == ForgottenConfidence.WorthReviewing,
            FilterTab.Excluded => c.Confidence == ForgottenConfidence.OldImportant,
            _ => c.IsReviewable,
        });
        if (age is { } bucket) list = list.Where(c => ForgottenText.Bucket(c.AgeDays) == bucket);
        if (onlyLarge) list = list.Where(c => c.Bytes >= 1_000_000_000);
        if (onlyDownloads) list = list.Where(c => InDownloads(c.AbsolutePath));
        if (onlyMedia) list = list.Where(c => c.Kind is FileKind.Video or FileKind.DiskImage or FileKind.Archive or FileKind.DeviceBackup);
        if (q.Length > 0)
            list = list.Where(c => c.Name.ToLowerInvariant().Contains(q) || c.DisplayPath.ToLowerInvariant().Contains(q)
                || c.ParentDisplay.ToLowerInvariant().Contains(q));
        return sort switch
        {
            SortMode.Largest => list.OrderByDescending(c => c.Bytes).ToList(),
            SortMode.Oldest => list.OrderByDescending(c => c.AgeDays).ToList(),
            SortMode.Name => list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => list.OrderByDescending(c => c.Score).ToList(),
        };
    }

    // ---- Catalog ----

    private async void Build()
    {
        if (Model.Tree is not { } tree || Model.Totals.Length != tree.Count || Model.RootPath is not { } root) return;
        var totals = Model.Totals;
        _builtFor = totals;
        _preparing = true;
        var (candidates, summary) = await Task.Run(() =>
        {
            var list = ForgottenFiles.Candidates(tree, root, totals);
            return (list, ForgottenFiles.Summary(list));
        });
        if (!ReferenceEquals(_builtFor, totals)) return;
        _all = candidates;
        _summary = summary;
        _preparing = false;
        Rebuild();
    }

    // ---- Build ----

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        if (Model.Tree is not { } tree || Model.RootPath is not { } root) { Model.ShowPage("Overview"); return; }
        if (!ReferenceEquals(_selectedTree, tree)) { _selected = null; _selectedTree = tree; }   // ids belong to one scan
        if (!ReferenceEquals(_builtFor, Model.Totals)) Build();
        bool searchFocused = _search?.IsKeyboardFocusWithin == true;
        int caret = _search?.CaretIndex ?? 0;
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        if (_preparing)
        {
            // The catalog gate covers the page until the candidates are ready.
            _visible = [];
            _root.Children.Add(ListKit.LoadingState("Finding forgotten files…",
                "Working from the scan you already ran — nothing is read from disk again."));
            return;
        }

        _visible = Filter(_all, _tab, _age, _onlyLarge, _onlyDownloads, _onlyMedia, _query, _sort);
        double pad = _class.PagePadding();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, 10) };
        top.Children.Add(Header());
        var filters = FilterRow();
        filters.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(filters);
        var search = SearchRow();
        search.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(search);
        _root.Children.Add(top);

        var body = _visible.Count == 0 ? EmptyState() : Table(root, pad);
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        if (ListKit.SelectionToolbar("Forgotten Files") is { } bar)
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
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Rd.PageTitle("Forgotten files");
        var stats = Rd.Figure($"{_summary.ReviewableCount:N0} files · {ShellFormat.HeroBytes(_summary.ReviewableBytes)}", 12);
        row.Children.Add(Rd.BaselineRow(title, 26, stats, 12, 12));
        if (_class != Rd.WidthClass.Compact)
        {
            var lede = Rd.Text("Your own files, untouched for a year or more. App and system data is left out.", 12, null, "RdInk3");
            row.Children.Add(Rd.BaselineRow(new Border(), 26, lede, 12, 12));
        }
        dock.Children.Add(row);
        return dock;
    }

    private FrameworkElement FilterRow()
    {
        var dock = new DockPanel();
        var age = ListKit.Dropdown("Untouched for", AgeChoice.All, new AgeChoice(_age), a => a.Title, a => { _age = a.Bucket; Rebuild(); });
        age.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(age, Dock.Right);
        dock.Children.Add(age);
        var chips = new WrapPanel();
        var pageSwitch = ListKit.PageSwitch("Forgotten Files");
        pageSwitch.Margin = new Thickness(0, 0, 12, 0);
        chips.Children.Add(pageSwitch);
        void add(FrameworkElement chip) { chip.Margin = new Thickness(0, 0, 8, 0); chips.Children.Add(chip); }
        add(ListKit.Chip("All", _summary.ReviewableCount, _tab == FilterTab.All, () => { _tab = FilterTab.All; Rebuild(); }));
        add(ListKit.Chip("Likely forgotten", _summary.LikelyCount, _tab == FilterTab.Likely,
            () => { _tab = _tab == FilterTab.Likely ? FilterTab.All : FilterTab.Likely; Rebuild(); }));
        // A filter that's on always keeps its chip, or a narrow window couldn't turn it off.
        if (_class != Rd.WidthClass.Compact || _tab == FilterTab.Worth)
            add(ListKit.Chip("Worth a look", _summary.WorthCount, _tab == FilterTab.Worth,
                () => { _tab = _tab == FilterTab.Worth ? FilterTab.All : FilterTab.Worth; Rebuild(); }));
        add(ListKit.Chip("Over 1 GB", null, _onlyLarge, () => { _onlyLarge = !_onlyLarge; Rebuild(); }));
        if (_class == Rd.WidthClass.Wide || _onlyDownloads)
            add(ListKit.Chip("Downloads", null, _onlyDownloads, () => { _onlyDownloads = !_onlyDownloads; Rebuild(); }));
        if (_class == Rd.WidthClass.Wide || _onlyMedia)
            add(ListKit.Chip("Media", null, _onlyMedia, () => { _onlyMedia = !_onlyMedia; Rebuild(); }));
        dock.Children.Add(chips);
        return dock;
    }

    private FrameworkElement SearchRow()
    {
        var field = ListKit.SearchField(_query, [], _query.Length == 0 ? null : _visible.Count,
            "Search forgotten files by name or folder", "Search forgotten files",
            text => { _query = text; DebouncedRebuild(); }, out var input);
        _search = input;
        var trailing = new List<FrameworkElement>();
        if (_tab != FilterTab.Excluded && _summary.ExcludedCount > 0)
        {
            var leftOut = Rd.Link($"{_summary.ExcludedCount} left out on purpose", () => { _tab = FilterTab.Excluded; Rebuild(); },
                12, "RdInk3", FontWeights.Normal);
            leftOut.ToolTip = "Old files that belong to apps, package managers or Windows. They are never suggested.";
            trailing.Add(leftOut);
        }
        else if (_tab == FilterTab.Excluded)
        {
            trailing.Add(Rd.Link("Back to suggestions", () => { _tab = FilterTab.All; Rebuild(); }, 12, "RdAccentDeep", FontWeights.Normal));
        }
        trailing.Add(ListKit.Dropdown("Sort", Enum.GetValues<SortMode>(), _sort, SortTitle, s => { _sort = s; Rebuild(); }));
        return BiggestFoldersExplorePage.FieldRow(field, _class == Rd.WidthClass.Compact ? null : 520, 12, [.. trailing]);
    }

    private FrameworkElement Table(string root, double pad)
    {
        var columns = ListKit.Columns.For(_class);
        var items = _visible;
        long largest = Math.Max(1, items.Count == 0 ? 1 : items.Max(c => c.Bytes));
        var staged = Model.StagedItems.Select(s => System.IO.Path.GetFullPath(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var grid = new Grid { Margin = new Thickness(pad, 0, pad, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(ListKit.ColumnHeader(columns, "Suggestion", _sort == SortMode.Largest ? "Size ↓" : "Size", false,
            _sort == SortMode.Oldest ? "Untouched ↓" : "Untouched", "Why"));

        var list = new StackPanel();
        foreach (var c in items)
        {
            bool stageable = c.IsReviewable && c.Safety.Level != SafetyLevel.Protected;
            var candidate = c;
            list.Children.Add(ListKit.FileRow(c.Name, BiggestFilesExplorePage.RelativeParent(c.AbsolutePath, root),
                ListKit.Badge(c.Name, c.Kind), ConfidenceLabel(c.Confidence),
                (double)c.Bytes / largest, c.Bytes, AgeText(c.AgeDays),
                new ListKit.Note(ListKit.NoteKind.Plain, Why(c.Reasons)),
                c.Confidence == ForgottenConfidence.LikelyForgotten,
                staged.Contains(c.AbsolutePath),
                c.Id == _selected || Model.MultiSelection.Contains(c.Id), columns,
                select: () => SelectRow(candidate.Id),
                add: stageable ? () => StageOne(candidate) : null));
        }
        var scroll = _scroll.Wrap(list, $"{_query}|{_onlyLarge}|{_onlyDownloads}|{_onlyMedia}");
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var footer = Footer();
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        return grid;
    }

    private FrameworkElement Footer()
    {
        var likely = _visible.Where(c => c.Confidence == ForgottenConfidence.LikelyForgotten && c.IsReviewable
            && c.Safety.Level != SafetyLevel.Protected).ToList();
        long likelyBytes = likely.Sum(c => c.Bytes);
        var text = ListKit.FooterText(($"{_visible.Count:N0} shown · {ShellFormat.HeroBytes(_visible.Sum(c => c.Bytes))}", "RdInk2"));
        var trailing = new List<FrameworkElement>();
        if (_class != Rd.WidthClass.Compact)
            trailing.Add(Rd.Text("By last-modified date: a file you only open may still look old.", 12, null, "RdInk3"));
        if (likely.Count > 0)
            trailing.Add(Rd.SecondaryButton($"Add {likely.Count} likely forgotten · {ShellFormat.HeroBytes(likelyBytes)}", () =>
                Model.ConfirmStageMany(likely.Select(c => c.Id), "Forgotten: " + ForgottenConfidence.LikelyForgotten.Title(), "Forgotten files")));
        return ListKit.FooterBar(text, [.. trailing]);
    }

    private FrameworkElement EmptyState()
    {
        if (_summary.ExcludedCount > 0 && _summary.ReviewableCount == 0 && _tab != FilterTab.Excluded)
            return ListKit.EmptyState(Icons.Forgotten, "Nothing worth reviewing yet",
                "Old files were found, but they belong to apps, package managers or Windows, so they aren’t suggested here.",
                Rd.SecondaryButton("Show excluded", () => { _tab = FilterTab.Excluded; Rebuild(); }));
        return ListKit.EmptyState(Icons.Forgotten,
            _tab == FilterTab.Excluded ? "No excluded old files" : "No forgotten files found",
            "Try another filter, or clear the search.");
    }

    // ---- Actions ----

    private void SelectRow(int id)
    {
        _selected = id;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Model.ToggleMulti(id);
        else { Model.ClearMulti(); Model.Select(id); }
        Rebuild();
    }

    private static void StageOne(ForgottenCandidate c)
    {
        if (!c.IsReviewable || c.Safety.Level == SafetyLevel.Protected)
        {
            Model.Toast("Not recommended for cleanup");
            return;
        }
        Model.Stage(c.Id, "Forgotten: " + c.Confidence.Title());
    }

    private System.Windows.Threading.DispatcherTimer? _debounce;

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

    private void OnKey(object sender, KeyEventArgs e)
    {
        var items = _visible;
        var ids = items.Select(c => c.Id).ToList();
        int? selected = _selected;
        if (ListKit.ListKey(e, ids, ref selected,
                id => { if (items.FirstOrDefault(c => c.Id == id) is { } c) StageOne(c); },
                id => items.FirstOrDefault(c => c.Id == id)?.AbsolutePath,
                () =>
                {
                    Model.ClearMulti();
                    foreach (var c in items.Where(c => c.IsReviewable)) Model.ToggleMulti(c.Id);
                }))
        {
            _selected = selected;
            e.Handled = true;
            Rebuild();
        }
    }
}
