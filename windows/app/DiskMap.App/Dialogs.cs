using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DiskMap.Core;

namespace DiskMap.App;

/// <summary>
/// The app's own popup chrome — a borderless, rounded card with a soft
/// shadow in the theme's colors — instead of the stock Win32 message box
/// and menu. Shared by the risky-item warning and the tray flyout.
/// </summary>
public static class Dialogs
{
    /// <summary>A borderless themed window whose content sits on a rounded card.</summary>
    public static Window CardWindow(UIElement content, double width)
    {
        var card = new Border
        {
            Background = Ui.Brush("AppCard"),
            BorderBrush = Ui.Brush("AppCardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Margin = new Thickness(16),   // room for the shadow
            Child = content,
            Effect = new DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 4, Opacity = 0.22, Direction = 270, Color = Colors.Black,
            },
        };
        return new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            Width = width + 32,
            Content = card,
            FontFamily = Application.Current.MainWindow?.FontFamily ?? new FontFamily("Segoe UI"),
            Foreground = Ui.Brush("AppForeground"),
        };
    }

    /// <summary>One row of a confirmation: what will be touched, where it is, how big.</summary>
    public sealed record ConfirmItem(string Name, string Path, long Bytes);

    public const int RowLimit = 400;

    /// <summary>A sheet-like window over the owner: the page colour, 26 padding, a hairline and a soft shadow.</summary>
    private static Window Sheet(UIElement content, double width, Window? owner)
    {
        var card = new Border
        {
            Background = Ui.Brush("RdPage"),
            BorderBrush = Ui.Brush("RdLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(26),
            Margin = new Thickness(16),
            Child = content,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.22, Direction = 270, Color = Colors.Black },
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Height,
            Width = width + 32,
            Content = card,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        // A borderless, layered window isn't always made the active one when it opens, and then
        // no key reaches it at all (Esc did nothing on "Where's the rest?"). Activate it once it
        // has drawn and put the focus inside, unless the sheet already focused its default.
        window.ContentRendered += (_, _) =>
        {
            window.Activate();
            var target = window.Tag as IInputElement;   // the sheet's default button, when it names one
            if (target is not null) System.Windows.Input.Keyboard.Focus(target);
            else if (!window.IsKeyboardFocusWithin)
                window.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        };
        // Esc dismisses every sheet as its safe choice. IsCancel alone needed the focus on a
        // button, and a click in the list or on a folding row took it away.
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            window.Close();
        };
        return window;
    }

    /// <summary>
    /// PathListConfirmSheet: anything that changes the disk or the basket in bulk. Says what will
    /// happen, lists every item by full path (biggest first, up to 400), totals it, and defaults
    /// to Cancel — Return never adds or moves anything by accident. True = go.
    /// </summary>
    public static bool Confirm(Window? owner, string title, string message,
        IReadOnlyList<ConfirmItem> items, string confirmLabel, bool danger = false, string? footnote = null)
    {
        var body = new StackPanel();
        var head = new DockPanel();
        var tile = Rd.IconTile(danger ? Icons.Trash : Icons.Cleanup, 40);
        tile.Margin = new Thickness(0, 0, 14, 0);
        tile.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var titles = new StackPanel();
        titles.Children.Add(Rd.Text(title, 20, FontWeights.SemiBold, wrap: true));
        var msg = Rd.Text(message, 13, null, "RdInk2", wrap: true);
        msg.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(msg);
        head.Children.Add(titles);
        body.Children.Add(head);

        // Every affected item, full path — "from which folder" is never a guess.
        var sorted = items.OrderByDescending(i => i.Bytes).ThenBy(i => i.Path, StringComparer.Ordinal).ToList();
        var list = new StackPanel();
        foreach (var item in sorted.Take(RowLimit))
        {
            var row = new DockPanel { Margin = new Thickness(14, 8, 14, 8) };
            var size = Rd.Figure(ShellFormat.HeroBytes(item.Bytes), 12);
            size.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            var names = new StackPanel();
            names.Children.Add(Rd.Text(item.Name, 13, FontWeights.Medium));
            var path = Rd.Figure(CanonicalPath.DisplayPath(item.Path), 11, null, "RdInk3");
            path.ToolTip = item.Path;
            path.Margin = new Thickness(0, 2, 0, 0);
            names.Children.Add(path);
            row.Children.Add(names);
            list.Children.Add(new Border
            {
                Child = row, BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.7), BorderThickness = new Thickness(0, 0, 0, 1),
            });
        }
        if (sorted.Count > RowLimit)
        {
            var more = Rd.Text($"+ {sorted.Count - RowLimit:N0} more", 12, null, "RdInk3");
            more.Margin = new Thickness(14, 8, 14, 8);
            list.Children.Add(more);
        }
        var table = new StackPanel();
        table.Children.Add(new ScrollViewer
        {
            MinHeight = 120, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list, Focusable = false,
        });
        var totalRow = Rd.LeaderRow($"{items.Count:N0} item{(items.Count == 1 ? "" : "s")}", ShellFormat.HeroBytes(items.Sum(i => Math.Max(0, i.Bytes))),
            emphasis: true, accentValue: danger);
        totalRow.Margin = new Thickness(14, 12, 14, 12);
        table.Children.Add(new Border
        {
            Child = totalRow, BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85), BorderThickness = new Thickness(0, 1, 0, 0),
        });
        var tableCard = Rd.Surface(table, "RdCard", 12);
        tableCard.Margin = new Thickness(0, 18, 0, 0);
        body.Children.Add(tableCard);

        if (footnote is not null)
        {
            var note = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
            var icon = Ui.Glyph(Icons.Warning, 13, Ui.Brush("AppWarning"));
            icon.Margin = new Thickness(0, 1, 10, 0);
            icon.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(icon, Dock.Left);
            note.Children.Add(icon);
            note.Children.Add(Rd.Text(footnote, 12, null, "RdInk2", wrap: true));
            body.Children.Add(note);
        }

        bool result = false;
        Window? window = null;
        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var go = Rd.AccentButton(confirmLabel, () => { result = true; window!.Close(); }, large: true, hard: danger);
        DockPanel.SetDock(go, Dock.Right);
        buttons.Children.Add(go);
        var cancel = LargeSecondary("Cancel", () => window!.Close());
        cancel.IsCancel = true;
        cancel.IsDefault = true;   // Cancel is the default: Return never adds or moves anything
        cancel.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(cancel);
        var hint = Rd.Text(danger ? "Put Back returns everything while it's in the Recycle Bin." : "Nothing moves yet.", 12, null, "RdInk3");
        buttons.Children.Add(hint);
        body.Children.Add(buttons);

        window = Sheet(body, 620, owner);
        window.Title = title;
        window.Tag = cancel;
        window.Loaded += (_, _) => cancel.Focus();   // the safe choice has focus
        window.ShowDialog();
        return result;
    }

    /// <summary>SecondaryButtonStyle(large: true): 40 tall, 18 side padding, 14 semibold, radius 8.</summary>
    private static Button LargeSecondary(string text, Action onClick)
    {
        var button = Rd.SecondaryButton(text, onClick);
        button.Height = 40;
        button.Padding = new Thickness(18, 0, 18, 0);
        if (button.Content is TextBlock label) { label.FontSize = Ui.Scaled(14); label.FontWeight = FontWeights.SemiBold; }
        return button;
    }

    /// <summary>
    /// RiskyStageSheet: "Add ext4.vhdx to the basket?" — what it is, why removing it hurts, the
    /// better way to get the space back, and "Keep it" as the default. True = add it anyway.
    /// </summary>
    public static bool ConfirmRisky(Window? owner, string name, StorageVerdict verdict)
    {
        bool result = false;
        var window = RiskyWindow(owner, name, verdict, anyway => result = anyway);
        window.ShowDialog();
        return result;
    }

    /// <summary>The warning window itself; <paramref name="done"/> gets true for "Add anyway".</summary>
    public static Window RiskyWindow(Window? owner, string name, StorageVerdict verdict, Action<bool> done, string? path = null)
    {
        var body = new StackPanel();
        var head = new DockPanel();
        var tile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(9), Background = Ui.Brush("RdCard"),
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Border
            {
                Child = Ui.Glyph(Icons.Warning, 16, Ui.Brush("AppWarning")),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var titles = new StackPanel();
        titles.Children.Add(Rd.Tag(verdict.Class.Title));
        var title = Rd.Text($"Add {name} to the basket?", 20, FontWeights.SemiBold, wrap: true);
        title.Margin = new Thickness(0, 6, 0, 0);
        title.MaxHeight = Ui.Scaled(20) * 2.5;
        titles.Children.Add(title);
        if (path is not null)
        {
            var where = Rd.Figure(CanonicalPath.DisplayPath(path), 11, null, "RdInk3");
            where.TextWrapping = TextWrapping.Wrap;
            where.Margin = new Thickness(0, 6, 0, 0);
            titles.Children.Add(where);
        }
        head.Children.Add(titles);
        body.Children.Add(head);

        if (verdict.Note is { } note)
        {
            var noteText = Rd.Text(note, 13, null, "RdInk", wrap: true);
            noteText.Margin = new Thickness(0, 18, 0, 0);
            body.Children.Add(noteText);
        }
        if (verdict.Instead is { } instead)
        {
            var box = new StackPanel { Margin = new Thickness(14) };
            box.Children.Add(Rd.Text("A better way", 13, FontWeights.Medium));
            var insteadText = Rd.Text(instead.Replace("`", ""), 13, null, "RdInk2", wrap: true);
            insteadText.Margin = new Thickness(0, 6, 0, 0);
            box.Children.Add(insteadText);
            var layers = new Grid();
            layers.Children.Add(box);
            layers.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Brush("AppAccent"), CornerRadius = new CornerRadius(10, 0, 0, 10) });
            var card = Rd.Surface(layers, "RdCard", 10);
            card.Margin = new Thickness(0, 18, 0, 0);
            body.Children.Add(card);
        }

        Window? window = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        // Keeping it is the default; adding needs a deliberate click.
        var keep = Rd.AccentButton("Keep it", () => window!.Close(), large: true);
        keep.IsDefault = true;
        keep.IsCancel = true;
        var anyway = LargeSecondary("Add anyway", () => { done(true); window!.Close(); });
        anyway.Margin = new Thickness(10, 0, 0, 0);
        buttons.Children.Add(keep);
        buttons.Children.Add(anyway);
        body.Children.Add(buttons);

        window = Sheet(body, 520, owner);
        window.Title = $"Add {name} to the basket?";
        window.Tag = keep;
        window.Loaded += (_, _) => keep.Focus();
        return window;
    }

    // ---- "Where's the rest?" (Mac: SpaceAccountSheet) ----

    /// <summary>
    /// The sheet Overview opens from "Where's the rest?": the drive's used space as one stacked
    /// bar, then a folding row per part, then "What Windows keeps" when it was measured, then Done.
    /// </summary>
    public static void ShowSpaceAccount(Window? owner, SpaceAccount account, IReadOnlyList<SystemStorageCatalog.Item>? system = null) =>
        SpaceAccountWindow(owner, account, system).ShowDialog();

    /// <summary>The "Where's the rest?" sheet, not yet shown (the harness renders it to a file).</summary>
    public static Window SpaceAccountWindow(Window? owner, SpaceAccount account, IReadOnlyList<SystemStorageCatalog.Item>? system = null,
        bool fullHeight = false)
    {
        var body = new StackPanel();
        var head = new DockPanel();
        var tile = Rd.IconTile(Icons.PieChart, 40);
        tile.Margin = new Thickness(0, 0, 14, 0);
        tile.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var titles = new StackPanel();
        titles.Children.Add(Rd.Text("Where's the rest?", 20, FontWeights.SemiBold));
        var sub = Rd.Text($"{ShellFormat.Bytes(account.UsedBytes)} in use, split into parts that add up", 13, null, "RdInk2", wrap: true);
        sub.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(sub);
        head.Children.Add(titles);
        body.Children.Add(head);

        // The breakdown, then "What Windows keeps", 24 apart in one scroller (Mac: DiskMapSpace.lg).
        var content = new StackPanel();
        content.Children.Add(SpaceAccountBreakdown(account));
        if (system is { Count: > 0 })
        {
            var keeps = SystemStorageList(system, StageSystemStorage);
            keeps.Margin = new Thickness(0, 24, 0, 0);
            content.Children.Add(keeps);
        }
        var scroller = new ScrollViewer
        {
            MaxHeight = fullHeight ? double.PositiveInfinity : 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false,
            Content = content, Margin = new Thickness(0, 18, 0, 0),
        };
        body.Children.Add(scroller);

        Window? window = null;
        var done = Rd.AccentButton("Done", () => window!.Close(), large: true);
        done.IsDefault = true;
        done.IsCancel = true;
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 18, 0, 0);
        System.Windows.Automation.AutomationProperties.SetAutomationId(done, "space-account-done");
        body.Children.Add(done);

        window = Sheet(body, 520, owner);   // the Mac sheet's width
        window.Title = "Where's the rest?";
        window.Tag = done;
        window.Loaded += (_, _) => done.Focus();
        return window;
    }

    /// <summary>SpaceAccountBreakdown: one stacked bar, then a folding row per part, in a card.</summary>
    public static Border SpaceAccountBreakdown(SpaceAccount account)
    {
        var stack = new StackPanel();
        stack.Children.Add(StackedBar(account));
        var rows = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < account.Rows.Count; i++)
        {
            var row = account.Rows[i];
            var detail = new StackPanel();
            detail.Children.Add(Rd.Text(row.Detail, 12, null, "RdInk2", wrap: true));
            if (row.Confidence == SpaceAccount.Confidence.Remainder)
            {
                var note = Rd.Text("Worked out from the other figures, not measured directly.", 12, null, "RdInk3", wrap: true);
                note.Margin = new Thickness(0, 6, 0, 0);
                detail.Children.Add(note);
            }
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = AccountColor(row, i) };
            var folding = FoldingRow(row.Title, dot, Rd.Figure(ShellFormat.Bytes(row.Bytes), 12, FontWeights.SemiBold), detail);
            if (i > 0) folding.Margin = new Thickness(0, 10, 0, 0);
            System.Windows.Automation.AutomationProperties.SetAutomationId(folding, "space-account-row");
            rows.Children.Add(folding);
        }
        stack.Children.Add(rows);
        var card = Rd.Surface(stack, "RdCard", 12);
        card.Padding = new Thickness(16);
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, "space-account");
        return card;
    }

    /// <summary>Every part as one 10 pt bar, in row order, 2 pt apart, each at least 3 pt.</summary>
    private static FrameworkElement StackedBar(SpaceAccount account)
    {
        var grid = new Grid { Height = 10, ClipToBounds = true };
        int column = 0;
        for (int i = 0; i < account.Rows.Count; i++)
        {
            var row = account.Rows[i];
            double fraction = account.UsedBytes > 0 ? (double)row.Bytes / account.UsedBytes : 0;
            if (fraction <= 0) continue;
            if (column > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star), MinWidth = 3 });
            var part = new System.Windows.Shapes.Rectangle { Fill = AccountColor(row, i) };
            Grid.SetColumn(part, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(part);
            column++;
        }
        // Rounded 3 pt ends: a Border's radius doesn't clip its child, so clip to the laid-out size.
        grid.SizeChanged += (_, e) => grid.Clip = new RectangleGeometry(new Rect(e.NewSize), 3, 3);
        System.Windows.Automation.AutomationProperties.SetAutomationId(grid, "space-account-bar");
        return grid;
    }

    /// <summary>The scan in the accent; Windows' own parts in the data palette; what's left in a quiet grey.</summary>
    public static Brush AccountColor(SpaceAccount.Row row, int index) => row.Part switch
    {
        SpaceAccount.Part.Scanned => Ui.Brush("AppAccent"),
        SpaceAccount.Part.NotSeen => Rd.WithOpacity(Ui.Brush("RdInk3"), 0.45),
        // Skips the palette's lilac (index 1), too close to the accent.
        _ => Ui.Data(new[] { 0, 2, 3, 5, 4, 6 }[index % 6]),
    };

    // ---- "What Windows keeps" (Mac: SystemStorageList, P2) ----

    /// <summary>
    /// Storage Windows fills by itself, with how to free it through Windows' own Settings: a mono
    /// label over a card of folding rows. Only <c>trash</c> entries can go to the basket.
    /// </summary>
    public static FrameworkElement SystemStorageList(IReadOnlyList<SystemStorageCatalog.Item> items,
        Action<SystemStorageCatalog.Item, SystemStorageCatalog.Found>? stage)
    {
        var section = new StackPanel();
        section.Children.Add(Rd.MonoLabel("What Windows keeps"));
        var rows = new StackPanel();
        for (int i = 0; i < items.Count; i++)
        {
            var row = SystemStorageRow(items[i], stage);
            if (i > 0) row.Margin = new Thickness(0, 10, 0, 0);
            rows.Children.Add(row);
        }
        var card = Rd.Surface(rows, "RdCard", 12);
        card.Padding = new Thickness(16);
        card.Margin = new Thickness(0, 10, 0, 0);
        section.Children.Add(card);
        System.Windows.Automation.AutomationProperties.SetAutomationId(section, "system-storage");
        return section;
    }

    private static FrameworkElement SystemStorageRow(SystemStorageCatalog.Item item,
        Action<SystemStorageCatalog.Item, SystemStorageCatalog.Found>? stage)
    {
        var action = item.Entry.Action;
        var icon = Ui.Glyph(SystemStorageIcon(action), 11,
            Ui.Brush(action == SystemStorageCatalog.Action.Trash ? "AppAccent" : "RdInk3"));
        var leading = new Border { Width = 12, Child = icon, ToolTip = SystemStorageHelp(action) };
        var size = item.SizeUnknown || (item.IsPartial && item.Bytes == 0)
            ? Rd.Text(SystemStorageSize(item), 12, null, "RdInk3")
            : Rd.Figure(SystemStorageSize(item), 12, FontWeights.SemiBold);
        if (SystemStorageSizeHelp(item) is { } help) size.ToolTip = help;

        var detail = new StackPanel();
        void Add(FrameworkElement element)
        {
            if (detail.Children.Count > 0) element.Margin = new Thickness(0, 6, 0, 0);
            detail.Children.Add(element);
        }
        Add(Rd.Text(item.Entry.Why, 12, null, "RdInk2", wrap: true));
        foreach (var step in item.Entry.Steps) Add(Rd.Text(step, 12, null, "RdInk", wrap: true));
        var links = new WrapPanel();
        void AddLink(FrameworkElement link, string automationId)
        {
            if (links.Children.Count > 0) link.Margin = new Thickness(14, 0, 0, 0);
            System.Windows.Automation.AutomationProperties.SetAutomationId(link, automationId);
            links.Children.Add(link);
        }
        if (item.Entry.SettingsUri is { } uri)
            AddLink(Rd.Link("Open Settings", () => OpenSettings(uri), 12), $"system-storage-settings-{item.Id}");
        if (stage is not null)
        {
            var targets = SystemStorageCatalog.StageTargets(item);
            foreach (var found in targets)
                AddLink(Rd.Link(SystemStorageStageTitle(found, targets.Count), () => stage(item, found), 12), "system-storage-stage");
        }
        if (links.Children.Count > 0) Add(links);

        var row = FoldingRow(item.Entry.Title, leading, size, detail);
        System.Windows.Automation.AutomationProperties.SetAutomationId(row, $"system-storage-row-{item.Id}");
        return row;
    }

    /// <summary>Lock: Windows manages it. Gear: freed in Settings. Trash: can go to the basket.</summary>
    internal static string SystemStorageIcon(SystemStorageCatalog.Action action) => action switch
    {
        SystemStorageCatalog.Action.Never => "BsLockFill",
        SystemStorageCatalog.Action.Guide => "BsGearFill",
        _ => "BsTrash3Fill",
    };

    internal static string SystemStorageHelp(SystemStorageCatalog.Action action) => action switch
    {
        SystemStorageCatalog.Action.Never => "Windows manages this; nothing to do.",
        SystemStorageCatalog.Action.Guide => "Freed through Windows' own settings.",
        _ => "Can go in the basket (Recycle Bin first).",
    };

    /// <summary>The row's figure: bytes, "+" when some folder inside couldn't be read; words when nothing could.</summary>
    internal static string SystemStorageSize(SystemStorageCatalog.Item item) =>
        item.SizeUnknown ? "Not measured"
        : item.IsPartial && item.Bytes == 0 ? "Can't read"
        : ShellFormat.Bytes(item.Bytes) + (item.IsPartial ? "+" : "");

    internal static string? SystemStorageSizeHelp(SystemStorageCatalog.Item item) =>
        !item.Entry.Measured ? "Not measured on purpose: the explanation says why."
        : item.SizeUnknown ? "Measuring this folder took too long."
        : item.IsPartial && item.Bytes == 0 ? "Windows didn't let freedisk.space read inside this folder."
        : item.IsPartial ? "At least this much: Windows didn't let freedisk.space read some folders inside."
        : null;

    /// <summary>"Add to the basket", or with the folder's name when an entry found several.</summary>
    internal static string SystemStorageStageTitle(SystemStorageCatalog.Found found, int count) =>
        count == 1 ? "Add to the basket" : $"Add {System.IO.Path.GetFileName(found.Path.TrimEnd('\\', '/'))} to the basket";

    /// <summary>Opens a Windows Settings page. Launching a local Settings page, not networking; only ms-settings: is allowed.</summary>
    private static void OpenSettings(string uri)
    {
        if (!SystemStorageCatalog.IsSettingsUri(uri)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { ScanModel.Shared.Toast("Couldn't open Settings"); }
    }

    /// <summary>One trash entry's path to the basket, through the model's single stage path (Mac: stageRow).</summary>
    private static void StageSystemStorage(SystemStorageCatalog.Item item, SystemStorageCatalog.Found found)
    {
        var model = ScanModel.Shared;
        if (model.IsStaged(found.Path)) { model.Toast("Already in the basket"); return; }
        if (!model.StagePath(found.Path, found.Bytes, item.Entry.Title)) model.Toast("This can't go in the basket");
    }

    /// <summary>
    /// FoldingRow: a name and a figure that fold their explanation away until clicked, so the
    /// sheet stays a list of numbers, not a page of text.
    /// </summary>
    public static FrameworkElement FoldingRow(string title, FrameworkElement leading, FrameworkElement trailing, FrameworkElement detail, bool startsOpen = false)
    {
        var chevron = Ui.Glyph(Icons.ChevronRight, 10, Ui.Brush("RdInk3"));
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        var rotate = new RotateTransform(startsOpen ? 90 : 0);
        chevron.RenderTransform = rotate;
        chevron.Margin = new Thickness(10, 0, 0, 0);
        chevron.VerticalAlignment = VerticalAlignment.Center;

        var header = new DockPanel { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        leading.VerticalAlignment = VerticalAlignment.Center;
        leading.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(leading, Dock.Left);
        header.Children.Add(leading);
        DockPanel.SetDock(chevron, Dock.Right);
        header.Children.Add(chevron);
        trailing.VerticalAlignment = VerticalAlignment.Center;
        trailing.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(trailing, Dock.Right);
        header.Children.Add(trailing);
        var name = Rd.Text(title, 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(name);

        detail.Margin = new Thickness(20, 6, 0, 0);
        detail.Visibility = startsOpen ? Visibility.Visible : Visibility.Collapsed;
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(detail);
        header.ToolTip = null;
        header.MouseLeftButtonUp += (_, e) =>
        {
            bool open = detail.Visibility != Visibility.Visible;
            detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            rotate.Angle = open ? 90 : 0;
            e.Handled = true;
        };
        return stack;
    }
}
