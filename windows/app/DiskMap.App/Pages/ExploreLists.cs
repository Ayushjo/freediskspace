using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// The Explore list kit (Mac: Kit/ListKit.swift): column widths, filter chips, the "Not
/// changed in" dropdown, the token search field with the hard shadow, and the two-line row
/// with a "can be freed" note and an add button.
/// </summary>
/// <summary>
/// A list's scroll position across rebuilds: every rebuild makes a new ScrollViewer, which
/// would start at the top. A different key (a new search, another folder) does start there.
/// </summary>
internal sealed class ScrollMemory
{
    private double _offset;
    private string? _key;

    public ScrollViewer Wrap(UIElement list, string key)
    {
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Track(scroll, key);
        return scroll;
    }

    /// <summary>Keeps <paramref name="scroll"/>'s offset under <paramref name="key"/> and brings it back once it's loaded.</summary>
    public void Track(ScrollViewer scroll, string key)
    {
        if (key != _key) { _key = key; _offset = 0; }
        double restore = _offset;
        bool restored = restore <= 0;
        scroll.ScrollChanged += (_, e) =>
        {
            if (!restored) return;   // the first layout reports 0 before the old offset is back
            _offset = e.VerticalOffset;
        };
        scroll.Loaded += (_, _) =>
        {
            if (restored) return;
            scroll.ScrollToVerticalOffset(restore);
            scroll.Dispatcher.InvokeAsync(() => restored = true, System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }
}

/// <summary>
/// A list row as a real Button: screen readers read its name, a press selects it, a double-click
/// opens it, and the page's keys (↑↓, Enter, Delete, Esc) act on it. A selection change repaints
/// it in place (<see cref="Paint"/>), so the row with the keyboard keeps it.
/// </summary>
internal sealed class ListRow : Button
{
    private readonly Action? _open;
    private bool _selected;

    public ListRow(UIElement content, string name, Action select, Action? open)
    {
        _open = open;
        Content = content;
        Template = Rd.PlainTemplate(0, row: true);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        ClickMode = ClickMode.Press;   // selects on press, as the rows always did
        System.Windows.Automation.AutomationProperties.SetName(this, name);
        Click += (_, _) => select();
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && _open is not null && !InsideNestedButton(e.OriginalSource as DependencyObject))
            {
                _open();
                e.Handled = true;
            }
        };
        MouseEnter += (_, _) => Background = Hover;
        MouseLeave += (_, _) => Background = Rest;
    }

    /// <summary>Sets the row's look for a selection state: <see cref="Rest"/>, <see cref="Hover"/>, marks.</summary>
    public Action<bool>? Paint { get; set; }

    public Brush Rest { get; set; } = Brushes.Transparent;
    public Brush Hover { get; set; } = Brushes.Transparent;

    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Paint?.Invoke(value);
            Background = IsMouseOver ? Hover : Rest;
            System.Windows.Automation.AutomationProperties.SetItemStatus(this, value ? "Selected" : "");
        }
    }

    /// <summary>Enter opens a folder where no page key claims it first (the lists' Enter reveals).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _open is not null) { _open(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    /// <summary>True for a press on the row's own add button, which handles its own clicks.</summary>
    private bool InsideNestedButton(DependencyObject? from)
    {
        for (var node = from; node is not null && node != this; node = node is Visual ? VisualTreeHelper.GetParent(node) : null)
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }
}

/// <summary>
/// A virtualized list (Mac: LazyVStack): rows are built only as they scroll into view, and a
/// selection change repaints the rows on screen in place. The list pages used to build up to
/// 500 hand-made rows of about 30 elements each, twice, on every click and arrow key.
/// </summary>
internal sealed class LazyRows : ItemsControl
{
    private readonly Func<object, bool> _isSelected;
    private readonly Func<object, bool, FrameworkElement> _build;
    private readonly ScrollMemory? _memory;
    private readonly string _memoryKey;
    private readonly Dictionary<ContentPresenter, object> _live = [];

    private static readonly ItemsPanelTemplate VirtualPanel = new(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));

    private static readonly ControlTemplate ListTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
<ControlTemplate TargetType=""ItemsControl""
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <ScrollViewer x:Name=""Scroll"" Focusable=""False"" CanContentScroll=""True""
                VerticalScrollBarVisibility=""Auto"" HorizontalScrollBarVisibility=""Disabled"">
    <ItemsPresenter/>
  </ScrollViewer>
</ControlTemplate>");

    /// <param name="items">The rows' items; anything <paramref name="build"/> knows, e.g. a closing "+N more" line.</param>
    /// <param name="memory">Brings the scroll position back across page rebuilds under the same <paramref name="memoryKey"/>.</param>
    public LazyRows(System.Collections.IList items, Func<object, bool> isSelected, Func<object, bool, FrameworkElement> build,
        string name, ScrollMemory? memory = null, string memoryKey = "")
    {
        _isSelected = isSelected;
        _build = build;
        _memory = memory;
        _memoryKey = memoryKey;
        ItemsPanel = VirtualPanel;
        Template = ListTemplate;
        Focusable = false;
        VirtualizingPanel.SetIsVirtualizing(this, true);
        VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(this, true);
        // One Tab stop (WPF returns to the row that last had focus); the page's arrow keys move between rows.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        System.Windows.Automation.AutomationProperties.SetName(this, name);
        ItemsSource = items;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_memory is not null && GetTemplateChild("Scroll") is ScrollViewer scroll) _memory.Track(scroll, _memoryKey);
    }

    protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();

    protected override bool IsItemItsOwnContainerOverride(object item) => false;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        var host = (ContentPresenter)element;
        bool selected = _isSelected(item);
        host.Content = _build(item, selected);
        _live[host] = item;
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        base.ClearContainerForItemOverride(element, item);
        var host = (ContentPresenter)element;
        host.Content = null;
        _live.Remove(host);
    }

    /// <summary>
    /// Repaints the rows on screen whose selection changed. With <paramref name="reveal"/> (an
    /// arrow-key move or a click), that item's row also scrolls into view and takes the keyboard,
    /// so screen readers announce it.
    /// </summary>
    public void Refresh(object? reveal = null)
    {
        foreach (var (host, item) in _live)
        {
            if (host.Content is not ListRow row) continue;
            bool selected = _isSelected(item);
            if (selected != row.Selected) row.Selected = selected;
        }
        if (reveal is null || Items.IndexOf(reveal) is not (>= 0 and var index)) return;
        if (ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter { Content: FrameworkElement shown })
        {
            // Not realized yet (scrolled far away): the panel makes it, then it can take focus.
            var panel = _live.Keys.Select(VisualTreeHelper.GetParent).OfType<VirtualizingPanel>().FirstOrDefault();
            panel?.BringIndexIntoViewPublic(index);
            if (ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter { Content: FrameworkElement made }) return;
            shown = made;
        }
        shown.BringIntoView();
        shown.Focus();
    }
}

internal static class ListKit
{
    private static ScanModel Model => ScanModel.Shared;

    /// <summary>Column widths for the list tables, by window width.</summary>
    public sealed record Columns(double Kind, double Bar, double Size, double Changed, bool ShowFree, double Free,
        double FreeMax, double Add, double RowHeight)
    {
        public static Columns For(Rd.WidthClass w) => w switch
        {
            Rd.WidthClass.Compact => new(110, 70, 84, 118, false, 0, 0, 40, 46),
            Rd.WidthClass.Regular => new(130, 100, 84, 130, true, 200, 280, 136, 50),
            _ => new(150, 120, 84, 140, true, 260, 380, 140, 54),
        };
    }

    /// <summary>A filter chip, "Video 214": card fill, radius 7, 28 tall; on shows an ink border.</summary>
    public static Border Chip(string title, int? count, bool isOn, Action action, bool removable = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Rd.Text(title, 12.5, isOn ? FontWeights.Medium : FontWeights.Normal, isOn ? "RdInk" : "RdInk2"));
        if (count is { } n)
        {
            var c = Rd.Figure(n.ToString("N0"), 10.5, FontWeights.Medium, isOn ? "RdInk" : "RdInk3");
            c.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(c);
        }
        if (removable)
        {
            var x = Ui.Glyph(Icons.Cancel, 9, Ui.Brush(isOn ? "RdInk" : "RdInk2"));
            x.Margin = new Thickness(6, 0, 0, 0);
            x.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(x);
        }
        var chip = Rd.Pressable(row, count is { } m ? $"{title} {m}" : title, action, 7);
        chip.Height = 28;
        chip.Padding = new Thickness(10, 0, 10, 0);
        chip.Background = Ui.Brush("RdCard");
        chip.BorderBrush = Ui.Brush(isOn ? "RdInk" : "RdLine");
        chip.BorderThickness = new Thickness(1);
        if (isOn) System.Windows.Automation.AutomationProperties.SetItemStatus(chip, "On");
        return Rd.ButtonHost(chip);
    }

    /// <summary>"Not changed in  Any time ⌄" — a chip-styled menu.</summary>
    public static Border Dropdown<T>(string label, IReadOnlyList<T> options, T selection, Func<T, string> title, Action<T> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Rd.Text(label, 12.5, null, "RdInk3"));
        var value = Rd.Text(title(selection), 12.5, FontWeights.Medium);
        value.Margin = new Thickness(8, 0, 8, 0);
        row.Children.Add(value);
        var chevron = Ui.Glyph(Icons.ChevronDown, 9, Ui.Brush("RdInk3"));
        chevron.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(chevron);
        Border? box = null;
        var button = Rd.Pressable(row, $"{label} {title(selection)}", () =>
        {
            if (box is not null) OpenMenu(box, options.Select(o => (title(o), EqualityComparer<T>.Default.Equals(o, selection), (Action)(() => pick(o)))));
        }, 7);
        button.Height = 28;
        button.Padding = new Thickness(10, 0, 10, 0);
        button.Background = Ui.Brush("RdCard");
        button.BorderBrush = Ui.Brush("RdLine");
        button.BorderThickness = new Thickness(1);
        box = Rd.ButtonHost(button);
        return box;
    }

    /// <summary>A menu under <paramref name="anchor"/>, the current choice ticked (Down and Enter work in it).</summary>
    public static void OpenMenu(FrameworkElement anchor, IEnumerable<(string Title, bool Current, Action Pick)> options)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var (text, current, choose) in options)
        {
            var item = new MenuItem { Header = (current ? "✓  " : "     ") + text };
            System.Windows.Automation.AutomationProperties.SetName(item, current ? $"{text}, current" : text);
            item.Click += (_, _) => choose();
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>
    /// The token search: an ink box with a hard 2,2 shadow, mono 13 text, what it understood as
    /// violet tags, "N MATCH", and a clear button.
    /// </summary>
    public static FrameworkElement SearchField(string text, ListQuery parsed, int? matchCount, string placeholder,
        Action<string> onChange, out TextBox input)
    {
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var box = new TextBox
        {
            Text = text, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            FontFamily = mono, FontSize = Ui.Scaled(13), FontWeight = FontWeights.Medium,
            Foreground = Ui.Brush("RdInk"), CaretBrush = Ui.Brush("RdInk"),
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Search the list");
        System.Windows.Automation.AutomationProperties.SetHelpText(box, placeholder);
        var hint = new TextBlock
        {
            Text = placeholder, FontFamily = mono, FontSize = Ui.Scaled(13), Foreground = Ui.Brush("RdInk3"),
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            onChange(box.Text);
        };
        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(box);

        var row = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
        var glass = Ui.Glyph(Icons.Search, 12, Ui.Brush("RdInk2"));
        glass.Margin = new Thickness(0, 0, 8, 0);
        glass.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(glass, Dock.Left);
        row.Children.Add(glass);
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (string token in parsed.Tokens.Take(3))
        {
            var tag = Rd.Tag(token, Rd.TagTone.Accent);
            tag.Margin = new Thickness(8, 0, 0, 0);
            trailing.Children.Add(tag);
        }
        if (matchCount is { } m)
        {
            var count = Rd.Figure($"{m:N0} match".ToUpperInvariant(), 10.5, FontWeights.Medium, "RdInk3");
            count.Margin = new Thickness(8, 0, 0, 0);
            trailing.Children.Add(count);
        }
        if (text.Length > 0)
        {
            var clear = Rd.IconButton(Icons.Cancel, "Clear search", () => { box.Text = ""; }, 22);
            clear.Margin = new Thickness(6, 0, 0, 0);
            trailing.Children.Add(clear);
        }
        DockPanel.SetDock(trailing, Dock.Right);
        row.Children.Add(trailing);
        row.Children.Add(field);

        var face = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(8), Background = Ui.Brush("RdCard"),
            BorderBrush = Ui.Brush("RdInk"), BorderThickness = new Thickness(1), Child = row,
        };
        var shadow = new Border { CornerRadius = new CornerRadius(8), Background = Ui.Brush("RdShadow"), Margin = new Thickness(2, 2, -2, -2) };
        var stack = new Grid();
        stack.Children.Add(shadow);
        stack.Children.Add(face);
        input = box;
        return stack;
    }

    public enum NoteKind { None, Accent, Review, Plain }

    /// <summary>The "Can be freed" column's note.</summary>
    public sealed record Note(NoteKind Kind, string? Text = null)
    {
        public static readonly Note None = new(NoteKind.None);
    }

    /// <summary>Rebuilds by itself (violet), look first (with what to do instead), or nothing.</summary>
    public static Note FreeNote(bool freeable, long bytes, StorageVerdict verdict)
    {
        if (freeable) return new Note(NoteKind.Accent, $"{ShellFormat.HeroBytes(bytes)} rebuilds by itself");
        if (verdict.Advice is CleanupAdvice.Warn or CleanupAdvice.Never) return new Note(NoteKind.Review, verdict.Instead);
        return Note.None;
    }

    /// <summary>
    /// One list row: type tile, name over place, kind, bar and size, last changed, can be
    /// freed, add. Selected rows get the accent wash and a 2 pt accent edge.
    /// </summary>
    /// <summary>Set by an arrow-key move: the next selected row brings itself into view.</summary>
    public static bool RevealSelection;

    public static FrameworkElement FileRow(string name, string place, string badge, string kind, double fraction, long bytes,
        string changed, Note note, bool accent, bool staged, bool selected, Columns columns,
        Action select, Action? add, Action? open = null)
    {
        var grid = new Grid();
        void col(GridLength w) => grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        col(new GridLength(1, GridUnitType.Star));          // tile + names
        col(new GridLength(columns.Kind));                  // kind
        col(new GridLength(columns.Bar + columns.Size));    // bar + size
        col(new GridLength(columns.Changed + 24));          // last changed
        if (columns.ShowFree) col(new GridLength(columns.Free + (columns.FreeMax - columns.Free) / 2));
        col(new GridLength(columns.Add + 8));               // add

        var tile = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7),
            Background = accent ? Ui.Brush("RdAccentWash") : Ui.Brush("RdCard"),
            BorderBrush = accent ? Rd.WithOpacity(Ui.Brush("AppAccent"), 0.45) : Ui.Brush("RdLine"),
            BorderThickness = new Thickness(1),
            Child = new Viewbox
            {
                StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(2),
                Child = Rd.Figure(badge, 8.5, FontWeights.Medium, accent ? "RdAccentDeep" : "RdInk2"),
            },
        };
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var title = Rd.Text(name, 13, FontWeights.Medium);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        names.Children.Add(title);
        var where = Rd.Figure(place, 11, null, "RdInk3");
        where.Margin = new Thickness(0, 1, 0, 0);
        names.Children.Add(where);
        var first = new DockPanel { Margin = new Thickness(12, 0, 12, 0) };
        DockPanel.SetDock(tile, Dock.Left);
        first.Children.Add(tile);
        first.Children.Add(names);
        grid.Children.Add(first);

        var kindText = Rd.Text(kind, 13, null, "RdInk2");
        Grid.SetColumn(kindText, 1);
        grid.Children.Add(kindText);

        var sizeCell = new DockPanel();
        var size = Rd.Figure(ByteFormat.Format(bytes), 12);
        size.Width = columns.Size - 10;
        size.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(size, Dock.Right);
        sizeCell.Children.Add(size);
        var bar = new Grid { Height = 4, Width = columns.Bar, Margin = new Thickness(0, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Right };
        bar.Children.Add(new Border { Background = Ui.Brush("RdSoft") });
        bar.Children.Add(new Border
        {
            Background = accent ? Ui.Brush("AppAccent") : Ui.Brush("RdInk"), HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(2, columns.Bar * Math.Min(1, fraction)),
        });
        sizeCell.Children.Add(bar);
        Grid.SetColumn(sizeCell, 2);
        grid.Children.Add(sizeCell);

        var changedText = Rd.Text(changed, 13, null, "RdInk2");
        changedText.Margin = new Thickness(24, 0, 0, 0);
        Grid.SetColumn(changedText, 3);
        grid.Children.Add(changedText);

        int next = 4;
        if (columns.ShowFree)
        {
            var noteView = NoteView(note);
            Grid.SetColumn(noteView, next++);
            grid.Children.Add(noteView);
        }
        var addHost = new ContentControl { Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(addHost, next);
        grid.Children.Add(addHost);
        foreach (FrameworkElement child in grid.Children) child.VerticalAlignment = VerticalAlignment.Center;

        var edge = new Border { Width = staged ? 3 : 2, HorizontalAlignment = HorizontalAlignment.Left };
        var layers = new Grid();
        layers.Children.Add(grid);
        layers.Children.Add(edge);
        var host = new ListRow(layers, $"{name}, {ByteFormat.Format(bytes)}, changed {changed}{(staged ? ", in the basket" : "")}", select, open)
        {
            Height = columns.RowHeight,
            BorderBrush = Ui.Brush("RdLine"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        host.Paint = on =>
        {
            host.Rest = staged ? Rd.InBasketFill : on ? Ui.Brush("RdAccentWash") : Brushes.Transparent;
            host.Hover = staged ? Rd.InBasketHover : on ? host.Rest : Ui.Brush("RdHover");
            edge.Background = on || staged ? Ui.Brush("AppAccent") : Brushes.Transparent;
            addHost.Content = AddView(name, staged, on, columns, add);
        };
        host.Selected = selected;
        // A list rebuilt after an arrow-key move brings its new selected row into view once.
        if (selected && RevealSelection)
            host.Loaded += (_, _) =>
            {
                RevealSelection = false;
                host.Dispatcher.InvokeAsync(host.BringIntoView, System.Windows.Threading.DispatcherPriority.Loaded);
            };
        return host;
    }

    private static FrameworkElement NoteView(Note note)
    {
        switch (note.Kind)
        {
            case NoteKind.Accent:
                return Rd.Text(note.Text ?? "", 13, null, "RdAccentDeep");
            case NoteKind.Plain:
                var plain = Rd.Text(note.Text ?? "", 12, null, "RdInk2");
                plain.ToolTip = note.Text;
                return plain;
            case NoteKind.Review:
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(Rd.Tag("Review"));
                if (note.Text is { } instead)
                {
                    var t = Rd.Text(instead.Replace("`", ""), 12, null, "RdInk2");
                    t.Margin = new Thickness(8, 0, 0, 0);
                    row.Children.Add(t);
                }
                row.ToolTip = note.Text is { } i ? "Instead: " + i.Replace("`", "") : "Look before you remove this";
                return row;
            default:
                return Rd.Text("—", 13, null, "RdInk3");
        }
    }

    private static FrameworkElement AddView(string name, bool staged, bool selected, Columns columns, Action? add)
    {
        if (add is null)
        {
            var kept = Rd.Tag("Kept");
            kept.ToolTip = "Windows or your account needs this, so it can't go in the basket";
            return kept;
        }
        if (staged) return new Border { Width = 26 };   // the violet row says it; no button to press twice
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (selected && columns.ShowFree)
        {
            var t = Rd.Text("Add to basket", 13, null, "RdAccentDeep");
            t.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(t);
        }
        var plus = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(7),
            Background = selected ? Ui.Brush("AppAccent") : Brushes.Transparent,
            Child = new Border
            {
                Child = Ui.Glyph(Icons.Add, 12, selected ? Ui.Brush("RdOnAccent") : Ui.Brush("RdInk3")),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(plus);
        var button = Rd.Pressable(row, $"Add {name} to basket", add, 7);
        button.ToolTip = "Add to basket";
        // The row keeps the keyboard (Delete adds it); a press here mustn't take it.
        button.Focusable = false;
        return button;
    }

    /// <summary>Folders | Files | Forgotten: the Explore list's three ways to look.</summary>
    public static Border PageSwitch(string current) => Rd.SegmentSwitch(
    [
        new Rd.Segment("Folders", current is "Biggest Folders" or "File Browser", () => Model.ShowPage("Biggest Folders")),
        new Rd.Segment("Files", current == "Biggest Files", () => Model.ShowPage("Biggest Files")),
        new Rd.Segment("Forgotten", current == "Forgotten Files", () => Model.ShowPage("Forgotten Files")),
    ]);

    /// <summary>The tile's short type ("MOV", "ZIP", "ISO").</summary>
    public static string Badge(string name, FileKind kind)
    {
        int dot = name.LastIndexOf('.');
        string ext = dot > 0 && dot < name.Length - 1 && !name[(dot + 1)..].Contains(' ') ? name[(dot + 1)..].ToUpperInvariant() : "";
        if (ext.Length > 0 && ext.Length <= 4) return ext;
        return kind switch
        {
            FileKind.Video => "VID",
            FileKind.DiskImage => "IMG",
            FileKind.Archive => "ZIP",
            FileKind.Application => "APP",
            FileKind.Document => "DOC",
            FileKind.Database => "DB",
            FileKind.VirtualDisk => "VM",
            FileKind.DeviceBackup => "BAK",
            _ => ext.Length == 0 ? "FILE" : ext[..3],
        };
    }

    /// <summary>"Disk image", "Video": the type in sentence case.</summary>
    public static string KindName(FileKind kind) => kind switch
    {
        FileKind.DiskImage => "Disk image",
        FileKind.VirtualDisk => "Virtual disk",
        FileKind.DeviceBackup => "Device backup",
        _ => kind.Title(),
    };

    /// <summary>The Kind column: the file type, or the storage class when the type says nothing.</summary>
    public static string KindText(FileKind kind, StorageClass storageClass) =>
        kind != FileKind.Other ? KindName(kind) : storageClass.Id == "other" ? "File" : storageClass.Title;

    public static string ChipTitle(FileKind kind) => kind switch
    {
        FileKind.Video => "Video",
        FileKind.Archive => "Archives",
        FileKind.DiskImage => "Disk images",
        FileKind.Application => "Apps",
        FileKind.Document => "Documents",
        FileKind.Database => "Databases",
        FileKind.VirtualDisk => "Virtual disks",
        FileKind.DeviceBackup => "Backups",
        _ => "Other",
    };

    // ---- Shared by Folders, Forgotten and Find (WIN-107) ----

    /// <summary>The token search with tokens the caller picked (Find shows every key: and comparison).</summary>
    public static FrameworkElement SearchField(string text, IReadOnlyList<string> tokens, int? matchCount, string placeholder,
        string accessibleName, Action<string> onChange, out TextBox input)
    {
        var field = SearchField(text, new ListQuery { Tokens = [.. tokens] }, matchCount, placeholder, onChange, out input);
        System.Windows.Automation.AutomationProperties.SetName(input, accessibleName);
        return field;
    }

    /// <summary>DiskMapEmptyState: a 20 pt ink3 symbol, the title (13 medium), the message (12 ink2, 420 wide), buttons.</summary>
    public static FrameworkElement EmptyState(string icon, string title, string message, params FrameworkElement[] buttons)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        var glyph = Ui.Glyph(icon, 20, Ui.Brush("RdInk3"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(glyph);
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
        if (buttons.Length > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            foreach (var b in buttons) { b.Margin = new Thickness(row.Children.Count > 0 ? 8 : 0, 0, 0, 0); row.Children.Add(b); }
            stack.Children.Add(row);
        }
        return stack;
    }

    /// <summary>DiskMapLoadingState (the catalog gate): a busy bar, the title, the detail.</summary>
    public static FrameworkElement LoadingState(string title, string detail)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4, HorizontalAlignment = HorizontalAlignment.Center });
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

    /// <summary>The table's column header: 30 tall, secondary ink3 labels, a 1 px ink @ 85% rule below.</summary>
    public static FrameworkElement ColumnHeader(Columns columns, string kind, string size, bool sizeOn, string changed, string? free)
    {
        var header = new Grid();
        void hcol(GridLength w) => header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        hcol(new GridLength(1, GridUnitType.Star)); hcol(new GridLength(columns.Kind)); hcol(new GridLength(columns.Bar + columns.Size));
        hcol(new GridLength(columns.Changed + 24));
        if (columns.ShowFree) hcol(new GridLength(columns.Free + (columns.FreeMax - columns.Free) / 2));
        hcol(new GridLength(columns.Add + 8));
        void head(string text, int c, string color = "RdInk3", bool right = false, double left = 0)
        {
            var t = Rd.Text(text, 12, null, color);
            t.Margin = new Thickness(left, 0, 0, 0);
            if (right) t.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(t, c);
            header.Children.Add(t);
        }
        head("Name", 0, left: 12); head(kind, 1); head(size, 2, sizeOn ? "RdInk" : "RdInk3", right: true); head(changed, 3, left: 24);
        if (columns.ShowFree && free is not null) head(free, 4);
        return new Border
        {
            Height = 30, Child = header, BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>The footer bar: mono 12 ink2 text, 10 above and below, a 1 px ink @ 85% rule on top.</summary>
    public static Border FooterBar(UIElement text, params FrameworkElement[] trailing)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var t in trailing)
        {
            t.Margin = new Thickness(14, 0, 0, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            buttons.Children.Add(t);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(text);
        return new Border { Child = dock, BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    /// <summary>A footer line of mono 12 runs, each with its own colour; trims at the end.</summary>
    public static TextBlock FooterText(params (string Text, string Color)[] runs)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var mono = new FontFamily("Cascadia Mono, Consolas");
        foreach (var (s, color) in runs)
            text.Inlines.Add(new System.Windows.Documents.Run(s) { FontFamily = mono, FontSize = Ui.Scaled(12), Foreground = Ui.Brush(color) });
        return text;
    }

    /// <summary>Selected nodes with no selected ancestor: a folder and something inside it are one item.</summary>
    public static List<int> SelectionRoots(FileTree tree, IReadOnlyCollection<int> selection, long[] totals) =>
        selection.Where(id =>
        {
            if (id < 0 || id >= tree.Count) return false;
            for (int p = tree.Parent[id]; p >= 0; p = tree.Parent[p])
                if (selection.Contains(p)) return false;
            return true;
        }).OrderByDescending(id => id < totals.Length ? totals[id] : 0).ToList();

    /// <summary>
    /// The selection a page may act on: the selection is app-wide and outlives pages and filters,
    /// so only what this page lists right now counts.
    /// </summary>
    public static HashSet<int> VisibleSelection(IReadOnlyCollection<int> selection, IEnumerable<int> visible) =>
        visible.Where(selection.Contains).ToHashSet();

    /// <summary>
    /// NodeSelectionToolbar: once anything is Ctrl-selected, "N selected  size  Clear", then
    /// Show in File Explorer, Copy Paths and Add to basket — 48 tall on the card colour.
    /// Null when nothing is selected. With <paramref name="visible"/>, only the selected items
    /// the page lists count (hidden ones were added under this page's name).
    /// </summary>
    public static FrameworkElement? SelectionToolbar(string pageLabel, IEnumerable<int>? visible = null)
    {
        if (Model.MultiSelection.Count == 0 || Model.Tree is not { } tree) return null;
        IReadOnlyCollection<int> selection = visible is null ? Model.MultiSelection : VisibleSelection(Model.MultiSelection, visible);
        if (selection.Count == 0) return null;
        var roots = SelectionRoots(tree, selection, Model.Totals);
        long bytes = roots.Sum(id => id < Model.Totals.Length ? Model.Totals[id] : 0);
        var paths = roots.Select(Model.PathOf).ToList();
        var dock = new DockPanel { Margin = new Thickness(16, 0, 16, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var reveal = Rd.IconButton(Icons.Open, "Show in File Explorer", () => { foreach (var p in paths.Take(1)) Explorer.Reveal(p); });
        right.Children.Add(reveal);
        var copy = Rd.IconButton(Icons.Copy, "Copy Paths", () =>
        {
            try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { }
        });
        copy.ToolTip = "Copy the selected paths, one per line";
        copy.Margin = new Thickness(2, 0, 0, 0);
        right.Children.Add(copy);
        var add = Rd.AccentButton("Add to basket", () =>
        {
            int added = Model.ConfirmStageMany(roots, $"Selected in {pageLabel}", $"Selected in {pageLabel}");
            if (added > 0) Model.ClearMulti();
        });
        add.Margin = new Thickness(8, 0, 0, 0);
        right.Children.Add(add);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Rd.Text($"{roots.Count:N0} selected", 13, FontWeights.Medium));
        var size = Rd.Figure(ByteFormat.Format(bytes), 12, null, "RdInk2");
        size.Margin = new Thickness(8, 0, 0, 0);
        left.Children.Add(size);
        var clear = Rd.Text("Clear", 13, FontWeights.Medium, "RdInk2");
        var clearBox = Rd.Pressable(clear, "Clear selection", Model.ClearMulti, 6);
        clearBox.Height = 28;
        clearBox.Padding = new Thickness(8, 0, 8, 0);
        clearBox.Margin = new Thickness(8, 0, 0, 0);
        clearBox.MouseEnter += (_, _) => { clearBox.Background = Ui.Brush("RdHover"); clear.Foreground = Ui.Brush("RdInk"); };
        clearBox.MouseLeave += (_, _) => { clearBox.Background = Brushes.Transparent; clear.Foreground = Ui.Brush("RdInk2"); };
        left.Children.Add(clearBox);
        dock.Children.Add(left);
        return new Border
        {
            Height = 48, Background = Ui.Brush("RdCard"), Child = dock,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0),
        };
    }

    /// <summary>Up/Down or J/K, Delete stages, Enter reveals, Ctrl+A selects all, Esc clears (Mac: listKeyboard).</summary>
    public static bool ListKey(KeyEventArgs e, IReadOnlyList<int> ids, ref int? selected, Action<int> stage,
        Func<int, string?> path, Action selectAll)
    {
        // A row (a button too) takes these keys; the search box and other buttons keep theirs.
        if (!TakesListKeys(e.OriginalSource) || ids.Count == 0) return false;
        int? current = selected;
        int index = current is { } s ? ids.ToList().IndexOf(s) : -1;
        switch (e.Key)
        {
            case Key.Down or Key.J when Keyboard.Modifiers == ModifierKeys.None:
                selected = ids[Math.Min(ids.Count - 1, index + 1)];
                Model.Select(selected.Value);
                RevealSelection = true;
                return true;
            case Key.Up or Key.K when Keyboard.Modifiers == ModifierKeys.None:
                selected = ids[Math.Max(0, index - 1)];
                Model.Select(selected.Value);
                RevealSelection = true;
                return true;
            case Key.Delete when index >= 0:
                stage(ids[index]);
                return true;
            case Key.Enter when index >= 0:
                if (path(ids[index]) is { } p) Explorer.Reveal(p);
                return true;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                selectAll();
                return true;
            case Key.Escape:
                Model.ClearMulti();
                return true;
        }
        return false;
    }

    /// <summary>The list's keys apply from the page or a row, never from the search box or another button.</summary>
    public static bool TakesListKeys(object? source) =>
        source is not TextBox && (source is not System.Windows.Controls.Primitives.ButtonBase || source is ListRow);
}

/// <summary>
/// WIN-107: Explore → List → Files (Mac: BiggestFilesView, redesign board 05): the largest files
/// in the scan, filtered by kind, age and a token search, each one a click from the basket.
/// </summary>
public sealed class BiggestFilesExplorePage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    /// <summary>A storage class Overview asked this page to filter by when it opens next.</summary>
    public static string? PendingClassFilter { get; set; }

    public enum AgeFilter { Any = 0, ThreeMonths = 90, SixMonths = 180, Year = 365, TwoYears = 730 }

    public static string AgeTitle(AgeFilter a) => a switch
    {
        AgeFilter.ThreeMonths => "3 months",
        AgeFilter.SixMonths => "6 months",
        AgeFilter.Year => "A year",
        AgeFilter.TwoYears => "2 years",
        _ => "Any time",
    };

    public sealed record Entry(int Id, string Name, string AbsolutePath, string Parent, FileKind Kind, long Size,
        int ModifiedDay, string SearchText, string FolderText, StorageVerdict Category);

    /// <summary>Kinds that get a chip, in board order. Everything else is only searchable.</summary>
    public static readonly FileKind[] ChipKinds =
        [FileKind.Video, FileKind.Archive, FileKind.DiskImage, FileKind.Application, FileKind.Document, FileKind.Database, FileKind.VirtualDisk];

    private string _query = "";
    private FileKind? _kind;
    private AgeFilter _age = AgeFilter.Any;
    private string? _category;
    private int? _selected;
    private readonly ScrollMemory _scroll = new();
    private List<Entry> _entries = [];
    private Dictionary<FileKind, int> _kindCounts = [];
    private object? _rankedFor;
    private bool _preparing = true;
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private readonly Grid _root = new();
    private TextBox? _search;
    private LazyRows? _rows;
    private List<Entry> _shown = [];
    private object? _chromeFor;

    public BiggestFilesExplorePage()
    {
        Background = Ui.Brush("RdPage");
        Content = _root;
        Focusable = true;
        ModelEvents.WhileLoaded(this, OnModelChanged);
        Loaded += (_, _) => Model.StagedChanged += OnStaged;
        Unloaded += (_, _) => Model.StagedChanged -= OnStaged;
        SizeChanged += (_, _) => { if (Rd.ClassFor(WindowWidth) != _class) Rebuild(); };
        PreviewKeyDown += OnKey;
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    /// <summary>What the page around the rows is built from; a change to anything else (a selection) only repaints rows.</summary>
    private static object ChromeKey() => (Model.Tree, Model.Totals, Model.Reviewables);

    private void OnModelChanged()
    {
        if (_rows is not null && Equals(_chromeFor, ChromeKey())) _rows.Refresh();
        else Rebuild();
    }

    private bool IsSelected(int id) => id == _selected || Model.MultiSelection.Contains(id);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    /// <summary>The list as shown: every filter applied, largest first.</summary>
    public static List<Entry> Filter(IEnumerable<Entry> entries, ListQuery query, FileKind? kind, AgeFilter age,
        string? category, int today) =>
        entries.Where(e =>
        {
            if (category is not null && e.Category.Class.Id != category) return false;
            if (kind is { } k && e.Kind != k) return false;
            int? ageDays = e.ModifiedDay > 0 ? today - e.ModifiedDay : null;
            if (age != AgeFilter.Any && !(ageDays is { } a && a >= (int)age)) return false;
            return query.Matches(e.SearchText, e.FolderText, e.Size, e.Kind, ageDays);
        }).OrderByDescending(e => e.Size).ToList();

    private async void Rank()
    {
        if (Model.Tree is not { } tree || Model.Totals.Length != tree.Count || Model.RootPath is not { } root) return;
        var totals = Model.Totals;
        _rankedFor = totals;
        _preparing = true;
        _selected = null;   // ranked again: the old ids may name other files now
        var (built, counts) = await Task.Run(() =>
        {
            var ids = TopSizes.Largest(1, tree.Count, 2_000, id => totals[id], id => !tree.IsDirectory[id] && totals[id] > 0);
            var kinds = new Dictionary<FileKind, int>();
            var list = new List<Entry>(ids.Count);
            foreach (int id in ids)
            {
                string name = tree.NameOf(id);
                string abs = tree.PathOf(id, root);
                var kind = FileKinds.Classify(name, abs);
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                string parent = RelativeParent(abs, root);
                list.Add(new Entry(id, name, abs, parent, kind, totals[id], tree.ModifiedDay[id],
                    name.ToLowerInvariant() + "\n" + CanonicalPath.DisplayPath(abs).ToLowerInvariant(),
                    parent.ToLowerInvariant(), StorageClassifier.Classify(abs, false)));
            }
            return (list, kinds);
        });
        if (!ReferenceEquals(_rankedFor, totals)) return;
        _entries = built;
        _kindCounts = counts;
        _preparing = false;
        Rebuild();
    }

    /// <summary>The containing folder relative to the scan ("Downloads\misc"); the root itself reads as "\".</summary>
    public static string RelativeParent(string absolutePath, string root)
    {
        string parent = System.IO.Path.GetDirectoryName(absolutePath) ?? "";
        string r = root.TrimEnd('\\', '/');
        if (parent.Equals(r, StringComparison.OrdinalIgnoreCase)) return "\\";
        if (parent.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return parent[(r.Length + 1)..];
        return CanonicalPath.DisplayPath(parent);
    }

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        if (Model.Tree is not { } tree) { Model.ShowPage("Overview"); return; }
        _chromeFor = ChromeKey();
        _debounce?.Stop();   // this rebuild already has the latest query
        if (!ReferenceEquals(_rankedFor, Model.Totals)) { Rank(); }
        if (PendingClassFilter is { } pending) { _category = pending; PendingClassFilter = null; }
        bool searchFocused = _search?.IsKeyboardFocusWithin == true;
        bool listFocused = _rows?.IsKeyboardFocusWithin == true;
        int caret = _search?.CaretIndex ?? 0;

        var parsed = ListQuery.Parse(_query);
        var visible = Filter(_entries, parsed, _kind, _age, _category, AgeMap.Today());
        double pad = _class.PagePadding();
        _rows = null;
        _shown = [];
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new StackPanel { Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, 10) };
        top.Children.Add(Header(tree));
        var filters = FilterRow();
        filters.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(filters);
        var search = SearchRow(parsed, visible.Count);
        search.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(search);
        _root.Children.Add(top);

        FrameworkElement body;
        if (_preparing) body = Empty("Finding biggest files", "Ranking the scan without blocking the window.");
        else if (visible.Count == 0)
            body = Empty(_entries.Count == 0 ? "No large files" : "No files match",
                _entries.Count == 0 ? "The scan found no files of note." : "Try another kind, or clear the search.");
        else body = Table(tree, visible, pad);
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        if (searchFocused && _search is not null)
        {
            _search.Focus();
            _search.CaretIndex = Math.Min(caret, _search.Text.Length);
        }
        else if (listFocused) Focus();   // the rebuilt list dropped its rows; the page keeps the keys
    }

    private static FrameworkElement Empty(string title, string message)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var t = Rd.Text(title, 15, FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(t);
        var m = Rd.Text(message, 13, null, "RdInk2");
        m.HorizontalAlignment = HorizontalAlignment.Center;
        m.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(m);
        return stack;
    }

    private FrameworkElement Header(FileTree tree)
    {
        var dock = new DockPanel();
        var views = ExplorePage.ViewSwitch(onList: true);
        DockPanel.SetDock(views, Dock.Right);
        dock.Children.Add(views);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Rd.PageTitle("Biggest files");
        var stats = Rd.Figure($"{_entries.Count:N0} largest · {ShellFormat.HeroBytes(_entries.Sum(e => e.Size))}", 12);
        row.Children.Add(Rd.BaselineRow(title, 26, stats, 12, 12));
        if (_class != Rd.WidthClass.Compact)
        {
            var lede = Rd.Text($"Every file in {(tree.Count > 0 ? tree.NameOf(0) : "the scan")}, sorted by size. Filter, then add what you want to the basket.", 12, null, "RdInk3");
            row.Children.Add(Rd.BaselineRow(new Border(), 26, lede, 12, 12));
        }
        dock.Children.Add(row);
        return dock;
    }

    private FrameworkElement FilterRow()
    {
        var dock = new DockPanel();
        var age = ListKit.Dropdown("Not changed in", Enum.GetValues<AgeFilter>(), _age, AgeTitle, a => { _age = a; Rebuild(); });
        DockPanel.SetDock(age, Dock.Right);
        dock.Children.Add(age);
        var chips = new WrapPanel();
        var pageSwitch = ListKit.PageSwitch("Biggest Files");
        pageSwitch.Margin = new Thickness(0, 0, 12, 0);
        chips.Children.Add(pageSwitch);
        void add(FrameworkElement chip) { chip.Margin = new Thickness(0, 0, 8, 0); chips.Children.Add(chip); }
        add(ListKit.Chip("All", _entries.Count, _kind is null, () => { _kind = null; Rebuild(); }));
        int limit = _class switch { Rd.WidthClass.Compact => 3, Rd.WidthClass.Regular => 5, _ => 7 };
        foreach (var kind in ChipKinds.Where(k => _kindCounts.GetValueOrDefault(k) > 0)
                     .OrderByDescending(k => _kindCounts.GetValueOrDefault(k)).Take(limit))
        {
            var captured = kind;
            add(ListKit.Chip(ListKit.ChipTitle(kind), _kindCounts.GetValueOrDefault(kind), _kind == kind,
                () => { _kind = _kind == captured ? null : captured; Rebuild(); }));
        }
        if (_category is { } category)
            add(ListKit.Chip(StorageClassifier.All.FirstOrDefault(c => c.Id == category)?.Title ?? category, null, true,
                () => { _category = null; Rebuild(); }, removable: true));
        dock.Children.Add(chips);
        return dock;
    }

    private static readonly (string Title, string Query)[] Suggestions =
    [
        ("Old installers", "kind:installer changed>6m"),
        ("Videos over 1 GB", "kind:video size>1GB"),
        ("Not changed in a year", "changed>1y"),
        ("In Downloads", "in:downloads"),
    ];

    private FrameworkElement SearchRow(ListQuery parsed, int matches)
    {
        var dock = new DockPanel();
        var field = ListKit.SearchField(_query, parsed, parsed.IsEmpty ? null : matches,
            "Search, or try size>1GB  kind:video  changed>1y  in:Downloads",
            text => { _query = text; DebouncedRebuild(); }, out var input);
        _search = input;
        if (_class != Rd.WidthClass.Compact) field.MaxWidth = 760;
        field.HorizontalAlignment = HorizontalAlignment.Left;
        if (_class != Rd.WidthClass.Compact || _query.Length == 0)
        {
            var tries = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            tries.Children.Add(Rd.Text("Try", 12, null, "RdInk3"));
            foreach (var (title, query) in _class == Rd.WidthClass.Compact ? Suggestions.Take(2) : Suggestions)
            {
                string captured = query;
                var link = Rd.Link(title, () => { _query = captured; Rebuild(); }, 12, "RdInk", FontWeights.Normal);
                link.TextDecorations = new TextDecorationCollection
                {
                    new TextDecoration(TextDecorationLocation.Underline, new Pen(Ui.Brush("RdLine"), 1), 1, TextDecorationUnit.Pixel, TextDecorationUnit.Pixel),
                };
                link.Margin = new Thickness(12, 0, 0, 0);
                tries.Children.Add(link);
            }
            DockPanel.SetDock(field, Dock.Left);
            dock.Children.Add(field);
            dock.Children.Add(tries);
        }
        else dock.Children.Add(field);
        return dock;
    }

    private FrameworkElement Table(FileTree tree, List<Entry> visible, double pad)
    {
        var columns = ListKit.Columns.For(_class);
        var rows = visible.Take(500).ToList();
        long largest = Math.Max(1, rows.FirstOrDefault()?.Size ?? 1);
        var freeable = new FreeableIndex(Model.Reviewables ?? [], Model.Totals);
        var staged = Model.StagedItems.Select(s => System.IO.Path.GetFullPath(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int today = AgeMap.Today();

        var grid = new Grid { Margin = new Thickness(pad, 0, pad, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        void hcol(GridLength w) => header.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        hcol(new GridLength(1, GridUnitType.Star)); hcol(new GridLength(columns.Kind)); hcol(new GridLength(columns.Bar + columns.Size));
        hcol(new GridLength(columns.Changed + 24));
        if (columns.ShowFree) hcol(new GridLength(columns.Free + (columns.FreeMax - columns.Free) / 2));
        hcol(new GridLength(columns.Add + 8));
        void head(string text, int c, string color = "RdInk3", bool right = false, double left = 0)
        {
            var t = Rd.Text(text, 12, null, color);
            t.Margin = new Thickness(left, 0, 0, 0);
            if (right) t.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(t, c);
            header.Children.Add(t);
        }
        head("Name", 0, left: 12); head("Kind", 1); head("Size ↓", 2, "RdInk", right: true); head("Last changed", 3, left: 24);
        if (columns.ShowFree) head("Can be freed", 4);
        grid.Children.Add(new Border
        {
            Child = header, BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(0, 0, 0, 1),
        });

        _shown = rows;
        _rows = new LazyRows(rows, item => item is Entry e && IsSelected(e.Id), (item, selected) =>
        {
            var e = (Entry)item;
            bool free = freeable.Contains(e.Id, tree);
            return ListKit.FileRow(e.Name, e.Parent, ListKit.Badge(e.Name, e.Kind), ListKit.KindText(e.Kind, e.Category.Class),
                (double)e.Size / largest, e.Size, ExplorePage.LastUsed(e.ModifiedDay, today),
                ListKit.FreeNote(free, e.Size, e.Category), free, staged.Contains(e.AbsolutePath), selected, columns,
                select: () => Select(e), add: () => Stage(e));
        }, "Biggest files", _scroll, $"{_query}|{_kind}|{_age}|{_category}");
        Grid.SetRow(_rows, 1);
        grid.Children.Add(_rows);

        var footer = Footer(tree, visible, freeable);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);
        return grid;
    }

    private FrameworkElement Footer(FileTree tree, List<Entry> visible, FreeableIndex index)
    {
        long shown = visible.Sum(v => v.Size);
        var freeable = visible.Where(v => index.Contains(v.Id, tree)).ToList();
        long freeBytes = freeable.Sum(f => f.Size);
        bool filtered = visible.Count != _entries.Count;
        var dock = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (_class != Rd.WidthClass.Compact)
        {
            var note = Rd.Text("Big app bundles and packages count as one file", 12, null, "RdInk3");
            note.Margin = new Thickness(0, 0, 14, 0);
            buttons.Children.Add(note);
        }
        if (visible.Count > 1)
        {
            int n = Math.Min(500, visible.Count);
            var all = Rd.SecondaryButton($"Select all {n:N0}", () =>
            {
                Model.ClearMulti();
                foreach (var v in visible.Take(500)) Model.ToggleMulti(v.Id);
                _rows?.Refresh();
            });
            all.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(all);
        }
        if (freeBytes > 0)
            buttons.Children.Add(Rd.SecondaryButton($"Add {ShellFormat.HeroBytes(freeBytes)} to basket", () =>
                Model.ConfirmStageMany(freeable.Select(f => f.Id), "Biggest file", "Biggest files that rebuild by themselves")));
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var mono = new FontFamily("Cascadia Mono, Consolas");
        void run(string s, string color) => text.Inlines.Add(new System.Windows.Documents.Run(s)
        {
            FontFamily = mono, FontSize = Ui.Scaled(12), Foreground = Ui.Brush(color),
        });
        run($"{_entries.Count:N0} largest files · {ShellFormat.HeroBytes(_entries.Sum(e => e.Size))}", "RdInk2");
        if (filtered) run($"  ·  showing {visible.Count:N0} · {ShellFormat.HeroBytes(shown)}", "RdInk2");
        if (freeBytes > 0) { run("  ·  ", "RdInk2"); run($"{ShellFormat.HeroBytes(freeBytes)} can be freed", "RdAccentDeep"); }
        dock.Children.Add(text);
        return new Border { Child = dock, BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    private System.Windows.Threading.DispatcherTimer? _debounce;

    /// <summary>Typing refilters after a short pause, not on every key (500 rows rebuild).</summary>
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

    private static void Stage(Entry entry)
    {
        if (entry.Id >= 0) Model.Stage(entry.Id, "Biggest file: " + entry.Name);
    }

    /// <summary>A click: the row is selected (Ctrl adds it to the selection) and keeps the keyboard.</summary>
    private void Select(Entry entry)
    {
        _selected = entry.Id;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Model.ToggleMulti(entry.Id);
        else { Model.ClearMulti(); Model.Select(entry.Id); }
        _rows?.Refresh(entry);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!ListKit.TakesListKeys(e.OriginalSource) || _preparing) return;
        var rows = _shown;
        if (rows.Count == 0) return;
        int index = _selected is { } s ? rows.FindIndex(r => r.Id == s) : -1;
        switch (e.Key)
        {
            case Key.Down or Key.J when Keyboard.Modifiers == ModifierKeys.None:
            case Key.Up or Key.K when Keyboard.Modifiers == ModifierKeys.None:
                var next = rows[e.Key is Key.Down or Key.J ? Math.Min(rows.Count - 1, index + 1) : Math.Max(0, index - 1)];
                _selected = next.Id;
                Model.Select(next.Id);
                _rows?.Refresh(next);
                e.Handled = true;
                break;
            case Key.Delete when index >= 0:
                Stage(rows[index]);
                e.Handled = true;
                break;
            case Key.Enter when index >= 0:
                Explorer.Reveal(rows[index].AbsolutePath);
                e.Handled = true;
                break;
            case Key.A when Keyboard.Modifiers == ModifierKeys.Control:
                Model.ClearMulti();
                foreach (var r in rows) Model.ToggleMulti(r.Id);
                _rows?.Refresh();
                e.Handled = true;
                break;
            case Key.Escape:
                Model.ClearMulti();
                _rows?.Refresh();
                e.Handled = true;
                break;
        }
    }
}
