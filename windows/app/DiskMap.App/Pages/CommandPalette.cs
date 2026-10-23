using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-115: Ctrl+K — find a folder, a file, or type a command (Mac: CommandPalette). An overlay
/// over the window: a dim backdrop and a 760 x 560 card with the boards' hard shadow. Actions
/// first, then folders and files from the scan by size; the palette speaks the Find query
/// language. ↑/↓ move, Enter runs, Ctrl+Enter shows in File Explorer, Ctrl+B adds to the
/// basket, Esc closes.
/// </summary>
internal sealed class CommandPalette : Grid
{
    private static ScanModel Model => ScanModel.Shared;

    private enum Category { Folders, Applications, Files, Actions }

    private static string CategoryTitle(Category c) => c switch
    {
        Category.Folders => "Folders",
        Category.Applications => "Applications",
        Category.Files => "Files",
        _ => "Actions",
    };

    private sealed record Hit(int NodeId, int ParentId, string Name, string Path, bool IsDirectory, bool IsApplication, long Bytes);

    private sealed record Command(string Title, string Subtitle, string Icon, Action Run, Category Category = Category.Actions,
        string? Trailing = null, bool TrailingAccent = false, Hit? Hit = null, string? KindTag = null)
    {
        public string Key => Title + "\u0001" + Subtitle;
    }

    private readonly TextBox _input;
    private readonly TextBlock _placeholder;
    private readonly TextBlock _resultCount = Rd.Text("", 12, null, "RdInk3");
    private readonly StackPanel _problems = new();
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _footer = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Action _close;
    private List<Hit> _hits = [];
    private bool _searching;
    private int _matchCount;
    private long _matchedBytes;
    private int _highlighted;
    private List<Command> _items = [];
    private CancellationTokenSource? _search;
    private readonly System.Windows.Threading.DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public CommandPalette(string initialQuery, Action close)
    {
        _close = close;
        // The dim backdrop: a click outside closes.
        var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)) };
        scrim.MouseLeftButtonDown += (_, _) => _close();
        System.Windows.Automation.AutomationProperties.SetName(scrim, "Dismiss command palette");
        Children.Add(scrim);

        _input = new TextBox
        {
            Text = initialQuery, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = Rd.FontFor(20), FontSize = Ui.Scaled(20), FontWeight = FontWeights.Medium,
            Foreground = Ui.Brush("RdInk"), CaretBrush = Ui.Brush("RdInk"), VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
        };
        System.Windows.Automation.AutomationProperties.SetName(_input, "Find anything");
        _placeholder = new TextBlock
        {
            Text = "Find a folder, a file, or type a command", FontFamily = Rd.FontFor(20), FontSize = Ui.Scaled(20),
            Foreground = Ui.Brush("RdInk3"), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center,
        };
        _input.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _highlighted = 0;
            _debounce.Stop();
            _debounce.Start();
            _searching = _input.Text.Trim().Length > 0;
            Render();
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = UpdateSearch(); };
        _input.PreviewKeyDown += OnKey;

        var field = new Grid();
        field.Children.Add(_placeholder);
        field.Children.Add(_input);
        var head = new DockPanel { Height = 64, Margin = new Thickness(22, 0, 22, 0) };
        var glass = Ui.Glyph(Icons.Search, 17, Ui.Brush("RdInk"));
        glass.Margin = new Thickness(0, 0, 12, 0);
        glass.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(glass, Dock.Left);
        head.Children.Add(glass);
        var esc = Kbd("esc");
        esc.Cursor = Cursors.Hand;
        esc.MouseLeftButtonUp += (_, _) => _close();
        System.Windows.Automation.AutomationProperties.SetName(esc, "Close");
        esc.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(esc, Dock.Right);
        head.Children.Add(esc);
        _resultCount.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(_resultCount, Dock.Right);
        head.Children.Add(_resultCount);
        head.Children.Add(field);

        _problems.Margin = new Thickness(22, 0, 22, 8);
        _list.Margin = new Thickness(10, 0, 10, 10);
        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        DockPanel.SetDock(_problems, Dock.Top);
        body.Children.Add(_problems);
        var line1 = new Border { Height = 1, Background = Ui.Brush("RdLine") };
        DockPanel.SetDock(line1, Dock.Top);
        body.Children.Add(line1);
        var foot = new Border
        {
            Height = 46, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Border { Margin = new Thickness(22, 0, 22, 0), Child = _footer },
        };
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(foot);
        body.Children.Add(_scroll);

        var face = new Border
        {
            Background = Ui.Brush("RdCard"), BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Child = body,
        };
        face.SizeChanged += (_, e) => body.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width - 2, e.NewSize.Height - 2), 13, 13);
        var card = new Grid { Width = 760, Height = 560, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(14), Margin = new Thickness(6, 6, -6, -6) });
        card.Children.Add(face);
        System.Windows.Automation.AutomationProperties.SetName(card, "Find anything");
        Children.Add(card);

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.CaretIndex = _input.Text.Length;   // keep typing where the top bar left off
            if (_input.Text.Trim().Length > 0) _ = UpdateSearch();
        };
        Unloaded += (_, _) => { _debounce.Stop(); _search?.Cancel(); };
        _placeholder.Visibility = initialQuery.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    // ---- Commands ----

    private string Query => _input.Text.Trim();

    private FileQuery.Parsed? Parsed =>
        Model.RootPath is { } root ? FileQuery.Parse(_input.Text, UserFolders.Home, root) : null;

    private bool IsStructured => Parsed?.Query.IsStructured ?? false;

    private static string? Shortcut(string pageKey) =>
        Array.IndexOf(MainWindowDestinations, pageKey) is int i and >= 0 ? $"Ctrl+{i + 1}" : null;

    /// <summary>The sidebar order (Ctrl+1..8), as MainWindow maps it.</summary>
    private static readonly string[] MainWindowDestinations =
        ["Overview", "Safe to Review", "Visualize", "Developer Storage", "Duplicates", "Snapshots", "Applications", "Cleanup"];

    private List<Command> Commands()
    {
        long safe = Model.SafeReclaimableBytes;
        var list = new List<Command>
        {
            new(safe > 0 ? $"Clean up {ShellFormat.HeroBytes(safe)}" : "Clean up", "Caches and build files that rebuild by themselves",
                Icons.NavCleanUp, () => Model.ShowPage("Safe to Review"), Trailing: Shortcut("Safe to Review"), TrailingAccent: true),
            new("Open Developer", "node_modules, Gradle, NuGet, WSL and Docker disks", Icons.NavDeveloper,
                () => Model.ShowPage("Developer Storage"), Trailing: Shortcut("Developer Storage")),
            new("Open Explore", "Every folder drawn to its size", Icons.NavExplore, () => Model.ShowPage("Visualize")),
            new("Biggest files", "Explore · List", Icons.File, () => Model.ShowPage("Biggest Files")),
            new("Find with a query", "ext:mp4 size>500MB age>1y in:downloads", Icons.Search, () => Model.ShowPage("Find")),
            new("Duplicates", "Files with exactly the same contents", Icons.NavDuplicates, () => Model.ShowPage("Duplicates")),
            new("Forgotten files", "Not opened in over a year", "BsClockHistory", () => Model.ShowPage("Forgotten Files")),
            new("What changed", "Compare with a saved scan", Icons.NavWhatChanged, () => Model.ShowPage("Snapshots")),
            new("History & Put Back", "Every cleanup, and the way back", Icons.NavHistory, () => Model.ShowPage("Cleanup")),
            new("Review basket", "Opens the review — never deletes directly", Icons.Cleanup, () => Model.OpenBasket()),
            new("Rescan " + (Model.RootPath is { } r ? CanonicalPath.DisplayPath(r) : ""),
                Model.LastScanFinishedAt is { } when ? "Last scan " + ShellFormat.RelativeTime(when, DateTimeOffset.Now) : "Re-read what changed",
                Icons.Rescan, () => _ = Model.RescanAsync(), Trailing: "Ctrl+R"),
            new("Full rescan", "Walk every folder again, ignoring the last scan", Icons.Rescan, () =>
            {
                if (Model.RootPath is { } root) ScanCache.Remove(root);
                _ = Model.RescanAsync();
            }),
        };
        // Saved searches are commands too.
        foreach (var search in SavedSearches.Load())
        {
            string query = search.Query;
            list.Add(new Command(search.Name, "Saved search · " + query, Icons.Search, () =>
            {
                Model.SearchQuery = query;
                Model.ShowPage("Find");
            }));
        }
        if (Query.Length > 0)
        {
            foreach (var hit in _hits)
            {
                var kind = hit.IsDirectory ? FileKind.Other : FileKinds.Classify(hit.Name, hit.Path);
                var captured = hit;
                list.Add(new Command(hit.Name, ParentPath(hit.Path),
                    hit.IsApplication ? Icons.Grid : hit.IsDirectory ? Icons.Folder : Icons.File,
                    () => Open(captured),
                    hit.IsApplication ? Category.Applications : hit.IsDirectory ? Category.Folders : Category.Files,
                    Trailing: ShellFormat.HeroBytes(hit.Bytes), Hit: hit, KindTag: kind == FileKind.Other ? null : ListKit.KindName(kind)));
            }
        }
        return list;
    }

    private static void Open(Hit hit)
    {
        Model.ShowPage("Visualize");
        Model.DrillTo(hit.IsDirectory ? hit.NodeId : Math.Max(0, hit.ParentId));
        Model.Select(hit.NodeId);
    }

    private List<Command> Filtered()
    {
        var all = Commands();
        string q = Query.ToLowerInvariant();
        if (q.Length == 0) return all.Where(c => c.Category == Category.Actions).ToList();
        if (IsStructured)
        {
            string meaning = string.Join(" · ", Parsed?.Query.Describe() ?? []);
            string summary = _searching ? "Searching…" : $"{_matchCount:N0} match{(_matchCount == 1 ? "" : "es")} · {ByteFormat.Format(_matchedBytes)}";
            var findAll = new Command($"Show all in Find — {summary}", meaning, Icons.Search, () =>
            {
                Model.SearchQuery = Query;
                Model.ShowPage("Find");
            });
            return [findAll, .. all.Where(c => c.Category != Category.Actions)];
        }
        // File hits were matched word by word already; only actions match the typed text here.
        return all.Where(c => c.Category != Category.Actions
            || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ---- Render ----

    private void Render()
    {
        var filtered = Filtered();
        _items = Enum.GetValues<Category>().SelectMany(c => filtered.Where(f => f.Category == c)).ToList();
        if (_items.Count > 0) _highlighted = Math.Clamp(_highlighted, 0, _items.Count - 1);
        var current = _items.Count == 0 ? null : _items[_highlighted];
        int results = _items.Count(i => i.Category != Category.Actions);
        _resultCount.Text = Query.Length == 0 ? "" : _searching ? "Searching…" : $"{results} result{(results == 1 ? "" : "s")}";

        _problems.Children.Clear();
        if (IsStructured && Parsed is { } parsed)
            foreach (var problem in parsed.Problems)
                _problems.Children.Add(Rd.Text($"Ignoring {problem.Token} — {problem.Message}", 12, null, "AppWarning"));

        _list.Children.Clear();
        var categories = Enum.GetValues<Category>();
        foreach (var category in categories)
        {
            var section = _items.Where(i => i.Category == category).ToList();
            if (section.Count == 0) continue;
            var label = new DockPanel { Margin = new Thickness(14, 14, 14, 6) };
            if (category != Category.Actions)
            {
                var by = Rd.Figure("BY SIZE", 10.5, FontWeights.Medium, "RdInk3");
                DockPanel.SetDock(by, Dock.Right);
                label.Children.Add(by);
            }
            label.Children.Add(Rd.Figure(CategoryTitle(category).ToUpperInvariant(), 10.5, FontWeights.Medium, "RdInk3"));
            _list.Children.Add(label);
            foreach (var cmd in section) _list.Children.Add(Row(cmd, cmd == current));
            if (category != categories[^1])
                _list.Children.Add(new Border { Height = 1, Background = Ui.Brush("RdLine"), Margin = new Thickness(-10, 8, -10, 0) });
        }
        if (_searching && _items.All(i => i.Category == Category.Actions))
        {
            var wait = Rd.Text("Searching this scan…", 12, null, "RdInk2");
            wait.Margin = new Thickness(14);
            _list.Children.Add(wait);
        }

        _footer.Children.Clear();
        void key(string k) { var kbd = Kbd(k); kbd.Margin = new Thickness(0, 0, 8, 0); _footer.Children.Add(kbd); }
        void hint(string text) { var t = Rd.Text(text, 12, null, "RdInk2"); t.Margin = new Thickness(0, 0, 18, 0); _footer.Children.Add(t); }
        key("↑"); key("↓"); hint("move");
        key("↵"); hint("open");
        if (current?.Hit is not null)
        {
            key("Ctrl"); key("↵"); hint("show in File Explorer");
            key("Ctrl"); key("B"); hint("add to basket");
        }
        key("esc"); hint("close");
        ScrollToHighlighted();
    }

    private FrameworkElement Row(Command cmd, bool on)
    {
        var row = new DockPanel { Margin = new Thickness(12, 0, 12, 0) };
        var tile = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Ui.Brush("RdCard"),
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 14, 0),
            Child = new Border
            {
                Child = Ui.Glyph(cmd.Icon, 13, Ui.Brush("RdInk")),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);
        if (on)
        {
            var run = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            run.Children.Add(Kbd("↵"));
            var word = Rd.Figure(cmd.Category == Category.Actions ? "RUN" : "OPEN", 11, FontWeights.Medium, "RdAccentDeep");
            word.Margin = new Thickness(6, 0, 0, 0);
            run.Children.Add(word);
            DockPanel.SetDock(run, Dock.Right);
            row.Children.Add(run);
        }
        if (cmd.Trailing is { } trailing)
        {
            var t = Rd.Figure(trailing, 13, FontWeights.Medium, cmd.TrailingAccent ? "RdAccentDeep" : "RdInk");
            t.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(t, Dock.Right);
            row.Children.Add(t);
        }
        var title = Highlighted(cmd.Title, cmd.Category == Category.Actions ? "" : Query);
        DockPanel.SetDock(title, Dock.Left);
        row.Children.Add(title);
        if (cmd.KindTag is { } tag)
        {
            var b = Rd.Tag(tag);
            b.Margin = new Thickness(14, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(b, Dock.Left);
            row.Children.Add(b);
        }
        var sub = Rd.Figure(cmd.Subtitle, 12, null, "RdInk3");
        sub.Margin = new Thickness(14, 0, 0, 0);
        row.Children.Add(sub);

        var layers = new Grid();
        layers.Children.Add(row);
        if (on) layers.Children.Add(new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Brush("AppAccent") });
        var host = new Border
        {
            Height = 52, CornerRadius = new CornerRadius(8), Child = layers, Cursor = Cursors.Hand,
            Background = on ? Ui.Brush("RdAccentWash") : Brushes.Transparent, Tag = cmd.Key,
        };
        host.MouseEnter += (_, _) =>
        {
            int index = _items.FindIndex(i => i.Key == cmd.Key);
            if (index >= 0 && index != _highlighted) { _highlighted = index; Render(); }
        };
        host.MouseLeftButtonUp += (_, _) => Run(cmd);
        System.Windows.Automation.AutomationProperties.SetName(host, $"{cmd.Title}. {cmd.Subtitle}{(cmd.Trailing is { } tr ? ". " + tr : "")}");
        return host;
    }

    /// <summary>The title with every typed word marked (accent wash behind, accent-deep text).</summary>
    private static TextBlock Highlighted(string text, string query)
    {
        var block = Rd.Text("", 15, FontWeights.Medium);
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Contains(':') && !w.Contains('>') && !w.Contains('<')).ToList();
        var marked = new bool[text.Length];
        string lower = text.ToLowerInvariant();
        foreach (string w in words)
            for (int at = lower.IndexOf(w, StringComparison.Ordinal); at >= 0; at = lower.IndexOf(w, at + w.Length, StringComparison.Ordinal))
                for (int k = at; k < at + w.Length; k++) marked[k] = true;
        int i = 0;
        while (i < text.Length)
        {
            int j = i;
            while (j < text.Length && marked[j] == marked[i]) j++;
            var run = new System.Windows.Documents.Run(text[i..j]);
            if (marked[i]) { run.Background = Ui.Brush("RdAccentWash"); run.Foreground = Ui.Brush("RdAccentDeep"); }
            block.Inlines.Add(run);
            i = j;
        }
        return block;
    }

    /// <summary>Kbd: a key in a soft rounded box, mono 11.</summary>
    private static Border Kbd(string keys) => new()
    {
        Height = 18, Padding = new Thickness(5, 0, 5, 0), CornerRadius = new CornerRadius(4),
        Background = Ui.Brush("RdHover"), VerticalAlignment = VerticalAlignment.Center,
        Child = Rd.Figure(keys, 11, null, "RdInk3"),
    };

    private void ScrollToHighlighted()
    {
        if (_items.Count == 0) return;
        string key = _items[_highlighted].Key;
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var child in _list.Children.OfType<Border>())
                if (child.Tag as string == key) { child.BringIntoView(); break; }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---- Keys ----

    private void OnKey(object sender, KeyEventArgs e)
    {
        var current = _items.Count == 0 ? null : _items[_highlighted];
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Down:
                _highlighted = Math.Min(Math.Max(0, _items.Count - 1), _highlighted + 1);
                Render();
                e.Handled = true;
                break;
            case Key.Up:
                _highlighted = Math.Max(0, _highlighted - 1);
                Render();
                e.Handled = true;
                break;
            case Key.Escape:
                _close();
                e.Handled = true;
                break;
            case Key.Enter when ctrl && current?.Hit is { } hit:
                Explorer.Reveal(Model.PathOf(hit.NodeId));
                e.Handled = true;
                break;
            case Key.Enter when current is not null:
                Run(current);
                e.Handled = true;
                break;
            case Key.B when ctrl && current?.Hit is { } add:
                // The same single-item checks as everywhere else.
                Model.Stage(add.NodeId, "Find: " + add.Name);
                e.Handled = true;
                break;
        }
    }

    private void Run(Command cmd)
    {
        _close();
        cmd.Run();
    }

    // ---- Search ----

    private async Task UpdateSearch()
    {
        _search?.Cancel();
        string normalized = Query;
        if (normalized.Length == 0 || Model.Tree is not { } tree || Model.RootPath is not { } root || Model.Totals.Length != tree.Count)
        {
            _hits = [];
            _searching = false;
            Render();
            return;
        }
        var cts = _search = new CancellationTokenSource();
        _searching = true;
        Render();
        var totals = Model.Totals;
        var parsed = FileQuery.Parse(normalized, UserFolders.Home, root);
        int limit = parsed.Query.IsStructured ? 8 : 100;
        var context = new FileQuery.Context(DuplicateFileIDs: Model.DuplicateFileIDs);
        try
        {
            var (result, hits) = await Task.Run(() =>
            {
                var r = parsed.Query.Run(tree, root, totals, context, limit: limit, isCancelled: () => cts.IsCancellationRequested);
                var h = r.Ids.Select(id =>
                {
                    string name = tree.NameOf(id);
                    bool dir = tree.IsDirectory[id];
                    return new Hit(id, tree.Parent[id], name, CanonicalPath.DisplayPath(tree.PathOf(id, root)), dir,
                        !dir && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && CanonicalPath.DisplayPath(tree.PathOf(id, root)).Contains("Program Files", StringComparison.OrdinalIgnoreCase),
                        totals[id]);
                }).ToList();
                return (r, h);
            }, cts.Token);
            if (cts.IsCancellationRequested || result.WasCancelled) return;
            _hits = hits;
            _matchCount = result.MatchCount;
            _matchedBytes = result.MatchedBytes;
            _searching = false;
            Render();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The folder a hit sits in ("~\AppData\Local").</summary>
    private static string ParentPath(string displayPath)
    {
        int cut = displayPath.LastIndexOfAny(['\\', '/']);
        return cut <= 0 ? displayPath : displayPath[..cut];
    }
}
