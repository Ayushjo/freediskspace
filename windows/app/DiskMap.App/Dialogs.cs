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
        window.Loaded += (_, _) => keep.Focus();
        return window;
    }
}
