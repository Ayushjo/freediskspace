using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-114: the basket (Mac: CleanupQueueView, a 1000 x 680 sheet). What's staged, grouped by
/// where it came from, beside the receipt and the one Move button. Moving confirms once more
/// with every path, shows progress, and — when the Recycle Bin refuses something as too big —
/// offers the documented permanent delete for regenerable folders only (AGENTS.md rule 1).
/// After a move it shows what happened, with Put Back.
/// </summary>
public sealed class BasketWindow : Window
{
    private static ScanModel Model => ScanModel.Shared;
    private static BasketWindow? _open;

    private readonly Grid _root = new();
    private CleanupQueue.StagedItem? _lastRemoved;
    private System.Windows.Threading.DispatcherTimer? _undoTimer;
    private Success? _success;
    private (string Label, int Done, int Total)? _progress;
    private bool _busy;
    private bool _showMoved;
    private readonly Dictionary<Guid, string> _whyStayed = [];

    private sealed record Success(int Count, string FreedLine, List<string> Details);

    /// <summary>Open the basket over the main window (one at a time).</summary>
    public static void Show(Window? owner)
    {
        if (_open is not null) { _open.Activate(); return; }
        _open = new BasketWindow { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.ShowDialog();
    }

    private BasketWindow()
    {
        Title = "Basket";
        Width = 1000;
        Height = 680;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Ui.Brush("RdPage");
        Content = _root;
        Model.StagedChanged += OnStaged;
        Closed += (_, _) => Model.StagedChanged -= OnStaged;
        // Esc closes (Done is the cancel action), but never mid-move.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) { Close(); e.Handled = true; }
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && _lastRemoved is { } item) { UndoRemove(item); e.Handled = true; }
        };
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Model.RefreshStaged();
        Rebuild();
    }

    /// <summary>The theme changed: repaint the open basket with the new brushes.</summary>
    internal static void ThemeChanged()
    {
        if (_open is not { } window) return;
        window.Background = Ui.Brush("RdPage");
        window.Rebuild();
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Rebuild);

    private List<CleanupQueue.StagedItem> Items => Model.Cleanup.AllItems().ToList();

    private void Rebuild()
    {
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
    }

    // ---- Items ----

    private FrameworkElement ItemsColumn(List<CleanupQueue.StagedItem> items)
    {
        var dock = new DockPanel();
        var estimate = Model.Cleanup.Estimate();

        var header = new DockPanel { Margin = new Thickness(24, 22, 24, 16) };
        var done = Rd.SecondaryButton("Done", () => { if (!_busy) Close(); });
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
            var count = Rd.Figure($"{p.Done} of {p.Total}", 11, null, "RdInk3");
            DockPanel.SetDock(count, Dock.Right);
            line.Children.Add(count);
            line.Children.Add(Rd.Text(p.Label, 12, null, "RdInk2"));
            bar.Children.Add(line);
            bar.Children.Add(new ProgressBar { Height = 4, Margin = new Thickness(0, 6, 0, 0), Maximum = Math.Max(1, p.Total), Value = p.Done });
            System.Windows.Automation.AutomationProperties.SetAutomationId(bar, "cleanup-commit-progress");
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
        }

        if (_lastRemoved is { } removed)
        {
            var undo = new DockPanel { Height = 40, Margin = new Thickness(24, 0, 24, 0) };
            var text = Rd.Text($"Removed “{System.IO.Path.GetFileName(removed.Path.TrimEnd('\\'))}” — it stays on disk", 12, null, "RdInk2");
            DockPanel.SetDock(text, Dock.Left);
            undo.Children.Add(text);
            var link = Rd.Link("Undo", () => UndoRemove(removed), 12);
            link.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(link, Dock.Left);
            undo.Children.Add(link);
            undo.Children.Add(new Border());
            var undoBox = new Border { Child = undo, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(undoBox, Dock.Bottom);
            dock.Children.Add(undoBox);
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

        var list = new StackPanel { Margin = new Thickness(24, 0, 24, 24) };
        foreach (var group in items.GroupBy(i => Source(i.Reason)).OrderBy(g => g.Key, StringComparer.Ordinal))
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
            foreach (var item in group) card.Children.Add(Row(item, items, estimate));
            var surface = Rd.Surface(card, "RdCard", 12);
            surface.Margin = new Thickness(0, list.Children.Count > 0 ? 14 : 0, 0, 0);
            list.Children.Add(surface);
        }
        list.IsEnabled = !_busy;
        dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        return dock;
    }

    private FrameworkElement Row(CleanupQueue.StagedItem item, List<CleanupQueue.StagedItem> all, CleanupQueue.ReclaimEstimate estimate)
    {
        bool isFolder = Directory.Exists(item.Path) && System.IO.Path.GetExtension(item.Path).Length == 0;
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

        var caption = Rd.Figure(Caption(item, all, estimate), 11, null, "RdInk2");
        caption.TextAlignment = TextAlignment.Right;
        caption.TextWrapping = TextWrapping.Wrap;
        caption.MaxWidth = 220;
        caption.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(caption, Dock.Right);
        row.Children.Add(caption);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Hidden, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Rd.IconButton(Icons.Visibility, "Open", () => OpenItem(item.Path), 24));
        actions.Children.Add(Rd.IconButton(Icons.Cancel, "Take out of the basket — the file stays where it is", () => RemoveWithUndo(item), 24));
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Rd.Text(System.IO.Path.GetFileName(item.Path.TrimEnd('\\')), 13, FontWeights.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        var where = Rd.Text(ParentDisplay(item.Path), 12, null, "RdInk3");
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
            var failed = Rd.Text($"Couldn’t move: {why}", 12, null, "AppWarning", wrap: true);
            System.Windows.Automation.AutomationProperties.SetAutomationId(failed, "cleanup-row-failure");
            text.Children.Add(failed);
        }
        row.Children.Add(text);

        var host = new Border
        {
            MinHeight = 48, Child = row, Background = Brushes.Transparent,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.7), BorderThickness = new Thickness(0, 0, 0, 1),
            ToolTip = Detail(item.Reason),
        };
        host.MouseEnter += (_, _) => { host.Background = Rd.WithOpacity(Ui.Brush("RdSoft"), 0.6); actions.Visibility = Visibility.Visible; };
        host.MouseLeave += (_, _) => { host.Background = Brushes.Transparent; actions.Visibility = Visibility.Hidden; };
        var menu = new ContextMenu();
        void item_(string header, Action action) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => action(); menu.Items.Add(mi); }
        item_("Open", () => OpenItem(item.Path));
        item_("Show in File Explorer", () => Explorer.Reveal(item.Path));
        menu.Items.Add(new Separator());
        item_("Take Out of the Basket", () => RemoveWithUndo(item));
        host.ContextMenu = menu;
        System.Windows.Automation.AutomationProperties.SetName(host, $"{name.Text}, {caption.Text}");
        return host;
    }

    /// <summary>Open a folder in File Explorer, a file in its app; programs are only shown, never run.</summary>
    private static void OpenItem(string path)
    {
        try
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            bool runnable = ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".lnk" or ".com" or ".scr" or ".vbs" or ".js";
            if (Directory.Exists(path) || runnable) Explorer.Reveal(path);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { Explorer.Reveal(path); }
    }

    // ---- Receipt column ----

    private FrameworkElement ReceiptColumn(List<CleanupQueue.StagedItem> items)
    {
        var estimate = Model.Cleanup.Estimate();
        var vol = Model.Volume ?? Model.Snapshot?.Volume;
        var lines = items.GroupBy(i => Source(i.Reason)).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Count() == 1 ? System.IO.Path.GetFileName(g.First().Path.TrimEnd('\\')) : $"{g.Key} · {g.Count()}",
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
        stack.Children.Add(MoveButton(items.Count));
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

    private FrameworkElement MoveButton(int count)
    {
        bool disabled = count == 0 || _busy;
        var content = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        var eyebrow = Rd.Figure("YOU’LL CONFIRM ONCE MORE", 10.5, FontWeights.Medium, "RdOnAccent");
        eyebrow.Opacity = 0.85;
        content.Children.Add(eyebrow);
        var title = Rd.TightText(count == 0 ? "Nothing to move" : $"Move {Count(count, "item")} to Recycle Bin…", 21, -0.3, "RdOnAccent");
        content.Children.Add(new Viewbox
        {
            Child = title, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0),
        });
        var face = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = disabled ? Rd.WithOpacity(Ui.Brush("AppAccent"), 0.45) : Ui.Brush("AppAccent"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(217, 0, 0, 0)), BorderThickness = new Thickness(1),
            Child = content,
        };
        var button = new Grid { Margin = new Thickness(0, 28, 5, 0), Cursor = disabled ? Cursors.Arrow : Cursors.Hand, ToolTip = "Moves everything here to the Recycle Bin, after you confirm" };
        button.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(224, 0, 0, 0)), Margin = new Thickness(5, 5, -5, -5) });
        button.Children.Add(face);
        button.MouseLeftButtonUp += (_, _) => { if (!disabled) Commit(); };
        System.Windows.Automation.AutomationProperties.SetName(button, $"Move {count} items to Recycle Bin");
        return button;
    }

    private FrameworkElement? LastCleanup()
    {
        if (CleanupRecord.Load() is not { Items.Count: > 0 } last) return null;
        var stack = new StackPanel();
        stack.Children.Add(Rd.Text("Last cleanup", 12, null, "RdInk2"));
        var row = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        var putBack = Rd.SecondaryButton("Put Back", async () => { await Model.PutBackLastCleanupAsync(); Rebuild(); });
        putBack.ToolTip = "Move them from the Recycle Bin back where they were. Nothing that is there now is replaced.";
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

    // ---- Success ----

    private FrameworkElement SuccessView(Success success)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement icon = AppSettings.Load().ShowDusty ? Ui.BrandIcon(104) : Ui.Glyph(Icons.Check, 22, Ui.Brush("AppAccent"));
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(icon);
        void add(FrameworkElement e, double top) { e.HorizontalAlignment = HorizontalAlignment.Center; e.Margin = new Thickness(0, top, 0, 0); stack.Children.Add(e); }
        add(Rd.Text($"{Count(success.Count, "item")} moved to the Recycle Bin", 15, FontWeights.SemiBold), 12);
        if (success.FreedLine.Length > 0)
        {
            var freed = Rd.Figure(success.FreedLine, 12, null, "RdInk2");
            freed.TextAlignment = TextAlignment.Center;
            add(freed, 12);
        }
        var note = Rd.Text("Changed your mind? Put Back returns them exactly where they were.", 12, null, "RdInk3", wrap: true);
        note.TextAlignment = TextAlignment.Center;
        note.MaxWidth = 380;
        add(note, 12);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var putBack = Rd.SecondaryButton("Put Back", async () =>
        {
            await Model.PutBackLastCleanupAsync();
            _success = null;
            Rebuild();
        });
        putBack.ToolTip = "Move them from the Recycle Bin back where they were. Nothing that is there now is replaced.";
        buttons.Children.Add(putBack);
        var done = Rd.PrimaryButton("Done", Close);
        done.IsDefault = true;
        done.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(done);
        add(buttons, 20);
        if (success.Details.Count > 0)
        {
            add(Rd.Link(_showMoved ? "Hide details" : "Show what moved", () => { _showMoved = !_showMoved; Rebuild(); }, 12), 20);
            if (_showMoved)
            {
                var details = Rd.Figure(string.Join(Environment.NewLine, success.Details), 11, null, "RdInk2");
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
        if (!Dialogs.Confirm(this,
                $"Move {Count(items.Count, "item")} to the Recycle Bin?",
                ConfirmMessage(items, estimate),
                items.Select(i => new Dialogs.ConfirmItem(System.IO.Path.GetFileName(i.Path.TrimEnd('\\')), i.Path, i.Size)).ToList(),
                "Move to Recycle Bin", danger: true))
            return;

        CleanupQueue.CommitReport report;
        _busy = true;
        try
        {
            var progress = new Progress<CleanupQueue.CommitProgress>(p =>
            {
                _progress = (p.Phase.StartsWith("Verif", StringComparison.OrdinalIgnoreCase) ? "Verifying sizes…" : "Moving to the Recycle Bin…", p.Done, p.Total);
                Rebuild();
            });
            _progress = ("Verifying sizes…", 0, items.Count);
            Rebuild();
            // Folders recycle first; their staged children report "moved with the folder".
            // Failures — including Recycle Bin refusals — stay staged with a reason.
            report = await Task.Run(() => Model.Cleanup.Commit(progress));
        }
        finally
        {
            _busy = false;
            _progress = null;
        }
        Model.RefreshStaged();
        var moved = report.Entries.Where(e => e.Error is null).ToList();
        _whyStayed.Clear();
        foreach (var failed in report.Entries.Where(e => e.Error is not null))
            _whyStayed[failed.Item.Id] = failed.Error is DiskMap.Core.Native.RecycleRefusedException
                ? (CleanupQueue.IsRegenerable(failed.Item.Path)
                    ? "too big for the Recycle Bin — nothing was deleted"
                    : "too big for the Recycle Bin and not regenerable — enlarge the bin or delete it yourself")
                : failed.Error!.Message;

        // The bin refused some items as too big: ask ONCE. Only regenerable ones (dependencies,
        // build output, caches) may be deleted permanently — the documented rule 1 exception.
        int deletedCount = 0;
        long deletedBytes = 0;
        var refused = report.Entries.Where(e => e.Error is DiskMap.Core.Native.RecycleRefusedException).ToList();
        var regenerable = refused.Where(e => CleanupQueue.IsRegenerable(e.Item.Path)).ToList();
        if (regenerable.Count > 0)
        {
            int others = refused.Count - regenerable.Count;
            if (Dialogs.Confirm(this,
                    $"The Recycle Bin can't hold {regenerable.Count:N0} folder{(regenerable.Count == 1 ? "" : "s")}",
                    "They're bigger than the Recycle Bin allows on this drive, so Windows can't recycle them — nothing has been deleted. " +
                    "They're regenerable (dependencies, build output, caches): your tools recreate them on the next install or build. " +
                    "Delete them permanently? This can't be undone.",
                    regenerable.Select(e => new Dialogs.ConfirmItem(System.IO.Path.GetFileName(e.Item.Path.TrimEnd('\\')), e.Item.Path, e.Item.Size)).ToList(),
                    $"Delete {regenerable.Count:N0} permanently", danger: true,
                    footnote: others > 0
                        ? $"{others} other item{(others == 1 ? " is" : "s are")} too big for the bin but not regenerable — they stay in the basket. Enlarge the bin (Recycle Bin → Properties) or delete them yourself."
                        : null))
            {
                _busy = true;
                try
                {
                    var progress = new Progress<CleanupQueue.CommitProgress>(p => { _progress = ("Deleting permanently…", p.Done, p.Total); Rebuild(); });
                    var ids = regenerable.Select(e => e.Item.Id).ToList();
                    var deleted = await Task.Run(() => Model.Cleanup.DeletePermanently(ids, progress));
                    deletedCount = deleted.Entries.Count(e => e.Error is null);
                    deletedBytes = deleted.FreedWhenEmptied;
                }
                finally
                {
                    _busy = false;
                    _progress = null;
                }
                Model.RefreshStaged();
            }
        }

        int left = Items.Count;
        if (moved.Count == 0 && deletedCount == 0)
        {
            Rebuild();
            Model.Toast(refused.Count > 0
                ? "Nothing moved — the Recycle Bin refused these items; they're still in the basket"
                : "Nothing could be moved — the items may be in use; they're still in the basket");
            return;
        }
        string freedLine = $"{(report.IsLowerBound ? "At least " : "")}{ByteFormat.Format(report.FreedWhenEmptied)} is freed when you empty the Recycle Bin.";
        var details = moved.Select(e => "Moved " + e.Item.Path).ToList();
        if (deletedCount > 0) details.Add($"Deleted permanently: {deletedCount:N0} too-big regenerable folder{(deletedCount == 1 ? "" : "s")} · {ByteFormat.Format(deletedBytes)} freed now.");
        if (left > 0) details.Add($"{Count(left, "item")} stayed in the basket — see the list for why.");
        _success = left == 0 ? new Success(moved.Count, freedLine, details) : null;
        Rebuild();
        Model.Toast($"Moved {moved.Count} items to the Recycle Bin" +
            (deletedCount > 0 ? $" · deleted {deletedCount} permanently" : "") +
            (left > 0 ? $" · {left} still in the basket" : ""));
    }

    // ---- Remove / undo ----

    private void RemoveWithUndo(CleanupQueue.StagedItem item)
    {
        if (_busy) return;
        Model.Unstage(item.Id);
        _lastRemoved = item;
        _undoTimer?.Stop();
        _undoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _undoTimer.Tick += (_, _) => { _undoTimer?.Stop(); _lastRemoved = null; Rebuild(); };
        _undoTimer.Start();
        Rebuild();
    }

    private void UndoRemove(CleanupQueue.StagedItem item)
    {
        _undoTimer?.Stop();
        _lastRemoved = null;
        Model.Cleanup.Stage(item.Path, item.Size, item.Reason, item.SharesStorageGroup, item.GroupCopyCount);
        Model.RefreshStaged();
        Rebuild();
    }

    // ---- Words ----

    private static string Count(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>Moving to the Recycle Bin frees nothing on its own; say when it does.</summary>
    public static string FreeSummary(CleanupQueue.ReclaimEstimate estimate)
    {
        if (estimate.IsCalculating) return "Calculating what emptying the Recycle Bin will free…";
        string amount = ByteFormat.Format(estimate.Bytes);
        return estimate.IsLowerBound
            ? $"At least {amount} freed when you empty the Recycle Bin"
            : $"{amount} freed when you empty the Recycle Bin";
    }

    private static string ConfirmMessage(List<CleanupQueue.StagedItem> items, CleanupQueue.ReclaimEstimate estimate)
    {
        string text = $"Moves {Count(items.Count, "item")} to the Recycle Bin — nothing is deleted permanently. ";
        text += estimate.IsLowerBound || estimate.IsCalculating ? "At least " : "About ";
        text += $"{ByteFormat.Format(estimate.Bytes)} is freed once you empty the Recycle Bin.";
        if (estimate.HeldByUnqueuedCopies > 0)
            text += $" {ByteFormat.Format(estimate.HeldByUnqueuedCopies)} stays in use because other copies or links of these files aren’t queued.";
        if (items.Select(i => CleanupRecipes.RecipeFor(i.Path)).FirstOrDefault(r => r is { TrashIsUnsafe: true }) is { } recipe)
            text += $" Warning: the queue includes data a tool manages itself — `{recipe.Command}` is the safe way to clean it.";
        return text;
    }

    /// <summary>What this row contributes, and why it may be less than its size.</summary>
    private string Caption(CleanupQueue.StagedItem item, List<CleanupQueue.StagedItem> all, CleanupQueue.ReclaimEstimate estimate)
    {
        if (item.IsMeasuring) return $"{ByteFormat.Format(item.Size)} · measuring…";
        long freed = estimate.PerItem.GetValueOrDefault(item.Id, item.Size);
        string prefixPath = item.Path.TrimEnd('\\');
        if (all.Any(o => o.Id != item.Id && prefixPath.StartsWith(o.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            return "Included in a queued folder — counted there";
        if (freed == 0 && item.Size > 0) return $"{ByteFormat.Format(item.Size)} · shared — freed only when every copy or link is queued";
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
