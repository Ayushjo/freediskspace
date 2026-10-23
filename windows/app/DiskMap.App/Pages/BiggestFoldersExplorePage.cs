using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-107: Explore → List → Folders (Mac: BiggestFoldersView, redesign board 05), also the
/// File Browser: what's inside the current folder, biggest first, with what can be freed in
/// each. Double-click opens a folder; Up and the path go back.
/// </summary>
public sealed class BiggestFoldersExplorePage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    /// <summary>Size first, like the rest of Explore; the file browser's other orders too.</summary>
    public enum SortMode { Size, Name, Changed, Kind }

    public static string SortTitle(SortMode mode) => mode switch
    {
        SortMode.Name => "Name",
        SortMode.Changed => "Recently changed",
        SortMode.Kind => "Kind",
        _ => "Largest",
    };

    /// <summary>
    /// Technical stubs at a drive's root, hidden until "Show technical" (Mac: .vol, dev, cores…):
    /// junctions and Windows' own setup and recovery folders.
    /// </summary>
    public static readonly IReadOnlySet<string> HiddenTechnical = new HashSet<string>(
        ["Documents and Settings", "Config.Msi", "PerfLogs", "Recovery", "$WinREAgent", "$SysReset",
         "$GetCurrent", "DumpStack.log.tmp", "DumpStack.log"],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _pageKey;
    private string _query = "";
    private SortMode _sort = SortMode.Size;
    private bool _showTechnical;
    private int? _selected;
    private readonly ScrollMemory _scroll = new();
    private FileTree? _selectedTree;
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private readonly Grid _root = new();
    private TextBox? _search;
    private List<int> _shownIds = [];

    public BiggestFoldersExplorePage() : this("Biggest Folders") { }

    /// <param name="pageKey">"Biggest Folders" or "File Browser": the same page under either key.</param>
    public BiggestFoldersExplorePage(string pageKey)
    {
        _pageKey = pageKey;
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

    private static int Current(FileTree tree) =>
        Model.ZoomedNode >= 0 && Model.ZoomedNode < tree.Count ? Model.ZoomedNode : 0;

    // ---- Words and order (pure, shared with Find) ----

    /// <summary>Folders before files when sorting by kind; ties fall back to size.</summary>
    public static List<(int Id, long Size)> Sorted(IEnumerable<(int Id, long Size)> rows, SortMode mode, FileTree tree)
    {
        var list = rows.ToList();
        switch (mode)
        {
            case SortMode.Name:
                var names = NameComparer;
                return list.OrderBy(r => tree.NameOf(r.Id), names).ToList();
            case SortMode.Changed:
                return list.OrderByDescending(r => tree.ModifiedDay[r.Id]).ThenByDescending(r => r.Size).ToList();
            case SortMode.Kind:
                return list.OrderByDescending(r => tree.IsDirectory[r.Id])
                    .ThenBy(r => Extension(tree.NameOf(r.Id)), StringComparer.Ordinal)
                    .ThenByDescending(r => r.Size).ToList();
            default:
                return list.OrderByDescending(r => r.Size).ThenBy(r => r.Id).ToList();
        }
    }

    /// <summary>File Explorer's order: case-insensitive, digits by value ("2" before "10").</summary>
    private static readonly StringComparer NameComparer = CreateNameComparer();

    private static StringComparer CreateNameComparer()
    {
        try
        {
            return StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        }
        catch (ArgumentException)
        {
            return StringComparer.CurrentCultureIgnoreCase;
        }
    }

    private static string Extension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : "";
    }

    /// <summary>"1,204 files · 31 folders".</summary>
    public static string Contents(int files, int folders)
    {
        var parts = new List<string> { $"{files:N0} file{(files == 1 ? "" : "s")}" };
        if (folders > 0) parts.Add($"{folders:N0} folder{(folders == 1 ? "" : "s")}");
        return string.Join(" · ", parts);
    }

    /// <summary>"8 folders", "3 folders, 1 file", "2 files".</summary>
    public static string FooterCount(int folders, int files)
    {
        var parts = new List<string>();
        if (folders > 0) parts.Add($"{folders:N0} folder{(folders == 1 ? "" : "s")}");
        if (files > 0 || folders == 0) parts.Add($"{files:N0} file{(files == 1 ? "" : "s")}");
        return string.Join(", ", parts);
    }

    public static string FolderKind(StorageClass storageClass) => storageClass.Id == "other" ? "Folder" : storageClass.Title;

    /// <summary>The "Can be freed" column for a folder: all of it, part of it, look first, or nothing.</summary>
    internal static ListKit.Note FolderNote(bool contained, long freeBytes, long size, StorageVerdict verdict)
    {
        if (contained) return new ListKit.Note(ListKit.NoteKind.Accent, $"{ShellFormat.HeroBytes(size)} rebuilds by itself");
        if (freeBytes > 0) return new ListKit.Note(ListKit.NoteKind.Accent, $"{ShellFormat.HeroBytes(freeBytes)} can be freed");
        if (verdict.Advice is CleanupAdvice.Warn or CleanupAdvice.Never) return new ListKit.Note(ListKit.NoteKind.Review, verdict.Instead);
        return ListKit.Note.None;
    }

    /// <summary>A drive's root ("C:\"), where the technical stubs live.</summary>
    public static bool IsVolumeRoot(string? root)
    {
        if (root is null) return false;
        string r = root.TrimEnd('\\', '/');
        return r.Length == 2 && r[1] == ':' || r.Length == 0;
    }

    // ---- Rows ----

    private List<(int Id, long Size)> RawRows(FileTree tree, int current)
    {
        var totals = Model.Totals;
        if (tree.Count == 0 || totals.Length != tree.Count) return [];
        return Sorted(tree.ChildrenOf(current, totals), _sort, tree);
    }

    private bool AtScannedVolumeRoot(FileTree tree) => Current(tree) == 0 && IsVolumeRoot(Model.RootPath);

    private List<(int Id, long Size)> Rows(FileTree tree, List<(int Id, long Size)> raw, ListQuery q, string root)
    {
        int today = AgeMap.Today();
        bool hideTechnical = AtScannedVolumeRoot(tree) && !_showTechnical;
        return raw.Where(row =>
        {
            string name = tree.NameOf(row.Id);
            if (hideTechnical && (row.Size <= 0 || HiddenTechnical.Contains(name))) return false;
            if (q.IsEmpty) return true;
            string abs = tree.PathOf(row.Id, root);
            string display = CanonicalPath.DisplayPath(abs).ToLowerInvariant();
            int day = tree.ModifiedDay[row.Id];
            bool isDir = tree.IsDirectory[row.Id];
            return q.Matches(name.ToLowerInvariant() + "\n" + display, display, row.Size,
                isDir ? FileKind.Other : FileKinds.Classify(name, abs), day > 0 ? today - day : null);
        }).ToList();
    }

    // ---- Build ----

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        if (Model.Tree is not { } tree || Model.RootPath is not { } root) { Model.ShowPage("Overview"); return; }
        if (!ReferenceEquals(_selectedTree, tree)) { _selected = null; _selectedTree = tree; }   // ids belong to one scan
        bool searchFocused = _search?.IsKeyboardFocusWithin == true;
        int caret = _search?.CaretIndex ?? 0;

        int current = Current(tree);
        var totals = Model.Totals;
        long parentTotal = current < totals.Length ? Math.Max(totals[current], 1) : 1;
        var parsed = ListQuery.Parse(_query);
        var raw = RawRows(tree, current);
        var shown = Rows(tree, raw, parsed, root);

        double pad = _class.PagePadding();
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, 10) };
        top.Children.Add(Header(tree, current, parentTotal));
        var filters = FilterRow(tree, current);
        filters.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(filters);
        var search = SearchRow(parsed, shown.Count);
        search.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(search);
        _root.Children.Add(top);

        FrameworkElement body = shown.Count == 0
            ? ListKit.EmptyState(Icons.Folder,
                raw.Count == 0 ? "Nothing with a size in this folder" : "No folders match",
                raw.Count == 0 ? "Try another folder, or go back up." : "Clear the search or show technical folders.")
            : Table(tree, root, current, parentTotal, shown, pad);
        _shownIds = shown.Take(RowLimit).Select(r => r.Id).ToList();
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        if (ListKit.SelectionToolbar(_pageKey) is { } bar)
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

    private FrameworkElement Header(FileTree tree, int current, long parentTotal)
    {
        var dock = new DockPanel();
        var views = ExplorePage.ViewSwitch(onList: true);
        views.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(views, Dock.Right);
        dock.Children.Add(views);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Rd.PageTitle("Biggest folders");
        var stats = Rd.Figure($"{(tree.Count > 0 ? tree.NameOf(current) : "")} · {ShellFormat.HeroBytes(parentTotal)}", 12);
        row.Children.Add(Rd.BaselineRow(title, 26, stats, 12, 12));
        if (_class != Rd.WidthClass.Compact)
        {
            var lede = Rd.Text("What's inside, biggest first. Double-click a folder to open it.", 12, null, "RdInk3");
            row.Children.Add(Rd.BaselineRow(new Border(), 26, lede, 12, 12));
        }
        dock.Children.Add(row);
        return dock;
    }

    private FrameworkElement FilterRow(FileTree tree, int current)
    {
        var dock = new DockPanel();
        if (AtScannedVolumeRoot(tree))
        {
            var technical = ListKit.Chip("Show technical", null, _showTechnical, () => { _showTechnical = !_showTechnical; Rebuild(); });
            technical.ToolTip = "Show zero-size and technical system stubs at the drive root";
            technical.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(technical, Dock.Right);
            dock.Children.Add(technical);
        }
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var pageSwitch = ListKit.PageSwitch(_pageKey);
        pageSwitch.Margin = new Thickness(0, 0, 12, 0);
        left.Children.Add(pageSwitch);
        bool canGoBack = current > 0 && tree.Parent[current] >= 0;
        var up = Rd.IconButton("BsChevronLeft", "Up to the enclosing folder", () => { if (canGoBack) Open(tree, tree.Parent[current]); });
        up.IsEnabled = canGoBack;
        up.Opacity = canGoBack ? 1 : 0.35;
        up.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(up);
        var crumbs = Breadcrumb(tree, current);
        crumbs.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(crumbs);
        dock.Children.Add(left);
        return dock;
    }

    /// <summary>BreadcrumbBar: the path, "/"-separated, the current folder in ink.</summary>
    private FrameworkElement Breadcrumb(FileTree tree, int current)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var chain = tree.AncestorIds(current);
        for (int i = 0; i < chain.Count; i++)
        {
            int id = chain[i];
            if (i > 0)
            {
                var slash = Rd.Figure("/", 11, null, "RdInk3");
                slash.Margin = new Thickness(6, 0, 6, 0);
                row.Children.Add(slash);
            }
            bool isCurrent = id == current;
            var crumb = Rd.Text(tree.NameOf(id), 13, isCurrent ? FontWeights.Medium : FontWeights.Normal, isCurrent ? "RdInk" : "RdInk2");
            crumb.Cursor = Cursors.Hand;
            crumb.MouseLeftButtonUp += (_, _) => Open(tree, id);
            System.Windows.Automation.AutomationProperties.SetName(crumb, tree.NameOf(id));
            row.Children.Add(crumb);
        }
        return new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private FrameworkElement SearchRow(ListQuery parsed, int matches)
    {
        var field = ListKit.SearchField(_query, parsed, parsed.IsEmpty ? null : matches,
            "Search here, or try size>1GB  changed>1y", text => { _query = text; DebouncedRebuild(); }, out var input);
        _search = input;
        var sort = ListKit.Dropdown("Sort", Enum.GetValues<SortMode>(), _sort, SortTitle, s => { _sort = s; Rebuild(); });
        return FieldRow(field, _class == Rd.WidthClass.Compact ? null : 760, 12, sort);
    }

    /// <summary>
    /// HStack { field.frame(maxWidth:); Spacer(minLength: 0); trailing }: the field takes the room
    /// up to its cap, the trailing controls sit at the right edge.
    /// </summary>
    internal static Grid FieldRow(FrameworkElement field, double? maxWidth, double spacing, params FrameworkElement[] trailing)
    {
        var grid = new Grid();
        var fieldColumn = new ColumnDefinition { Width = new GridLength(1000, GridUnitType.Star) };
        if (maxWidth is { } cap) fieldColumn.MaxWidth = cap;
        grid.ColumnDefinitions.Add(fieldColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(field);
        int column = 2;
        foreach (var t in trailing)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            t.Margin = new Thickness(spacing, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, column++);
            grid.Children.Add(t);
        }
        return grid;
    }

    private const int RowLimit = 500;

    private FrameworkElement Table(FileTree tree, string root, int current, long parentTotal, List<(int Id, long Size)> shown, double pad)
    {
        var columns = ListKit.Columns.For(_class);
        var totals = Model.Totals;
        var index = new FreeableIndex(Model.Reviewables ?? [], totals);
        var staged = Model.StagedItems.Select(s => System.IO.Path.GetFullPath(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int today = AgeMap.Today();
        var (fileCounts, folderCounts) = Model.Counts;

        var grid = new Grid { Margin = new Thickness(pad, 0, pad, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = ListKit.ColumnHeader(columns, "Kind", _sort == SortMode.Size ? "Size ↓" : "Size", _sort == SortMode.Size,
            "Last changed", "Can be freed");
        grid.Children.Add(header);

        var list = new StackPanel();
        var rows = shown.Take(RowLimit).ToList();
        foreach (var (id, size) in rows)
        {
            bool isDir = tree.IsDirectory[id];
            string name = tree.NameOf(id);
            string abs = tree.PathOf(id, root);
            var verdict = StorageClassifier.Classify(abs, isDir);
            bool contained = index.Contains(id, tree);
            long freeBytes = index.Bytes(id, tree, totals);
            bool isProtected = SafetyClassifier.Assess(abs, name, isDir).Level == SafetyLevel.Protected;
            var kind = isDir ? FileKind.Other : FileKinds.Classify(name, abs);
            string place = isDir
                ? Contents(id < fileCounts.Length ? fileCounts[id] : 0, id < folderCounts.Length ? folderCounts[id] : 0)
                : BiggestFilesExplorePage.RelativeParent(abs, root);
            int captured = id;
            long capturedSize = size;
            list.Children.Add(ListKit.FileRow(name, place,
                isDir ? "DIR" : ListKit.Badge(name, kind),
                isDir ? FolderKind(verdict.Class) : ListKit.KindText(kind, verdict.Class),
                (double)size / parentTotal, size, ExplorePage.LastUsed(tree.ModifiedDay[id], today),
                FolderNote(contained, freeBytes, size, verdict),
                contained || (size > 0 && (double)freeBytes / size >= 0.6),
                staged.Contains(abs),
                id == _selected || Model.MultiSelection.Contains(id), columns,
                select: () => SelectRow(captured),
                add: isProtected ? null : () => Stage(tree, captured),
                open: isDir ? () => Open(tree, captured) : null));
        }
        if (shown.Count > rows.Count)
        {
            var more = Rd.Text($"Showing the {rows.Count:N0} biggest of {shown.Count:N0} — search to find the rest.", 12, null, "RdInk3");
            more.Margin = new Thickness(12, 10, 0, 10);
            list.Children.Add(more);
        }
        var scroll = _scroll.Wrap(list, $"{Current(tree)}|{_query}");
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var footer = Footer(tree, current, parentTotal, shown, index);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        return grid;
    }

    private FrameworkElement Footer(FileTree tree, int current, long parentTotal, List<(int Id, long Size)> shown, FreeableIndex index)
    {
        long freeBytes = index.Bytes(current, tree, Model.Totals);
        int folders = shown.Count(r => tree.IsDirectory[r.Id]);
        var runs = new List<(string, string)> { ($"{FooterCount(folders, shown.Count - folders)} · {ShellFormat.HeroBytes(parentTotal)}", "RdInk2") };
        if (freeBytes > 0)
        {
            runs.Add(("  ·  ", "RdInk2"));
            runs.Add(($"{ShellFormat.HeroBytes(freeBytes)} can be freed here", "RdAccentDeep"));
        }
        var text = ListKit.FooterText([.. runs]);
        return freeBytes > 0
            ? ListKit.FooterBar(text, Rd.SecondaryButton($"Add {ShellFormat.HeroBytes(freeBytes)} to basket", () => StageFreeable(tree, current)))
            : ListKit.FooterBar(text);
    }

    /// <summary>Everything that rebuilds by itself under the current folder, through the bulk-add confirmation.</summary>
    private static void StageFreeable(FileTree tree, int current)
    {
        var targets = (Model.Reviewables ?? []).Where(t => t.IsGenerallySafe)
            .Select(t => (t.DisplayName, Ids: t.NodeIds.Where(n => n == current || FreeableIndex.IsAncestor(current, n, tree)).ToList()))
            .Where(t => t.Ids.Count > 0).ToList();
        if (targets.Count == 0)
        {
            // Inside something that rebuilds by itself (a folder in node_modules or Temp): the
            // footer counts this whole folder, so this folder is what goes in.
            var around = (Model.Reviewables ?? []).Where(t => t.IsGenerallySafe)
                .FirstOrDefault(t => t.NodeIds.Any(n => FreeableIndex.IsAncestor(n, current, tree)));
            if (around is null) return;
            Model.ConfirmStageMany([current], "Explore: " + around.DisplayName,
                $"Things that rebuild by themselves in {tree.NameOf(current)}");
            return;
        }
        string reason = targets.Count == 1 ? "Explore: " + targets[0].DisplayName : "Explore: rebuilds by itself";
        Model.ConfirmStageMany(targets.SelectMany(t => t.Ids), reason,
            $"Things that rebuild by themselves in {tree.NameOf(current)}");
    }

    // ---- Actions ----

    private void SelectRow(int id)
    {
        Focus();   // a click gives the list the keyboard (the search box kept it, so ↑↓ and Delete did nothing)
        _selected = id;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Model.ToggleMulti(id);
        else { Model.ClearMulti(); Model.Select(id); }
        Rebuild();
    }

    private static void Stage(FileTree tree, int id) => Model.Stage(id, "Biggest folder: " + tree.NameOf(id));

    /// <summary>Opens a folder: it becomes the current one (shared with the map), selected, search cleared.</summary>
    private void Open(FileTree tree, int id)
    {
        if (id < 0 || id >= tree.Count || !tree.IsDirectory[id]) return;
        if (tree.AncestorIds(Current(tree)).Contains(id)) Model.DrillToAncestor(id);
        else Model.DrillTo(id);
        _selected = null;
        Model.Select(id);
        _query = "";
        if (_search is not null) _search.Text = "";
        Rebuild();
    }

    private System.Windows.Threading.DispatcherTimer? _debounce;

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

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Model.Tree is not { } tree || Model.RootPath is not { } root) return;
        var ids = _shownIds;
        int? selected = _selected;
        if (ListKit.ListKey(e, ids, ref selected, id => Stage(tree, id), id => tree.PathOf(id, root), () =>
            {
                Model.ClearMulti();
                foreach (int id in ids) Model.ToggleMulti(id);
            }))
        {
            _selected = selected;
            e.Handled = true;
            Rebuild();
        }
    }
}
