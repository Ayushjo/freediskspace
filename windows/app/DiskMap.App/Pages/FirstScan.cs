using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// First launch (redesign board 11, Mac: FirstScanHero.firstLaunch): what to scan, and the
/// promises. Windows has no Full Disk Access step, so the access card never shows.
/// </summary>
internal sealed class FirstLaunch(Rd.WidthClass widthClass)
{
    private static ScanModel Model => ScanModel.Shared;

    /// <summary>The picked option survives the page rebuilding.</summary>
    private static bool _choosePC = true;

    /// <summary>"Scan this PC": the drive Windows runs from, read whole (MFT when elevated).</summary>
    internal static string SystemDrive =>
        System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\";

    public FrameworkElement Build()
    {
        bool compact = widthClass == Rd.WidthClass.Compact;
        var column = new StackPanel
        {
            Margin = compact ? new Thickness(36, 48, 36, 48) : new Thickness(72, 48, 72, 48),
        };
        var choose = Choose();
        choose.MaxWidth = 620;
        choose.HorizontalAlignment = HorizontalAlignment.Left;
        column.Children.Add(choose);
        var promises = Promises();
        promises.Margin = new Thickness(0, 36, 0, 0);
        column.Children.Add(promises);

        var page = new Grid { MinHeight = 600, Background = Ui.Brush("RdPage") };
        page.Children.Add(new Border { Background = Rd.Grid24(Rd.WithOpacity(Ui.Brush("RdInk"), 0.035)) });
        page.Children.Add(column);
        // Return starts the picked option (.keyboardShortcut(.defaultAction)).
        page.Focusable = true;
        page.Loaded += (_, _) => page.Focus();
        page.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.OriginalSource is TextBox) return;
            Start();
            e.Handled = true;
        };
        return page;
    }

    private static void Start()
    {
        if (_choosePC) _ = Model.ScanAsync(SystemDrive);
        else Model.RequestScan();
    }

    private FrameworkElement Choose()
    {
        var stack = new StackPanel();
        void add(UIElement element, double top)
        {
            if (element is FrameworkElement fe && stack.Children.Count > 0) fe.Margin = new Thickness(0, top, 0, 0);
            stack.Children.Add(element);
        }

        if (AppSettings.Load().ShowDusty)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dusty = Ui.BrandIcon(44);
            dusty.HorizontalAlignment = HorizontalAlignment.Center;
            dusty.VerticalAlignment = VerticalAlignment.Center;
            var frame = new Grid { Width = 70, Height = 70 };
            frame.Children.Add(new Rectangle
            {
                RadiusX = 14, RadiusY = 14, Stroke = Ui.Brush("RdLine"), StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });
            frame.Children.Add(dusty);
            row.Children.Add(frame);
            var hello = Rd.Text("Hi, I'm Dusty. I'll help you find space.", 13, null, "RdInk3");
            hello.Margin = new Thickness(14, 0, 0, 0);
            row.Children.Add(hello);
            add(row, 0);
        }

        var heading = new StackPanel();
        heading.Children.Add(Rd.TightText("See what fills your PC", widthClass == Rd.WidthClass.Compact ? 40 : 52, -1));
        var lede = Rd.Text("One scan shows every folder drawn to its real size, and what you can safely free. It usually takes under a minute.",
            15, null, "RdInk2", wrap: true);
        lede.Margin = new Thickness(0, 12, 0, 0);
        heading.Children.Add(lede);
        add(heading, 22);

        var options = new StackPanel();
        options.Children.Add(Option(true, PcOption()));
        var folder = Option(false, FolderOption());
        folder.Margin = new Thickness(0, 10, 0, 0);
        options.Children.Add(folder);
        add(options, 22);

        var go = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        go.Children.Add(Rd.AccentButton(_choosePC ? "Scan this PC" : "Choose a folder…", Start, large: true, hard: true));
        var hint = Rd.Text("↵ to start · you can stop any time", 12, null, "RdInk3");
        hint.Margin = new Thickness(14, 0, 0, 0);
        go.Children.Add(hint);
        add(go, 26);
        return stack;
    }

    private static FrameworkElement PcOption()
    {
        string drive = SystemDrive;
        var volume = VolumeStats.Of(drive);
        var row = new DockPanel();
        var tile = Rd.IconTile(Icons.Drive, 40);
        tile.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Rd.Text(volume?.DriveLabel ?? "This PC", 13, FontWeights.Medium));
        if (volume is { } v)
        {
            var total = Rd.Figure(ShellFormat.HeroBytes(v.TotalBytes), 11, null, "RdInk3");
            total.Margin = new Thickness(8, 0, 0, 0);
            title.Children.Add(total);
        }
        var scope = Rd.Text("· the whole drive", 12, null, "RdInk3");
        scope.Margin = new Thickness(8, 0, 0, 0);
        title.Children.Add(scope);
        text.Children.Add(title);
        if (volume is { } vol)
        {
            double used = vol.UsedBytes, all = Math.Max(1, vol.TotalBytes);
            var bar = new Grid { Height = 8, Margin = new Thickness(0, 8, 0, 0) };
            bar.Children.Add(new Border { Background = Rd.FreeStripes(), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1) });
            var fill = new Border { Background = Ui.Brush("RdInk"), HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(fill);
            bar.SizeChanged += (_, e) =>
            {
                fill.Width = e.NewSize.Width * used / all;
                bar.Clip = new RectangleGeometry(new Rect(e.NewSize), 2, 2);
            };
            text.Children.Add(bar);
            var legend = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var free = Rd.Figure($"{ShellFormat.HeroBytes(vol.FreeBytes)} free".ToUpperInvariant(), 10.5, FontWeights.Medium, "RdInk3");
            DockPanel.SetDock(free, Dock.Right);
            legend.Children.Add(free);
            legend.Children.Add(Rd.Figure($"{ShellFormat.HeroBytes(vol.UsedBytes)} used".ToUpperInvariant(), 10.5, FontWeights.Medium, "RdInk3"));
            text.Children.Add(legend);
        }
        row.Children.Add(text);
        return row;
    }

    private static FrameworkElement FolderOption()
    {
        var row = new DockPanel();
        var tile = Rd.IconTile(Icons.Folder, 40);
        tile.Margin = new Thickness(0, 0, 16, 0);
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);
        var choose = Rd.SecondaryButton("Choose…", () => Model.RequestScan());
        choose.Margin = new Thickness(8, 0, 0, 0);
        choose.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(choose, Dock.Right);
        row.Children.Add(choose);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Rd.Text("Choose a folder…", 13, FontWeights.Medium));
        var detail = Rd.Text("Scan just one place, like Downloads or a project.", 12, null, "RdInk2");
        detail.Margin = new Thickness(0, 4, 0, 0);
        text.Children.Add(detail);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A selectable option card: radio dot, then the content; violet edge when picked.</summary>
    private FrameworkElement Option(bool pc, FrameworkElement content)
    {
        bool on = _choosePC == pc;
        var radio = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        radio.Children.Add(new Ellipse { Stroke = Ui.Brush(on ? "AppAccent" : "RdInk3"), StrokeThickness = 1.5 });
        if (on) radio.Children.Add(new Ellipse { Fill = Ui.Brush("AppAccent"), Margin = new Thickness(5) });
        var row = new DockPanel();
        DockPanel.SetDock(radio, Dock.Left);
        row.Children.Add(radio);
        row.Children.Add(content);
        var card = new Border
        {
            Padding = new Thickness(on ? 19.5 : 20, on ? 17.5 : 18, on ? 19.5 : 20, on ? 17.5 : 18),
            CornerRadius = new CornerRadius(12),
            Background = Ui.Brush("RdCard"),
            BorderBrush = Ui.Brush(on ? "AppAccent" : "RdLine"),
            BorderThickness = new Thickness(on ? 1.5 : 1),
            Child = row,
            Cursor = Cursors.Hand,
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindButton(d) is not null) return;
            if (_choosePC == pc) return;
            _choosePC = pc;
            RequestRebuild(card);
        };
        System.Windows.Automation.AutomationProperties.SetName(card, pc ? "Scan this PC" : "Choose a folder");
        return card;
    }

    private static Button? FindButton(DependencyObject d)
    {
        for (var current = d; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Button button) return button;
        return null;
    }

    private static void RequestRebuild(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is OverviewPage page) { page.RebuildNow(); return; }
    }

    private FrameworkElement Promises()
    {
        var stack = new StackPanel();
        stack.Children.Add(new Border { Height = 1, Background = Ui.Brush("RdLine") });
        var items = new (string Icon, string Title, string Detail)[]
        {
            ("BsLock", "Nothing leaves your PC", "No accounts, no analytics. Scans stay on this disk."),
            ("BsArrowCounterclockwise", "Nothing is deleted without you", "Everything goes to the Recycle Bin first, so you can put it back."),
            ("BsCheck2", "Free and open source", "No trial, no upgrade screen. Read every line on GitHub."),
        };
        bool compact = widthClass == Rd.WidthClass.Compact;
        var grid = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        for (int i = 0; i < items.Length; i++)
        {
            var (icon, title, detail) = items[i];
            var row = new DockPanel();
            var tile = Rd.IconTile(icon, 38);
            tile.VerticalAlignment = VerticalAlignment.Top;
            tile.Margin = new Thickness(0, 0, 14, 0);
            DockPanel.SetDock(tile, Dock.Left);
            row.Children.Add(tile);
            var text = new StackPanel();
            text.Children.Add(Rd.Text(title, 13, FontWeights.Medium));
            var d = Rd.Text(detail, 13, null, "RdInk2", wrap: true);
            d.Margin = new Thickness(0, 4, 0, 0);
            text.Children.Add(d);
            row.Children.Add(text);
            if (compact)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(row, i);
                if (i > 0) row.Margin = new Thickness(0, 18, 0, 0);
            }
            else
            {
                if (i > 0) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(row, i * 2);
            }
            grid.Children.Add(row);
        }
        stack.Children.Add(grid);
        return stack;
    }
}

/// <summary>
/// The first scan, live (redesign board 12, Mac: ScanningBoard): counters, a map of the
/// biggest top folders so far with the one being read dashed, and the list of places.
/// </summary>
internal sealed class ScanningBoard(Rd.WidthClass widthClass, double height)
{
    private static ScanModel Model => ScanModel.Shared;

    public FrameworkElement Build()
    {
        double pad = widthClass.PagePadding();
        double gap = widthClass.Gap();
        var grid = new Grid
        {
            Margin = new Thickness(pad, widthClass == Rd.WidthClass.Compact ? 14 : 20, pad, pad),
            MinHeight = Math.Max(360, height - (widthClass == Rd.WidthClass.Compact ? 14 : 20) - pad),
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new DockPanel();
        var cancel = Rd.SecondaryButton("Cancel", () => Model.CancelScan());
        cancel.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(cancel, Dock.Right);
        head.Children.Add(cancel);
        var titles = new StackPanel();
        titles.Children.Add(Rd.PageTitle(Title(Model.RootPath, Model.ScanPhase)));
        var sub = Rd.Text(Subtitle(Model.ScanPhase), 13, null, "RdInk2");
        sub.Margin = new Thickness(0, 6, 0, 0);
        titles.Children.Add(sub);
        head.Children.Add(titles);
        grid.Children.Add(head);

        var stats = Stats();
        Grid.SetRow(stats, 2);
        grid.Children.Add(stats);

        var bottom = new Grid();
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.Children.Add(FoundSoFar());
        if (widthClass != Rd.WidthClass.Compact)
        {
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widthClass == Rd.WidthClass.Regular ? 300 : 340) });
            var places = Places();
            Grid.SetColumn(places, 2);
            bottom.Children.Add(places);
        }
        Grid.SetRow(bottom, 4);
        grid.Children.Add(bottom);

        // Esc cancels (.keyboardShortcut(.cancelAction)).
        grid.Focusable = true;
        grid.Loaded += (_, _) => grid.Focus();
        grid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Model.CancelScan(); e.Handled = true; }
        };
        return grid;
    }

    public static string Title(string? root, string phase)
    {
        if (phase == "summarizing") return "Putting it together";
        if (root is null) return "Scanning";
        string trimmed = root.TrimEnd('\\', '/');
        if (System.IO.Path.GetPathRoot(root)?.TrimEnd('\\') == trimmed)
            return "Scanning " + (VolumeStats.Of(root)?.DriveLabel ?? "your PC");
        if (trimmed.Equals(UserFolders.Home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "Scanning your user folder";
        return "Scanning " + System.IO.Path.GetFileName(trimmed);
    }

    public static string Subtitle(string phase) => phase == "summarizing"
        ? "Sizing folders and preparing the first screen."
        : "Reading every folder. Boxes fill in as sizes are found.";

    /// <summary>Only a whole-drive scan knows its end: how much of the used space is found.</summary>
    public static double? Fraction(long found, string? root)
    {
        if (root is null || System.IO.Path.GetPathRoot(root)?.TrimEnd('\\') != root.TrimEnd('\\', '/')) return null;
        if (VolumeStats.Of(root) is not { UsedBytes: > 0 } volume) return null;
        return Math.Min(0.99, (double)found / volume.UsedBytes);
    }

    /// <summary>"212" and "GB".</summary>
    public static (string Number, string Unit) SplitBytes(long bytes)
    {
        string text = ShellFormat.HeroBytes(bytes);
        int space = text.LastIndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..]);
    }

    /// <summary>"C:\Users\me\AppData\Local\x" under "C:\Users\me" → "AppData".</summary>
    public static string? TopFolder(string path, string root)
    {
        string prefix = root.TrimEnd('\\', '/') + "\\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = path[prefix.Length..];
        int cut = rest.IndexOfAny(['\\', '/']);
        string first = cut < 0 ? rest : rest[..cut];
        return first.Length == 0 ? null : first;
    }

    private static List<(string Name, long Bytes)> Folders() =>
        (Model.ScanStats?.TopLevel ?? []).OrderByDescending(f => f.Bytes).ToList();

    private static string? ReadingName() =>
        Model.ScanStats is { } s && Model.RootPath is { } root ? TopFolder(s.CurrentFolder, root) : null;

    private FrameworkElement Stats()
    {
        var s = Model.ScanStats;
        var row = new Grid();
        void column(FrameworkElement element, bool star = false)
        {
            if (row.ColumnDefinitions.Count > 0)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
                var line = new Border { Background = Ui.Brush("RdLine") };
                Grid.SetColumn(line, row.ColumnDefinitions.Count - 1);
                row.Children.Add(line);
            }
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(element, row.ColumnDefinitions.Count - 1);
            row.Children.Add(element);
        }
        column(Stat("Files", (s?.Items ?? Model.ScanProgress).ToString("N0"), ""));
        var found = SplitBytes(s?.Bytes ?? 0);
        column(Stat("Found", found.Number, found.Unit));
        double elapsed = Model.ScanStartedAt is { } started ? (DateTimeOffset.Now - started).TotalSeconds : 0;
        column(Stat("Time", ((long)elapsed).ToString(), "s"));

        double? fraction = Fraction(s?.Bytes ?? 0, Model.RootPath);
        var progress = new StackPanel { Margin = new Thickness(28, 0, 28, 0), VerticalAlignment = VerticalAlignment.Center };
        progress.Children.Add(Rd.Figure((fraction is { } f ? $"About {(int)(f * 100)}% done" : "Reading folders").ToUpperInvariant(),
            10.5, FontWeights.Medium, "RdInk3"));
        var bar = new Grid { Height = 8, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(new Border { Background = Rd.FreeStripes(), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1) });
        if (fraction is { } done)
        {
            var fill = new Border { Background = Ui.Brush("RdInk"), HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(fill);
            bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * done;
        }
        else
        {
            bar.Children.Add(new Border { Background = Rd.HatchBrush(Rd.WithOpacity(Ui.Brush("RdInk"), 0.5), Ui.Brush("RdSoft"), 6, 2) });
        }
        bar.SizeChanged += (_, e) => bar.Clip = new RectangleGeometry(new Rect(e.NewSize), 2, 2);
        progress.Children.Add(bar);
        var current = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Brush("AppAccent"), Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(dot, Dock.Left);
        current.Children.Add(dot);
        var folder = Rd.Figure(s is { CurrentFolder.Length: > 0 } ? s.CurrentFolder : "Starting…", 11.5, null, "RdInk2");
        current.Children.Add(folder);
        progress.Children.Add(current);
        column(progress, star: true);

        var card = Rd.Surface(new Border { Padding = new Thickness(0, 22, 0, 22), Child = row }, "RdCard", 12);
        return card;
    }

    private FrameworkElement Stat(string label, string value, string unit)
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(28, 0, 28, 0),
            MaxWidth = widthClass == Rd.WidthClass.Compact ? 200 : 260,
            MinWidth = 90,
        };
        stack.Children.Add(Rd.Figure(label.ToUpperInvariant(), 10.5, FontWeights.Medium, "RdInk3"));
        double size = widthClass == Rd.WidthClass.Compact ? 30 : 40;
        var number = Rd.TightText(value, size, 0);
        var unitText = Rd.Text(unit, 13, null, "RdInk3");
        var line = Rd.BaselineRow(number, size, unitText, 13, 4);
        line.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(line);
        System.Windows.Automation.AutomationProperties.SetName(stack, $"{label} {value} {unit}".Trim());
        return stack;
    }

    private static Border CardHeader(string title, string? detail, string trailing)
    {
        var dock = new DockPanel { Margin = new Thickness(20, 0, 20, 0) };
        var right = Rd.Figure(trailing, 11, FontWeights.Medium, "RdInk3");
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Rd.CardTitle(title));
        if (detail is not null)
        {
            var d = Rd.Text(detail, 12, null, "RdInk3");
            d.Margin = new Thickness(10, 0, 0, 0);
            left.Children.Add(d);
        }
        dock.Children.Add(left);
        return new Border
        {
            Height = 50, Child = dock,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private FrameworkElement FoundSoFar()
    {
        var folders = Folders();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(CardHeader("Found so far", "The dashed box is still being read",
            $"{folders.Count} places so far".ToUpperInvariant()));
        var map = new LiveMap(folders, ReadingName()) { Margin = new Thickness(16) };
        Grid.SetRow(map, 1);
        grid.Children.Add(map);
        return Rd.Surface(grid, "RdCard", 12);
    }

    private FrameworkElement Places()
    {
        var folders = Folders();
        string? reading = ReadingName();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new DockPanel { Margin = new Thickness(20, 0, 20, 0) };
        var path = Rd.Figure(Model.RootPath ?? "", 11, null, "RdInk3");
        path.Margin = new Thickness(10, 0, 0, 0);
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(path, Dock.Right);
        head.Children.Add(Rd.CardTitle("Places"));
        head.Children.Add(path);
        grid.Children.Add(new Border
        {
            Height = 50, Child = head, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
        });

        var list = new StackPanel();
        foreach (var (name, bytes) in folders)
        {
            bool isReading = name == reading;
            var row = new DockPanel { Margin = new Thickness(20, 0, 20, 0) };
            FrameworkElement mark = isReading
                ? new Ellipse { Width = 7, Height = 7, Fill = Ui.Brush("AppAccent"), Margin = new Thickness(3.5, 0, 13.5, 0) }
                : Ui.Glyph("BsCheckLg", 10, Ui.Brush("AppAccent"));
            if (!isReading) { mark.Width = 14; mark.Margin = new Thickness(0, 0, 10, 0); }
            mark.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(mark, Dock.Left);
            row.Children.Add(mark);
            var size = Rd.Figure(ShellFormat.HeroBytes(bytes) + (isReading ? " so far" : ""), 12);
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(Rd.Text(name, 13, isReading ? FontWeights.Medium : FontWeights.Normal));
            if (isReading)
            {
                var tag = Rd.Figure("READING", 10, FontWeights.Medium, "RdInk3");
                tag.Margin = new Thickness(10, 0, 0, 0);
                label.Children.Add(tag);
            }
            row.Children.Add(label);
            list.Children.Add(new Border
            {
                Height = 46, Child = row,
                Background = isReading ? Ui.Brush("RdAccentWash") : Brushes.Transparent,
                BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
            });
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);

        var note = Rd.Text("Nothing is changed while scanning. We only read sizes.", 12, null, "RdInk2");
        var foot = new Border
        {
            Padding = new Thickness(20, 14, 20, 14), Child = note,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0),
        };
        Grid.SetRow(foot, 2);
        grid.Children.Add(foot);
        return Rd.Surface(grid, "RdCard", 12);
    }
}

/// <summary>The scan's biggest top folders as outlined boxes; the one being read is dashed (Mac: LiveMap).</summary>
internal sealed class LiveMap : Grid
{
    private readonly List<(string Name, long Bytes)> _folders;
    private readonly string? _reading;

    public LiveMap(List<(string Name, long Bytes)> folders, string? reading)
    {
        _folders = folders;
        _reading = reading;
        ClipToBounds = true;
        SizeChanged += (_, e) => Lay(e.NewSize);
    }

    private void Lay(Size size)
    {
        Children.Clear();
        if (_folders.Count == 0)
        {
            Children.Add(new Border { Background = Rd.HatchBrush(Ui.Brush("RdLine"), Ui.Brush("RdCard"), 10, 1) });
            var text = Rd.Text("Looking for the first folders…", 13, null, "RdInk3");
            text.HorizontalAlignment = HorizontalAlignment.Center;
            Children.Add(text);
            return;
        }
        var items = _folders.Select((f, i) => (i, Math.Max(1L, f.Bytes))).ToList();
        foreach (var rect in SquarifiedTreemap.Layout(items, new DmRect(0, 0, size.Width, size.Height)))
        {
            var (name, bytes) = _folders[rect.Id];
            bool reading = name == _reading;
            var r = rect.Rect;
            var box = new Grid
            {
                Width = Math.Max(0, r.Width - 4),
                Height = Math.Max(0, r.Height - 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(r.X + 2, r.Y + 2, 0, 0),
            };
            if (reading)
            {
                box.Children.Add(new Border { Background = Rd.HatchBrush(Rd.WithOpacity(Ui.Brush("AppAccent"), 0.35), Ui.Brush("RdAccentWash"), 8, 1.5) });
                box.Children.Add(new Rectangle
                {
                    Stroke = Ui.Brush("AppAccent"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
                });
            }
            else
            {
                box.Children.Add(new Border
                {
                    Background = Ui.Brush("RdCard"),
                    BorderBrush = Rd.WithOpacity(Ui.Brush("RdInk"), 0.85),
                    BorderThickness = new Thickness(1),
                });
            }
            if (r.Width > 60 && r.Height > 26)
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8), ClipToBounds = true };
                var title = Rd.Text(name, 12.5, FontWeights.SemiBold, reading ? "RdAccentDeep" : "RdInk");
                label.Children.Add(title);
                var size2 = Rd.Figure(reading ? "reading…" : ShellFormat.ShortBytes(bytes), 11, null, "RdInk3");
                size2.Margin = new Thickness(6, 0, 0, 0);
                label.Children.Add(size2);
                label.VerticalAlignment = VerticalAlignment.Top;
                label.HorizontalAlignment = HorizontalAlignment.Left;
                label.MaxWidth = Math.Max(0, r.Width - 4);
                box.Children.Add(label);
            }
            System.Windows.Automation.AutomationProperties.SetName(box,
                $"{name}, {ByteFormat.Format(bytes)}{(reading ? ", being read" : "")}");
            Children.Add(box);
        }
    }
}
