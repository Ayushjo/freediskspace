using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-114: the basket (Mac: CleanupQueueView, a 1000 x 680 sheet). What's staged, grouped by
/// where it came from, beside the receipt and the one Move button. Moving confirms once more
/// with every path, shows progress, and — when the Recycle Bin refuses something — offers the
/// documented permanent delete for regenerable folders only (AGENTS.md rule 1). After a move it
/// shows what happened, with Put Back for what went to the Recycle Bin.
/// </summary>
public sealed class BasketWindow : Window
{
    private static ScanModel Model => ScanModel.Shared;
    private static BasketWindow? _open;

    private readonly Grid _root = new();
    private CleanupQueue.StagedItem? _lastRemoved;
    private DispatcherTimer? _undoTimer;
    private FrameworkElement? _undoBar;
    private Outcome? _success;
    private (string Label, int Done, int Total)? _progress;
    private bool _busy;
    private Window? _guarded;
    private bool _showMoved;
    private readonly Dictionary<Guid, string> _whyStayed = [];
    // One list for the window's life: a rebuild swaps its cards, so the scroll position stays.
    private readonly StackPanel _list = new() { Margin = new Thickness(24, 0, 24, 24) };
    private readonly ScrollViewer _listScroll;
    private readonly Dictionary<string, bool> _isFolder = new(StringComparer.OrdinalIgnoreCase);
    // Read once, and again only after a commit or Put Back changes it.
    private CleanupRecord? _lastRecord = CleanupRecord.Load();
    private Guid? _focusAfterRebuild;

    /// <summary>Open the basket over the main window (one at a time).</summary>
    public static void Show(Window? owner)
    {
        // Closed mid-move it only hid; opening it again brings it back, still moving.
        if (_open is not null) { _open.Show(); _open.Activate(); return; }
        _open = new BasketWindow { Owner = owner };
        _open.FitTo(owner);
        _open.Closed += (_, _) => _open = null;
        _open.ShowDialog();
    }

    private BasketWindow()
    {
        Title = "Basket";
        Width = 1000;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Ui.Brush("RdPage");
        Content = _root;
        _listScroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Model.StagedChanged += OnStaged;
        // Sizes landing move only the estimate (ScanModel), and the rows show those sizes.
        Model.PropertyChanged += OnModelProperty;
        Closed += (_, _) =>
        {
            Model.StagedChanged -= OnStaged;
            Model.PropertyChanged -= OnModelProperty;
            SetBusy(false);
        };
        // Esc closes (Done is the cancel action); mid-move that only hides it.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _lastRemoved is { } item) { UndoRemove(item); e.Handled = true; }
        };
        // A big move takes minutes: closing hides the basket and the move carries on. Hiding
        // a dialog ends its modal loop, so the main window is usable again meanwhile.
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            Hide();
            Model.Toast(StillWorkingToast(_progress?.Label));
        };
        Model.RefreshStaged();
        Rebuild();
    }

    /// <summary>1000 × 680, or less on a small screen (1366 × 768 at 125 %): the owner's screen, less its taskbar.</summary>
    private void FitTo(Window? owner)
    {
        var area = SystemParameters.WorkArea;
        if (owner is not null && new System.Windows.Interop.WindowInteropHelper(owner).Handle is var hwnd && hwnd != IntPtr.Zero)
        {
            var pixels = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(owner);
            area = new Rect(0, 0, pixels.Width / dpi.DpiScaleX, pixels.Height / dpi.DpiScaleY);
        }
        (Width, MinWidth) = Fit(1000, 760, area.Width);
        (Height, MinHeight) = Fit(680, 480, area.Height);
    }

    /// <summary>The size wanted, or the room there is (less a margin); the minimum never outgrows the screen.</summary>
    public static (double Size, double Min) Fit(double want, double min, double available)
    {
        double room = Math.Max(0, available - 32);
        double floor = Math.Min(min, room);
        return (Math.Max(floor, Math.Min(want, room)), floor);
    }

    /// <summary>The theme changed: repaint the open basket with the new brushes.</summary>
    internal static void ThemeChanged()
    {
        if (_open is not { } window) return;
        window.Background = Ui.Brush("RdPage");
        window._rows = [];   // kept rows hold the old brushes
        window.Rebuild();
        window.Dispatcher.InvokeAsync(() => { if (window.IsActive && !window.IsKeyboardFocusWithin) window.Focus(); },
            DispatcherPriority.Loaded);
    }

    private void OnStaged(object? sender, EventArgs e) => RequestRebuild();

    private void OnModelProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanModel.ReclaimEstimate)) RequestRebuild();
    }

    private bool _rebuildQueued;

    /// <summary>
    /// Measurements, removals and progress land in bursts: one rebuild per burst, after input and
    /// rendering, so Esc, Hide and the close button always answer.
    /// </summary>
    private void RequestRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.InvokeAsync(() => { _rebuildQueued = false; Rebuild(); }, DispatcherPriority.Background);
    }

    private TextBlock? _progressCount;
    private ProgressBar? _progressBar;

    /// <summary>A commit tick: the same phase only moves its bar, in place — no rebuild per item.</summary>
    private void ShowProgress(string label, int done, int total)
    {
        bool samePhase = _progress?.Label == label;
        _progress = (label, done, total);
        if (!samePhase || _progressBar is null || _progressCount is null) { RequestRebuild(); return; }
        _progressBar.Maximum = Math.Max(1, total);
        _progressBar.Value = done;
        _progressCount.Text = $"{done} of {total}";
    }

    /// <summary>
    /// Mid-move the app stays open: quitting would stop the move half done. The basket hides
    /// instead of closing, and the main window (or the tray's Quit) waits with a note.
    /// </summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (_guarded is not null) _guarded.Closing -= OnOwnerClosing;
        _guarded = busy ? Owner ?? Application.Current?.MainWindow : null;
        if (_guarded is not null) _guarded.Closing += OnOwnerClosing;
    }

    private void OnOwnerClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Model.Toast(StillWorkingToast(_progress?.Label));
    }

    private static List<CleanupQueue.StagedItem> Items => Model.Cleanup.AllItems().ToList();

    /// <summary>The list's order: by where items came from, then as staged.</summary>
    private static IEnumerable<IGrouping<string, CleanupQueue.StagedItem>> Groups(IEnumerable<CleanupQueue.StagedItem> items) =>
        items.GroupBy(i => Source(i.Reason)).OrderBy(g => g.Key, StringComparer.Ordinal);

    private void Rebuild()
    {
        _progressCount = null;
        _progressBar = null;
        _undoBar = null;
        _root.Children.Clear();
        _root.ColumnDefinitions.Clear();
        var items = Items;
        if (items.Count == 0 && _success is { } success)
        {
            _root.Children.Add(SuccessView(success));
            return;
        }
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        _root.Children.Add(ItemsColumn(items));
        var line = new Border { Background = Ui.Brush("RdLine") };
        Grid.SetColumn(line, 1);
        _root.Children.Add(line);
        var receipt = ReceiptColumn(items);
        Grid.SetColumn(receipt, 2);
        _root.Children.Add(receipt);
        // A row taken out with the keyboard hands the focus to its neighbour.
        if (_focusAfterRebuild is { } next && _rows.TryGetValue(next, out var row))
            Dispatcher.InvokeAsync(() => row.Row.Focus(), DispatcherPriority.Loaded);
        _focusAfterRebuild = null;
    }

    // ---- Items ----

    private FrameworkElement ItemsColumn(List<CleanupQueue.StagedItem> items)
    {
        var dock = new DockPanel();
        var estimate = Model.ReclaimEstimate;

        var header = new DockPanel { Margin = new Thickness(24, 22, 24, 16) };
        var done = Rd.SecondaryButton(_busy ? "Hide" : "Done", Close);
        done.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(done, Dock.Right);
        header.Children.Add(done);
        if (estimate.IsCalculating)
        {
            var spinner = new ProgressBar { IsIndeterminate = true, Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(spinner, Dock.Right);
            header.Children.Add(spinner);
        }
        var titles = new StackPanel();
        titles.Children.Add(Rd.PageTitle("Basket"));
        var sub = Rd.Text(items.Count == 0 ? "Nothing in it yet" : $"{Count(items.Count, "item")} · {FreeSummary(estimate)}", 13, null, "RdInk2");
        sub.Margin = new Thickness(0, 4, 0, 0);
        titles.Children.Add(sub);
        header.Children.Add(titles);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        if (_progress is { } p)
        {
            var bar = new StackPanel { Margin = new Thickness(24, 0, 24, 12) };
            var line = new DockPanel();
            var count = _progressCount = Rd.Figure($"{p.Done} of {p.Total}", 11, null, "RdInk3");
            DockPanel.SetDock(count, Dock.Right);
            line.Children.Add(count);
            line.Children.Add(Rd.Text(p.Label, 12, null, "RdInk2"));
            bar.Children.Add(line);
            bar.Children.Add(_progressBar = new ProgressBar { Height = 4, Margin = new Thickness(0, 6, 0, 0), Maximum = Math.Max(1, p.Total), Value = p.Done });
            System.Windows.Automation.AutomationProperties.SetAutomationId(bar, "cleanup-commit-progress");
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
        }

        if (_lastRemoved is { } removed)
        {
            var undo = new DockPanel { Height = 40, Margin = new Thickness(24, 0, 24, 0) };
            var text = Rd.Text($"Removed “{Dialogs.SafeDisplay(FileName(removed.Path))}” — it stays on disk", 12, null, "RdInk2");
            DockPanel.SetDock(text, Dock.Left);
            undo.Children.Add(text);
            var link = Rd.Link("Undo", () => UndoRemove(removed), 12);
            link.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(link, Dock.Left);
            undo.Children.Add(link);
            undo.Children.Add(new Border());
            var undoBox = new Border { Child = undo, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(undoBox, Dock.Bottom);
            dock.Children.Add(_undoBar = undoBox);
        }

        if (items.Count == 0)
        {
            var empty = new StackPanel();
            empty.Children.Add(Rd.Text("Your basket is empty", 15, FontWeights.SemiBold));
            var why = Rd.Text("Tick things in Clean up, or add files from any page with Add to basket (Ctrl+Backspace). Nothing moves to the Recycle Bin until you confirm here.",
                13, null, "RdInk2", wrap: true);
            why.Margin = new Thickness(0, 8, 0, 0);
            empty.Children.Add(why);
            var box = new Grid { Margin = new Thickness(24, 0, 24, 0), VerticalAlignment = VerticalAlignment.Top };
            box.Children.Add(new Rectangle
            {
                RadiusX = 12, RadiusY = 12, Fill = Ui.Brush("RdCard"), Stroke = Ui.Brush("RdLine"),
                StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            });
            empty.Margin = new Thickness(24);
            box.Children.Add(empty);
            dock.Children.Add(box);
            return dock;
        }

        double offset = _listScroll.VerticalOffset;
        (_listScroll.Parent as Panel)?.Children.Remove(_listScroll);
        _list.Children.Clear();
        var covered = CleanupQueue.CoveredItemIds(items);
        // Sizes land one by one: keep each row whose caption didn't change instead of
        // re-creating hundreds of rows per landing (that rebuild is what froze the window).
        var rows = new Dictionary<Guid, (string Key, FrameworkElement Row)>();
        foreach (var group in Groups(items))
        {
            var card = new StackPanel();
            var head = new DockPanel { Height = 44, Margin = new Thickness(16, 0, 16, 0) };
            var size = Rd.Figure(ByteFormat.Format(group.Sum(i => estimate.PerItem.GetValueOrDefault(i.Id, i.Size))), 12);
            DockPanel.SetDock(size, Dock.Right);
            head.Children.Add(size);
            var name = Rd.CardTitle(group.Key);
            DockPanel.SetDock(name, Dock.Left);
            head.Children.Add(name);
            var n = Rd.Text(Count(group.Count(), "item"), 12, null, "RdInk3");
            n.Margin = new Thickness(8, 0, 0, 0);
            head.Children.Add(n);
            card.Children.Add(new Border { Child = head, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1) });
            foreach (var item in group)
            {
                string caption = Caption(item, covered, estimate);
                string key = caption + "\n" + _whyStayed.GetValueOrDefault(item.Id);
                if (!_rows.TryGetValue(item.Id, out var cached) || cached.Key != key) cached = (key, Row(item, caption));
                (cached.Row.Parent as Panel)?.Children.Remove(cached.Row);
                card.Children.Add(cached.Row);
                rows[item.Id] = cached;
            }
            var surface = Rd.Surface(card, "RdCard", 12);
            surface.Margin = new Thickness(0, _list.Children.Count > 0 ? 14 : 0, 0, 0);
            _list.Children.Add(surface);
        }
        _rows = rows;
        _list.IsEnabled = !_busy;
        dock.Children.Add(_listScroll);
        _listScroll.ScrollToVerticalOffset(offset);
        return dock;
    }

    private Dictionary<Guid, (string Key, FrameworkElement Row)> _rows = [];

    /// <summary>Folder or file, asked of the disk once per path — not again for every new row.</summary>
    private bool IsFolder(string path)
    {
        if (!_isFolder.TryGetValue(path, out bool folder)) _isFolder[path] = folder = Directory.Exists(path);
        return folder;
    }

    private FrameworkElement Row(CleanupQueue.StagedItem item, string captionText)
    {
        bool isFolder = IsFolder(item.Path) && System.IO.Path.GetExtension(item.Path).Length == 0;
        var row = new DockPanel { Margin = new Thickness(16, 8, 16, 8) };
        var icon = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7),
            Background = Rd.WithOpacity(Ui.Brush("RdInk"), 0.06),
            Child = new Border
            {
                Child = Ui.Glyph(isFolder ? Icons.Folder : Icons.File, 14, Ui.Brush("RdInk2")),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var caption = Rd.Figure(captionText, 11, null, "RdInk2");
        caption.TextAlignment = TextAlignment.Right;
        caption.TextWrapping = TextWrapping.Wrap;
        caption.MaxWidth = 220;
        caption.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(caption, Dock.Right);
        row.Children.Add(caption);

        // Transparent, not hidden, until the row is hovered or focused: hidden buttons can't take the focus.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(RowButton(Icons.Visibility, "Open", () => OpenItem(item.Path)));
        actions.Children.Add(RowButton(Icons.Cancel, "Take out of the basket — the file stays where it is",
            () => RemoveWithUndo(item, keyboard: InputManager.Current.MostRecentInputDevice is KeyboardDevice)));
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Rd.Text(Dialogs.SafeDisplay(FileName(item.Path)), 13, FontWeights.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        var where = Rd.Text(Dialogs.SafeDisplay(ParentDisplay(item.Path)), 12, null, "RdInk3");
        where.ToolTip = Dialogs.SafeDisplay(item.Path);   // the row shortens it; the whole path on hover
        where.TextTrimming = TextTrimming.CharacterEllipsis;
        where.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(where);
        // Staged from any page (Biggest files can surface Docker's disk): warn where the
        // Recycle Bin would damage a tool's state.
        if (CleanupRecipes.RecipeFor(item.Path) is { TrashIsUnsafe: true } recipe)
        {
            var warn = Rd.Text($"Moving this to the Recycle Bin damages {(recipe.Id == "docker" ? "Docker" : "the tool")}’s data. Use `{recipe.Command}` instead.", 12, null, "AppDanger", wrap: true);
            text.Children.Add(warn);
        }
        if (_whyStayed.TryGetValue(item.Id, out var why))
        {
            var failed = Rd.Text(why, 12, null, "AppWarning", wrap: true);
            System.Windows.Automation.AutomationProperties.SetAutomationId(failed, "cleanup-row-failure");
            text.Children.Add(failed);
        }
        row.Children.Add(text);

        var host = new Border
        {
            MinHeight = 48, Child = row, Background = Brushes.Transparent, Focusable = true,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.7), BorderThickness = new Thickness(0, 0, 0, 1),
            ToolTip = Detail(item.Reason) is { } detail ? Dialogs.SafeDisplay(detail) : null,
        };
        void Lit(bool mouse)
        {
            bool on = mouse || host.IsKeyboardFocusWithin;
            host.Background = on ? Rd.WithOpacity(Ui.Brush("RdSoft"), 0.6) : Brushes.Transparent;
            actions.Opacity = on ? 1 : 0;
        }
        host.MouseEnter += (_, _) => Lit(true);
        host.MouseLeave += (_, _) => Lit(false);
        host.IsKeyboardFocusWithinChanged += (_, _) => Lit(host.IsMouseOver);
        // Delete on a focused row (or one of its buttons) takes it out, like the Take out button.
        host.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None) return;
            RemoveWithUndo(item, keyboard: true);
            e.Handled = true;
        };
        var menu = new ContextMenu();
        void item_(string header, Action action) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => action(); menu.Items.Add(mi); }
        item_("Open", () => OpenItem(item.Path));
        item_("Show in File Explorer", () => Explorer.Reveal(item.Path));
        menu.Items.Add(new Separator());
        item_("Take Out of the Basket", () => RemoveWithUndo(item));
        host.ContextMenu = menu;
        System.Windows.Automation.AutomationProperties.SetName(host, $"{name.Text}, {caption.Text}");
        System.Windows.Automation.AutomationProperties.SetHelpText(host, "Delete takes it out of the basket");
        return host;
    }

    /// <summary>A row action: a real button (Tab, Enter and Space reach it), drawn like the quiet icon buttons.</summary>
    private static Button RowButton(string icon, string label, Action onClick)
    {
        var button = Ui.IconButton(icon, label, onClick);
        button.Content = Ui.Glyph(icon, 12, Ui.Brush("RdInk2"));
        button.Width = button.Height = 24;
        return button;
    }

    /// <summary>Open a folder in File Explorer, a file in its app, as the signed-in user; programs are only shown, never run.</summary>
    private static void OpenItem(string path) => Explorer.Open(path);

    // ---- Receipt column ----

    private FrameworkElement ReceiptColumn(List<CleanupQueue.StagedItem> items)
    {
        var estimate = Model.ReclaimEstimate;
        var vol = Model.Volume ?? Model.Snapshot?.Volume;
        var lines = Groups(items)
            .Select(g => (g.Count() == 1 ? Dialogs.SafeDisplay(FileName(g.First().Path)) : $"{g.Key} · {g.Count()}",
                g.Sum(i => estimate.PerItem.GetValueOrDefault(i.Id, i.Size))))
            .ToList();
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new ReceiptView(lines, estimate.Bytes, vol?.FreeBytes,
            vol?.DriveLabel ?? System.IO.Path.GetFileName(Model.RootPath?.TrimEnd('\\') ?? "") ?? "This PC", 0, compact: false));
        if (estimate.HeldByUnqueuedCopies > 0)
        {
            var held = Rd.Text($"{ByteFormat.Format(estimate.HeldByUnqueuedCopies)} stays in use by copies or links that aren’t in the basket.", 12, null, "RdInk2", wrap: true);
            held.Margin = new Thickness(0, 18, 0, 0);
            stack.Children.Add(held);
        }
        stack.Children.Add(MoveButton(items.Count, estimate.IsCalculating));
        if (LastCleanup() is { } last)
        {
            last.Margin = new Thickness(0, 24, 0, 0);
            stack.Children.Add(last);
        }
        var host = new Grid { Background = Ui.Brush("RdSidebar") };
        host.Children.Add(new Border { Background = Rd.Grid24(), Opacity = 0.8 });
        host.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        return host;
    }

    private Button MoveButton(int count, bool measuring)
    {
        // Still measuring: greyed, and it says why and how far along — not a purple button that waits.
        bool waiting = measuring && !_busy && count > 0;
        bool disabled = count == 0 || _busy || waiting;
        var (measured, total) = waiting ? Model.Cleanup.MeasurementProgress() : (0, 0);
        string ink = waiting ? "RdInk2" : "RdOnAccent";
        var content = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        var eyebrow = Rd.Figure(_busy ? "WORKING — YOU CAN CLOSE THIS WINDOW"
            : waiting ? $"MEASURING SIZES · {measured:N0} OF {total:N0} READY"
            : "YOU’LL CONFIRM ONCE MORE", 10.5, FontWeights.Medium, ink);
        eyebrow.Opacity = 0.85;
        content.Children.Add(eyebrow);
        string titleText = _busy ? _progress?.Label ?? "Moving…" : count == 0 ? "Nothing to move"
            : waiting ? "Available once sizes are measured" : $"Move {Count(count, "item")} to Recycle Bin…";
        content.Children.Add(new Viewbox
        {
            Child = Rd.TightText(titleText, 21, -0.3, ink), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0),
        });
        var look = new Grid();
        look.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(224, 0, 0, 0)), Margin = new Thickness(5, 5, -5, -5) });
        look.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = waiting ? Ui.Brush("RdSoft") : disabled ? Rd.WithOpacity(Ui.Brush("AppAccent"), 0.45) : Ui.Brush("AppAccent"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(217, 0, 0, 0)), BorderThickness = new Thickness(1),
            Child = content,
        });
        // A real button around the drawn face: Tab reaches it, Enter and Space press it, a screen
        // reader names it. Not AccentButton: its disabled look fades the text to 40 % over the
        // black shadow, and "Working…" and "Measuring sizes" must stay readable.
        var button = new Button
        {
            Content = look, Template = Dialogs.BareButton(), IsEnabled = !disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 28, 5, 0),
            Cursor = disabled ? Cursors.Arrow : Cursors.Hand,
            ToolTip = waiting ? "Some folders are still being measured, so the figure above may change. The button turns on when they're done."
                : "Moves everything here to the Recycle Bin, after you confirm",
        };
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += (_, _) => Commit();
        System.Windows.Automation.AutomationProperties.SetName(button, titleText);
        return button;
    }

    private FrameworkElement? LastCleanup()
    {
        if (_lastRecord is not { Items.Count: > 0 } last) return null;
        var stack = new StackPanel();
        stack.Children.Add(Rd.Text("Last cleanup", 12, null, "RdInk2"));
        var row = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        var putBack = Rd.SecondaryButton("Put Back", PutBackLast);
        putBack.ToolTip = "Move them from the Recycle Bin back where they were. Nothing that is there now is replaced.";
        putBack.IsEnabled = !_busy;   // a move in flight writes the record Put Back reads
        putBack.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(putBack, Dock.Right);
        row.Children.Add(putBack);
        var text = new StackPanel();
        text.Children.Add(Rd.Text($"{Count(last.Items.Count, "item")} · {ShellFormat.HeroBytes(last.Items.Sum(i => i.Bytes))}", 13));
        var when = Rd.Figure(ShellFormat.RelativeTime(last.Date, DateTimeOffset.Now), 11, null, "RdInk3");
        when.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(when);
        row.Children.Add(text);
        var card = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        card.Children.Add(new Rectangle
        {
            RadiusX = 10, RadiusY = 10, Fill = Ui.Brush("RdCard"), Stroke = Ui.Brush("RdLine"),
            StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
        });
        card.Children.Add(row);
        stack.Children.Add(card);
        return stack;
    }

    /// <summary>Put Back the last cleanup; what came back is on disk again, so the scan catches up.</summary>
    private async void PutBackLast()
    {
        if (_busy) return;
        var report = await Model.PutBackLastCleanupAsync();
        _lastRecord = CleanupRecord.Load();
        _success = null;
        if (report is { Restored.Count: > 0 }) Rescan();
        Rebuild();
    }

    /// <summary>What moved is gone and what came back is here again: rescan so every page, and "You can free", agree.</summary>
    private static void Rescan()
    {
        if (Model.RootPath is not null && !Model.IsScanning) _ = Model.RescanAsync();
    }

    // ---- Success ----

    private FrameworkElement SuccessView(Outcome success)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement icon = AppSettings.Load().ShowDusty ? Ui.BrandIcon(104) : Ui.Glyph(Icons.Check, 22, Ui.Brush("AppAccent"));
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(icon);
        void add(FrameworkElement e, double top) { e.HorizontalAlignment = HorizontalAlignment.Center; e.Margin = new Thickness(0, top, 0, 0); stack.Children.Add(e); }
        add(Rd.Text(success.Headline, 15, FontWeights.SemiBold), 12);
        if (success.FreedLine.Length > 0)
        {
            var freed = Rd.Figure(success.FreedLine, 12, null, "RdInk2");
            freed.TextAlignment = TextAlignment.Center;
            freed.TextWrapping = TextWrapping.Wrap;
            freed.MaxWidth = 520;
            add(freed, 12);
        }
        var note = Rd.Text(success.Note, 12, null, "RdInk3", wrap: true);
        note.TextAlignment = TextAlignment.Center;
        note.MaxWidth = 380;
        add(note, 12);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        // Put Back only returns what went to the Recycle Bin; after permanent deletes alone there's nothing to return.
        if (success.Moved > 0)
        {
            var putBack = Rd.SecondaryButton("Put Back", PutBackLast);
            putBack.ToolTip = "Move them from the Recycle Bin back where they were. Nothing that is there now is replaced.";
            putBack.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(putBack);
        }
        var done = Rd.PrimaryButton("Done", Close);
        done.IsDefault = true;
        buttons.Children.Add(done);
        add(buttons, 20);
        if (success.Details.Count > 0)
        {
            add(Rd.Link(_showMoved ? "Hide details" : "Show details", () => { _showMoved = !_showMoved; Rebuild(); }, 12), 20);
            if (_showMoved)
            {
                var details = Rd.Figure(string.Join("\n", success.Details.Select(Dialogs.SafeDisplay)), 11, null, "RdInk2");
                details.TextTrimming = TextTrimming.None;
                add(new ScrollViewer { Content = details, MaxHeight = 120, MaxWidth = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 8);
            }
        }
        return stack;
    }

    // ---- Commit (the only path to the Recycle Bin) ----

    private async void Commit()
    {
        if (_busy) return;
        var items = Items;
        if (items.Count == 0) return;
        var estimate = Model.Cleanup.Estimate();
        // Each row carries what it frees, as the group headers do, so the total matches the sentence.
        if (!Dialogs.Confirm(this,
                $"Move {Count(items.Count, "item")} to the Recycle Bin?",
                ConfirmMessage(items, estimate),
                items.Select(i => new Dialogs.ConfirmItem(FileName(i.Path), i.Path, estimate.PerItem.GetValueOrDefault(i.Id, i.Size))).ToList(),
                "Move to Recycle Bin", danger: true, footnote: CommitFootnote(items)))
            return;

        CleanupQueue.CommitReport report;
        List<CleanupQueue.CommitEntry> regenerable;
        SetBusy(true);
        try
        {
            var progress = new Progress<CleanupQueue.CommitProgress>(p => ShowProgress(
                p.Phase.StartsWith("Verif", StringComparison.OrdinalIgnoreCase) ? "Verifying sizes…" : "Moving to the Recycle Bin…", p.Done, p.Total));
            _progress = ("Verifying sizes…", 0, items.Count);
            Rebuild();
            // Folders recycle first; their staged children report "moved with the folder".
            // Failures — including Recycle Bin refusals — stay staged with a reason. The bin
            // refuses what's too big for it, and everything on a drive that keeps none (a
            // network share, or the bin switched off); only regenerable ones may be deleted
            // permanently. Sorted off the UI thread, still busy: that can be every item.
            (report, regenerable) = await Task.Run(() =>
            {
                var r = Model.Cleanup.Commit(progress);
                return (r, r.Entries.Where(e => e.Error is DiskMap.Core.Native.RecycleRefusedException
                    && CleanupQueue.IsRegenerable(e.Item.Path)).ToList());
            });
        }
        finally
        {
            SetBusy(false);
            _progress = null;
        }
        Model.RefreshStaged();
        _whyStayed.Clear();
        var refused = report.Entries.Where(e => e.Error is DiskMap.Core.Native.RecycleRefusedException).ToList();
        var regenerableIds = regenerable.Select(e => e.Item.Id).ToHashSet();
        foreach (var failed in report.Entries.Where(e => e.Error is not null))
            _whyStayed[failed.Item.Id] = failed.Error is DiskMap.Core.Native.RecycleRefusedException
                ? RefusedReason(regenerableIds.Contains(failed.Item.Id))
                : "Couldn’t move: " + failed.Error!.Message;

        // Ask ONCE, for the regenerable ones only (dependencies, build output, caches) — the
        // documented rule 1 exception.
        CleanupQueue.CommitReport? deleted = null;
        if (regenerable.Count > 0)
        {
            var prompt = PermanentDeletePrompt(regenerable.Count, refused.Count - regenerable.Count);
            var stayed = Model.Cleanup.Estimate();
            if (Dialogs.Confirm(this, prompt.Title, prompt.Message,
                    regenerable.Select(e => new Dialogs.ConfirmItem(FileName(e.Item.Path), e.Item.Path,
                        stayed.PerItem.GetValueOrDefault(e.Item.Id, e.Item.Size))).ToList(),
                    prompt.ConfirmLabel, danger: true, footnote: prompt.Footnote, hint: prompt.Hint))
            {
                SetBusy(true);
                try
                {
                    var progress = new Progress<CleanupQueue.CommitProgress>(p => ShowProgress("Deleting permanently…", p.Done, p.Total));
                    var ids = regenerable.Select(e => e.Item.Id).ToList();
                    deleted = await Task.Run(() => Model.Cleanup.DeletePermanently(ids, progress));
                }
                finally
                {
                    SetBusy(false);
                    _progress = null;
                }
                foreach (var entry in deleted.Entries)
                    if (entry.Error is null) _whyStayed.Remove(entry.Item.Id);
                    else _whyStayed[entry.Item.Id] = DeleteFailedReason(entry.Error.Message);
                Model.RefreshStaged();
            }
        }

        _lastRecord = CleanupRecord.Load();
        int left = Items.Count;
        int deleteFailed = deleted?.Entries.Count(e => e.Error is not null) ?? 0;
        var outcome = DescribeCommit(report, deleted, left);
        if (outcome.Moved == 0 && outcome.Deleted == 0)
        {
            Rebuild();
            Model.Toast(NothingToast(refused.Count, deleteFailed));
            if (!IsVisible) Close();
            return;
        }
        _success = left == 0 ? outcome : null;
        _showMoved = false;
        Rescan();
        Rebuild();
        Model.Toast(outcome.Toast);
        if (!IsVisible) Close();   // hidden mid-move: the toast is the report
    }

    /// <summary>The commit sheet's warnings: duplicates with no copy left, and items that sync to the cloud.</summary>
    private string? CommitFootnote(List<CleanupQueue.StagedItem> items)
    {
        var everyCopy = Model.Duplicates
            .Where(g => g.FileIDs.Count > 1 && g.FileIDs.All(id => Model.IsCovered(Model.PathOf(id))))
            .Select(g => (IReadOnlyList<string>)g.FileIDs.Select(Model.PathOf).ToList())
            .ToList();
        int cloud = items.Count(i => StorageClassifier.Classify(i.Path, IsFolder(i.Path)).Class == StorageClassifier.Cloud);
        return CommitWarnings(everyCopy, cloud);
    }

    // ---- Remove / undo ----

    private void RemoveWithUndo(CleanupQueue.StagedItem item, bool keyboard = false)
    {
        if (_busy) return;
        if (keyboard) _focusAfterRebuild = NextRow(item.Id);
        _lastRemoved = item;
        _undoTimer?.Stop();
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _undoTimer.Tick += (_, _) => HideUndo();
        _undoTimer.Start();
        Model.Unstage(item.Id);   // its StagedChanged is the one rebuild, undo bar included
    }

    /// <summary>The undo offer expired: drop its bar in place — a whole rebuild for that cost as much as the removal.</summary>
    private void HideUndo()
    {
        _undoTimer?.Stop();
        _lastRemoved = null;
        if (_undoBar is not null) _undoBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>The row the keyboard focus goes to when this one leaves: the next, else the one before.</summary>
    private static Guid? NextRow(Guid id)
    {
        var order = Groups(Items).SelectMany(g => g).Select(i => i.Id).ToList();
        int at = order.IndexOf(id);
        return at < 0 ? null : at + 1 < order.Count ? order[at + 1] : at > 0 ? order[at - 1] : null;
    }

    private void UndoRemove(CleanupQueue.StagedItem item)
    {
        _undoTimer?.Stop();
        _lastRemoved = null;
        Model.Cleanup.Stage(item.Path, item.Size, item.Reason, item.SharesStorageGroup, item.GroupCopyCount);
        Model.RefreshStaged();   // its StagedChanged rebuilds
    }

    // ---- Words ----

    private static string Count(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    private static string FileName(string path) => System.IO.Path.GetFileName(path.TrimEnd('\\'));

    /// <summary>Moving to the Recycle Bin frees nothing on its own; say when it does.</summary>
    public static string FreeSummary(CleanupQueue.ReclaimEstimate estimate)
    {
        if (estimate.IsCalculating) return "Calculating what emptying the Recycle Bin will free…";
        string amount = ByteFormat.Format(estimate.Bytes);
        return estimate.IsLowerBound
            ? $"At least {amount} freed when you empty the Recycle Bin"
            : $"{amount} freed when you empty the Recycle Bin";
    }

    public static string ConfirmMessage(IReadOnlyList<CleanupQueue.StagedItem> items, CleanupQueue.ReclaimEstimate estimate)
    {
        string text = $"Moves {Count(items.Count, "item")} to the Recycle Bin — nothing is deleted permanently. ";
        text += estimate.IsLowerBound || estimate.IsCalculating ? "At least " : "About ";
        text += $"{ByteFormat.Format(estimate.Bytes)} is freed once you empty the Recycle Bin.";
        if (estimate.HeldByUnqueuedCopies > 0)
            text += $" {ByteFormat.Format(estimate.HeldByUnqueuedCopies)} stays in use because other copies or links of these files aren’t in the basket.";
        if (items.Select(i => CleanupRecipes.RecipeFor(i.Path)).FirstOrDefault(r => r is { TrashIsUnsafe: true }) is { } recipe)
            text += $" Warning: the basket includes data a tool manages itself — `{recipe.Command}` is the safe way to clean it.";
        return text;
    }

    /// <summary>
    /// The footnote under the commit sheet's list: every copy of a duplicate is in the basket (none
    /// would be left), and items in a cloud-synced folder (moving them removes them everywhere).
    /// </summary>
    public static string? CommitWarnings(IReadOnlyList<IReadOnlyList<string>> everyCopyInBasket, int cloudItems)
    {
        var notes = new List<string>();
        if (everyCopyInBasket.Count == 1)
        {
            var copies = everyCopyInBasket[0];
            notes.Add($"Every copy of “{Dialogs.SafeDisplay(FileName(copies[0]))}” is in the basket, so none would be left: "
                + string.Join(", ", copies.Select(Dialogs.SafeDisplay)) + ".");
        }
        else if (everyCopyInBasket.Count > 1)
        {
            const int Named = 5;
            var names = everyCopyInBasket.Take(Named).Select(c => $"“{Dialogs.SafeDisplay(FileName(c[0]))}” ({c.Count} copies)");
            string more = everyCopyInBasket.Count > Named ? $" and {everyCopyInBasket.Count - Named:N0} more" : "";
            notes.Add($"Every copy of {everyCopyInBasket.Count:N0} duplicate files is in the basket, so none would be left: {string.Join(", ", names)}{more}.");
        }
        if (cloudItems > 0)
            notes.Add(cloudItems == 1
                ? "1 item is in a cloud-synced folder: moving it also removes it from the cloud and your other devices."
                : $"{cloudItems:N0} items are in a cloud-synced folder: moving them also removes them from the cloud and your other devices.");
        return notes.Count == 0 ? null : string.Join("\n\n", notes);
    }

    /// <summary>
    /// Why a refused item stayed. A refusal isn't always size: a drive with no Recycle Bin (a
    /// network share, or the bin switched off) refuses everything the same way.
    /// </summary>
    public static string RefusedReason(bool regenerable) =>
        "Couldn’t move: the Recycle Bin wouldn't take it (too big, or this drive has no Recycle Bin) — nothing was deleted"
        + (regenerable ? "" : ". Make the Recycle Bin bigger or turn it on for this drive (Recycle Bin → Properties), or delete it yourself");

    /// <summary>A permanent delete stops at the first file it can't remove, so part of the folder may already be gone.</summary>
    public static string DeleteFailedReason(string error) =>
        $"Couldn’t finish deleting it permanently: {error} — part of it may already be gone; your tools recreate it on the next install or build";

    public sealed record Prompt(string Title, string Message, string ConfirmLabel, string Hint, string? Footnote);

    /// <summary>The one question before the documented permanent delete: what, why, and that it can't be undone.</summary>
    public static Prompt PermanentDeletePrompt(int count, int others)
    {
        bool one = count == 1;
        string them = one ? "it" : "them";
        return new Prompt(
            one ? "The Recycle Bin can't take this item" : $"The Recycle Bin can't take these {count:N0} items",
            $"The Recycle Bin can't take {(one ? "this" : "these")} — {(one ? "it's" : "they're")} too big for it, or this drive has no Recycle Bin — "
            + $"so nothing has been deleted. {(one ? "It's" : "They're")} regenerable (dependencies, build output, caches): your tools recreate "
            + $"{them} on the next install or build. Delete {them} permanently?",
            $"Delete {count:N0} permanently",
            "This can't be undone — Put Back doesn't cover these.",
            others == 0 ? null
                : $"{Count(others, "other item")} the Recycle Bin wouldn't take {(others == 1 ? "isn't" : "aren't")} regenerable, so "
                  + $"{(others == 1 ? "it stays" : "they stay")} in the basket. Make the Recycle Bin bigger or turn it on for this drive "
                  + $"(Recycle Bin → Properties), or delete {(others == 1 ? "it" : "them")} yourself.");
    }

    /// <summary>What a commit did, in words: the success screen and the toast.</summary>
    public sealed record Outcome(int Moved, int Deleted, string Headline, string FreedLine, string Note, List<string> Details, string Toast);

    /// <summary>
    /// Built from what happened — moved, deleted permanently, or both — so permanent deletes are
    /// never reported as "moved to the Recycle Bin", and each one is named.
    /// </summary>
    public static Outcome DescribeCommit(CleanupQueue.CommitReport report, CleanupQueue.CommitReport? deleted, int left)
    {
        var moved = report.Entries.Where(e => e.Error is null).Select(e => e.Item.Path).ToList();
        var gone = deleted?.Entries.Where(e => e.Error is null).Select(e => e.Item.Path).ToList() ?? [];
        string headline = gone.Count == 0 ? $"{Count(moved.Count, "item")} moved to the Recycle Bin"
            : moved.Count == 0 ? $"{Count(gone.Count, "item")} deleted permanently"
            : $"{Count(moved.Count, "item")} moved to the Recycle Bin, {gone.Count:N0} deleted permanently";
        var freed = new List<string>();
        if (moved.Count > 0)
            freed.Add($"{(report.IsLowerBound ? "At least " : "")}{ByteFormat.Format(report.FreedWhenEmptied)} is freed when you empty the Recycle Bin.");
        if (deleted is not null && gone.Count > 0)
            freed.Add($"{(deleted.IsLowerBound ? "At least " : "")}{ByteFormat.Format(deleted.FreedWhenEmptied)} freed now by deleting permanently.");
        string note = gone.Count == 0 ? "Changed your mind? Put Back returns them exactly where they were."
            : moved.Count == 0 ? $"Deleted permanently, so Put Back can't return {(gone.Count == 1 ? "it" : "them")} — your tools recreate {(gone.Count == 1 ? "it" : "them")} on the next install or build."
            : "Changed your mind? Put Back returns what went to the Recycle Bin, not what was deleted permanently.";
        var details = moved.Select(p => "Moved " + p).Concat(gone.Select(p => "Deleted permanently " + p)).ToList();
        if (left > 0) details.Add($"{Count(left, "item")} stayed in the basket — see the list for why.");
        var toast = new List<string>();
        if (moved.Count > 0) toast.Add($"Moved {Count(moved.Count, "item")} to the Recycle Bin");
        if (gone.Count > 0) toast.Add(moved.Count > 0 ? $"deleted {gone.Count:N0} permanently" : $"Deleted {Count(gone.Count, "item")} permanently");
        if (left > 0) toast.Add($"{left:N0} still in the basket");
        return new Outcome(moved.Count, gone.Count, headline, string.Join(" ", freed), note, details, string.Join(" · ", toast));
    }

    /// <summary>The toast when nothing moved and nothing was deleted.</summary>
    public static string NothingToast(int refused, int deleteFailed) =>
        deleteFailed > 0 ? "Nothing moved, and deleting permanently didn’t finish — the basket says why for each item"
        : refused > 0 ? "Nothing moved — the Recycle Bin wouldn't take these items (too big, or this drive has no Recycle Bin); nothing was deleted"
        : "Nothing could be moved — the items may be in use; they're still in the basket";

    /// <summary>What the basket says when it is closed, or the app quit, mid-move.</summary>
    public static string StillWorkingToast(string? phase) =>
        phase?.StartsWith("Deleting", StringComparison.Ordinal) == true
            ? "Still deleting permanently — open the basket to watch, you'll get a note when it's done"
            : "Still moving to the Recycle Bin — open the basket to watch, you'll get a note when it's done";

    /// <summary>What this row contributes, and why it may be less than its size.</summary>
    public static string Caption(CleanupQueue.StagedItem item, HashSet<Guid> covered, CleanupQueue.ReclaimEstimate estimate)
    {
        if (item.IsMeasuring) return $"{ByteFormat.Format(item.Size)} · measuring…";
        long freed = estimate.PerItem.GetValueOrDefault(item.Id, item.Size);
        if (covered.Contains(item.Id)) return "Inside a folder in the basket — counted there";
        if (freed == 0 && item.Size > 0) return $"{ByteFormat.Format(item.Size)} · shared — freed only when every copy or link is in the basket";
        return freed + 4_096 < item.Size
            ? $"Frees {ByteFormat.Format(freed)} of {ByteFormat.Format(item.Size)} — the rest is shared"
            : ByteFormat.Format(freed);
    }

    /// <summary>Items grouped by where they came from ("Old download", "Biggest file").</summary>
    public static string Source(string reason)
    {
        int colon = reason.IndexOf(':');
        string head = (colon >= 0 ? reason[..colon] : reason).Trim();
        if (head.Length == 0) return "Other";
        head = head switch
        {
            "duplicate" => "Duplicates",
            "caches" => "Caches",
            "developer storage" => "Developer storage",
            _ => head,
        };
        if (head.StartsWith("leftover of ", StringComparison.Ordinal)) head = "App leftovers";
        return char.ToUpperInvariant(head[0]) + head[1..];
    }

    public static string? Detail(string reason)
    {
        int colon = reason.IndexOf(':');
        if (colon < 0) return null;
        string text = reason[(colon + 1)..].Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>"~\Downloads\" style parent folder for a staged path.</summary>
    public static string ParentDisplay(string path)
    {
        string parent = System.IO.Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;
        string display = CanonicalPath.DisplayPath(parent);
        return display.EndsWith('\\') ? display : display + "\\";
    }
}
