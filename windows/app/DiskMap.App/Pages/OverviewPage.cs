using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-105: Home — "why is my disk full?" in about five seconds: free space, what you can
/// free, where it all went (Mac: OverviewView.swift, redesign board 01). Before the first scan
/// it is the first-launch screen, and while that scan runs the live scanning board
/// (Mac: FirstScanHero.swift).
/// </summary>
public sealed class OverviewPage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };
    private bool _hideUnreadable;
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private double _builtWidth = -1;
    private DateTime _lastScanningBuild = DateTime.MinValue;
    private System.Windows.Threading.DispatcherTimer? _scanTick;

    public OverviewPage()
    {
        Background = Ui.Brush("RdPage");
        Content = _scroll;
        ModelEvents.WhileLoaded(this, Rebuild);
        SizeChanged += (_, _) =>
        {
            if (Math.Abs(PageWidth - _builtWidth) >= 1) Rebuild();
        };
        Loaded += (_, _) => Model.PropertyChanged += OnModelProperty;
        Unloaded += (_, _) =>
        {
            Model.PropertyChanged -= OnModelProperty;
            _scanTick?.Stop();
        };
    }

    /// <summary>The scanning board follows the live counters (a few times a second, not per report).</summary>
    private void OnModelProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScanModel.IsScanning) or nameof(ScanModel.ScanPhase))
        {
            Dispatcher.InvokeAsync(Rebuild);
            return;
        }
        if (e.PropertyName == nameof(ScanModel.ScanStats) && Model.Tree is null
            && (DateTime.UtcNow - _lastScanningBuild).TotalMilliseconds > 250)
        {
            _lastScanningBuild = DateTime.UtcNow;
            Dispatcher.InvokeAsync(Rebuild);
        }
    }

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    private double PageWidth => ActualWidth > 0 ? ActualWidth : WindowWidth - 221;

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        _builtWidth = PageWidth;
        if (Model.Tree is not { } tree || Model.Totals.Length != tree.Count)
        {
            if (Model.IsScanning)
            {
                _scroll.Content = new ScanningBoard(_class, _scroll.ActualHeight > 0 ? _scroll.ActualHeight : ActualHeight).Build();
                // The clock keeps moving between progress reports.
                _scanTick ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _scanTick.Tick -= OnScanTick;
                _scanTick.Tick += OnScanTick;
                _scanTick.Start();
            }
            else
            {
                _scanTick?.Stop();
                _scroll.Content = new FirstLaunch(_class).Build();
            }
            return;
        }
        _scanTick?.Stop();
        double offset = _scroll.VerticalOffset;
        _scroll.Content = BuildLoaded(tree);
        _scroll.ScrollToVerticalOffset(offset);
    }

    /// <summary>For the first-launch options: re-render after a pick.</summary>
    internal void RebuildNow() => Rebuild();

    private void OnScanTick(object? sender, EventArgs e)
    {
        if (Model.IsScanning && Model.Tree is null) Rebuild();
        else _scanTick?.Stop();
    }

    // ---- Loaded ----

    private double Gap => _class.Gap();
    private double HeroSize => _class switch { Rd.WidthClass.Compact => 88, Rd.WidthClass.Regular => 116, _ => 146 };
    private double TopCardMinHeight => _class switch { Rd.WidthClass.Compact => 206, Rd.WidthClass.Regular => 250, _ => 300 };

    private FrameworkElement BuildLoaded(FileTree tree)
    {
        double pad = _class.PagePadding();
        var column = new StackPanel { Margin = new Thickness(pad) };
        void add(FrameworkElement element)
        {
            if (column.Children.Count > 0) element.Margin = new Thickness(0, Gap, 0, 0);
            column.Children.Add(element);
        }

        if (UnreadableNotice() is { } unreadable) add(unreadable);
        double inner = Math.Max(0, PageWidth - pad * 2 - Gap);
        add(Pair(FreeCard(), CanFreeCard(), Math.Max(300, inner * 0.42)));
        add(UsageCard());
        add(Pair(BiggestPlaces(tree), ReadyToFree(), Math.Max(300, inner * 0.44)));
        if (Footer() is { } footer) { add(footer); footer.Margin = new Thickness(0, Gap + 2, 0, 0); }
        return column;
    }

    /// <summary>Two cards side by side, the same height, the right one at a fixed width.</summary>
    private Grid Pair(FrameworkElement left, FrameworkElement right, double rightWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Gap) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightWidth) });
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    // ---- Free space ----

    private Border FreeCard()
    {
        var snap = Model.Snapshot;
        var body = new StackPanel();
        double hero = HeroSize;
        if ((Model.Volume ?? snap?.Volume) is { } vol)
        {
            body.Children.Add(Rd.Text("Free space", 13, null, "RdInk2"));
            var figure = Rd.TightText(ShellFormat.HeroBytes(vol.FreeBytes), hero, -hero * 0.05);
            var of = Rd.Text($"of {ShellFormat.HeroBytes(vol.TotalBytes)}", hero * 0.27, FontWeights.Medium, "RdInk3");
            var line = Rd.BaselineRow(figure, hero, of, hero * 0.27, hero * 0.1);
            body.Children.Add(Shrinkable(line, 0.5, new Thickness(0, 6, 0, 0)));
            var pace = Rd.Text(PaceLine(vol), 13, null, "RdInk2", wrap: true);
            pace.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(pace);
        }
        else
        {
            string name = System.IO.Path.GetFileName(Model.RootPath?.TrimEnd('\\', '/') ?? "") is { Length: > 0 } n ? n : "This scan";
            body.Children.Add(Rd.Text(name, 13, null, "RdInk2"));
            var figure = Rd.TightText(ByteFormat.Format(snap?.ScannedBytes ?? Model.Totals[0]), hero, -hero * 0.05);
            body.Children.Add(Shrinkable(figure, 0.5, new Thickness(0, 6, 0, 0)));
        }
        var coverage = CoverageLine();
        coverage.Margin = new Thickness(0, 6, 0, 0);
        body.Children.Add(coverage);

        bool compact = _class == Rd.WidthClass.Compact;
        body.Margin = compact ? new Thickness(24, 22, 24, 22) : new Thickness(36, 32, 36, 32);
        body.VerticalAlignment = VerticalAlignment.Center;
        var layers = new Grid { MinHeight = TopCardMinHeight };
        layers.Children.Add(new Border { Background = Rd.Grid24(), Opacity = 0.7 });
        layers.Children.Add(body);
        var card = Rd.Surface(layers, "RdCard", 14);
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, "overview-free");
        return card;
    }

    /// <summary>minimumScaleFactor: shrink to fit (down to <paramref name="minimum"/>), never grow.</summary>
    private static FrameworkElement Shrinkable(FrameworkElement content, double minimum, Thickness margin)
    {
        var box = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content,
            Margin = margin,
        };
        content.SizeChanged += (_, _) =>
        {
            // Below the minimum scale the figure clips instead of shrinking further.
            if (box.ActualWidth > 0 && content.ActualWidth * minimum > box.ActualWidth)
                box.Stretch = Stretch.None;
        };
        return box;
    }

    /// <summary>"24% of your disk is free." and the week's pace, or the health title.</summary>
    private string PaceLine(VolumeInfo vol)
    {
        long freePct = (long)Math.Round((double)vol.FreeBytes / Math.Max(1, vol.TotalBytes) * 100, MidpointRounding.AwayFromZero);
        string line = $"{freePct}% of your disk is free.";
        if (Model.HistoryComparison is { IsWeek: true } c && c.FreeDelta < -GrowthNoise)
        {
            double weeklyLoss = -c.FreeDelta;
            double lowMark = vol.TotalBytes * 0.1;
            double weeks = (vol.FreeBytes - lowMark) / weeklyLoss;
            if (weeks <= 0) line += " You're already low on space.";
            else if (weeks < 52)
            {
                long n = Math.Max(1, (long)Math.Round(weeks, MidpointRounding.AwayFromZero));
                line += $" At this week's pace you'll be low in about {n} week{(n == 1 ? "" : "s")}.";
            }
        }
        else if (Model.Snapshot?.Health is { } health && health != StorageHealth.Healthy)
        {
            line += " " + HealthTitle(health) + ".";
        }
        return line;
    }

    public const long GrowthNoise = 50_000_000;

    private static string HealthTitle(StorageHealth health) => health switch
    {
        StorageHealth.Tight => "Getting full",
        StorageHealth.Low => "Low on space",
        StorageHealth.Critical => "Critically full",
        _ => "Healthy",
    };

    /// <summary>One line on what the scan covers, with the causes behind "Why?".</summary>
    private FrameworkElement CoverageLine()
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var text = Rd.Text(CoverageText(), 12, null, "RdInk3");
        text.TextWrapping = TextWrapping.Wrap;
        text.MaxWidth = 900;
        System.Windows.Automation.AutomationProperties.SetAutomationId(text, "volume-reconciliation");
        row.Children.Add(text);
        if (ShowsAccount && Model.SpaceAccount is { } account)
        {
            // The full account replaces the one-line "Why?" once it was read; its parts always
            // add up to what the drive has in use.
            var rest = Rd.Link("Where's the rest?", () => Dialogs.ShowSpaceAccount(Window.GetWindow(this), account, Model.SystemStorage), 12);
            rest.Margin = new Thickness(6, 0, 0, 0);
            rest.ToolTip = "Explains the gap between this scan and the drive";
            System.Windows.Automation.AutomationProperties.SetAutomationId(rest, "space-account-link");
            row.Children.Add(rest);
        }
        else if (WhyDetail() is { Count: > 0 } detail)
        {
            System.Windows.Controls.Primitives.Popup? popup = null;
            // The link handles its own click, so the toggle must be its action (a second
            // MouseLeftButtonUp handler never runs once the link marks the event handled).
            var why = Rd.Link("Why?", () => { if (popup is not null) popup.IsOpen = !popup.IsOpen; }, 12);
            why.Margin = new Thickness(6, 0, 0, 0);
            popup = WhyPopover(why, detail);
            row.Children.Add(why);
            row.Children.Add(popup);
        }
        return row;
    }

    private bool ShowsAccount => Model.SpaceAccount is { } a && a.Rows.Count > 1 && a.Total == a.UsedBytes;

    private string CoverageText()
    {
        var snap = Model.Snapshot;
        if (snap?.Reconciliation is { } rec)
        {
            string scanned = ByteFormat.Format(rec.ScannedBytes), used = ByteFormat.Format(rec.UsedBytes);
            if (rec.ScannedExceedsUsed) return $"This scan found {scanned}, more than the {used} in use.";
            if (rec.CoverageFraction >= 0.98) return $"This scan accounts for all {used} in use.";
            return $"This scan accounts for {scanned} of the {used} in use.";
        }
        return $"{ByteFormat.Format(snap?.ScannedBytes ?? 0)} found in this scan.";
    }

    /// <summary>The "Why the numbers differ" paragraphs (Mac: reconciliationDetail + cloneNote, Windows causes).</summary>
    private List<string> WhyDetail()
    {
        var lines = new List<string>();
        var snap = Model.Snapshot;
        if (snap?.Reconciliation is { } rec)
        {
            if (rec.ScannedExceedsUsed)
            {
                lines.Add("Hard-linked and block-cloned files share the same blocks on disk but are listed once per name, so the scan total can exceed what is actually used.");
            }
            else if (rec.CoverageFraction < 0.98)
            {
                var causes = new List<string>();
                string root = Model.RootPath ?? "";
                if (System.IO.Path.GetPathRoot(root)?.TrimEnd('\\') != root.TrimEnd('\\'))
                {
                    string place = UserFolders.Home.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                        ? "your user folder"
                        : $"“{System.IO.Path.GetFileName(root.TrimEnd('\\'))}”";
                    causes.Add($"outside {place} (Windows, apps and other users)");
                }
                causes.Add("used by Windows itself (the page file, hibernation, restore points and System Volume Information) or by file system metadata");
                int denied = Model.DeniedDirectories.Count;
                if (denied > 0) causes.Add($"inside {denied:N0} folder{(denied == 1 ? "" : "s")} freedisk.space couldn’t read");
                string list = causes.Count > 1
                    ? string.Join(", ", causes.Take(causes.Count - 1)) + ", or " + causes[^1]
                    : causes[0];
                lines.Add($"The other {ByteFormat.Format(rec.UnaccountedBytes)} is {list}.");
            }
        }
        if (snap?.HardLinkCorrection is { IsEmpty: false } c)
            lines.Add($"{c.DuplicateNameCount:N0} names of hard-linked files are counted once — {ByteFormat.Format(c.AllocatedBytes)} of shared blocks.");
        if (Model.Volume is { DriveFormat: "ReFS" })
        {
            var clone = Model.CloneCorrection;
            lines.Add(clone.CloneCount > 0
                ? $"{clone.CloneCount:N0} cloned cop{(clone.CloneCount == 1 ? "y" : "ies")} share {ByteFormat.Format(clone.Bytes)} with their originals — counted once."
                : "Block-cloned files are counted once per copy, so these totals can be higher than the disk really uses. Turn on clone accounting in Settings (Ctrl+,) to count clones once.");
        }
        return lines;
    }

    private static System.Windows.Controls.Primitives.Popup WhyPopover(FrameworkElement anchor, List<string> lines)
    {
        var body = new StackPanel { Width = 340 };
        body.Children.Add(Rd.MonoLabel("Why the numbers differ"));
        bool first = true;
        foreach (string line in lines)
        {
            var text = Rd.Text(line, first ? 13 : 12, null, first ? "RdInk" : "RdInk2", wrap: true);
            text.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(text);
            first = false;
        }
        return new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = Ui.Brush("RdCard"),
                BorderBrush = Ui.Brush("RdLine"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 6, 8, 8),
                Child = body,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.12 },
            },
        };
    }

    // ---- You can free ----

    private Border CanFreeCard()
    {
        bool ready = Model.Reviewables is not null;
        long bytes = Model.SafeReclaimableBytes;
        double hero = HeroSize;
        var white = (double o) => (Brush)new SolidColorBrush(Color.FromArgb((byte)Math.Round(o * 255), 255, 255, 255));

        var body = new DockPanel { LastChildFill = true };
        var button = Rd.AccentButton(bytes > 0 ? $"Review {ShellFormat.HeroBytes(bytes)}" : "Open Clean up",
            () => Model.ShowPage("Safe to Review"), large: true, shadow: true);
        button.Opacity = ready ? 1 : 0;
        button.IsHitTestVisible = ready;
        button.Margin = new Thickness(0, 16, 0, 0);
        DockPanel.SetDock(button, Dock.Bottom);
        button.VerticalAlignment = VerticalAlignment.Bottom;

        var top = new StackPanel();
        var label = Rd.Text("You can free", 13);
        label.Foreground = white(0.62);
        top.Children.Add(label);
        string figureText = ready ? (bytes > 0 ? ShellFormat.HeroBytes(bytes) : "Nothing yet") : "Checking…";
        double figureSize = bytes > 0 || !ready ? hero * 0.43 : hero * 0.3;
        var figure = Rd.TightText(figureText, figureSize, ready ? -hero * 0.02 : 0, brush:
            ready && bytes > 0 ? Ui.Brush("RdAccentBright") : white(0.7));
        System.Windows.Automation.AutomationProperties.SetAutomationId(figure, "overview-can-free");
        top.Children.Add(Shrinkable(figure, 0.6, new Thickness(0, 8, 0, 0)));
        var detail = Rd.Text(bytes > 0 || !ready
            ? "Caches and build files that rebuild by themselves. They go to the Recycle Bin first, so you can always put them back."
            : Model.NothingTickedLine, 13, null, "RdInk", wrap: true);
        detail.Foreground = white(0.82);
        detail.LineHeight = Math.Ceiling(Ui.Scaled(13) * Rd.TextLineHeight) + 2;   // .lineSpacing(2)
        detail.Margin = new Thickness(0, 12, 0, 0);
        top.Children.Add(detail);

        body.Children.Add(button);
        body.Children.Add(top);
        bool compact = _class == Rd.WidthClass.Compact;
        body.Margin = compact ? new Thickness(24, 22, 24, 22) : new Thickness(32, 30, 32, 30);
        var layers = new Grid { MinHeight = TopCardMinHeight };
        layers.Children.Add(new Border { Background = Rd.Grid24(white(0.05)) });
        layers.Children.Add(body);
        return Rd.Surface(layers, "RdNight", 14, stroke: null);
    }

    // ---- What's using your disk ----

    private sealed record UsagePart(string Id, string Title, long Bytes, string Fill, int Grey, StorageCategory? Category);

    private List<UsagePart> UsageParts()
    {
        var snap = Model.Snapshot;
        var parts = new List<UsagePart>();
        if (snap is null) return parts;
        var cats = snap.Categories.Where(c => c.Bytes > 0).ToList();
        int i = 0;
        foreach (var c in cats.Take(6)) parts.Add(new UsagePart(c.Key, c.Title, c.Bytes, "grey", i++, c));
        long rest = cats.Skip(6).Sum(c => c.Bytes);
        if (rest > 0) parts.Add(new UsagePart("rest", "Everything else", rest, "grey", 6, null));
        if ((Model.Volume ?? snap.Volume) is { } vol)
        {
            long unscanned = vol.UsedBytes - snap.ScannedBytes;
            if (unscanned > vol.TotalBytes / 100)
            {
                string root = snap.ScanRootPath.TrimEnd('\\');
                bool wholeDrive = System.IO.Path.GetPathRoot(snap.ScanRootPath)?.TrimEnd('\\') == root;
                parts.Add(new UsagePart("unscanned", wholeDrive ? "System and other" : "Outside this scan", unscanned, "unscanned", 0, null));
            }
            parts.Add(new UsagePart("free", "Free", vol.FreeBytes, "free", 0, null));
        }
        return parts;
    }

    private static readonly double[] Greys = [1.0, 0.74, 0.56, 0.42, 0.30, 0.21, 0.14];

    private static FrameworkElement Swatch(UsagePart part, double radius = 0)
    {
        if (part.Fill == "free")
            return new Border
            {
                Background = Rd.FreeStripes(), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(radius),
            };
        double opacity = part.Fill == "unscanned" ? 0.08 : Greys[Math.Min(part.Grey, Greys.Length - 1)];
        return new Border { Background = Rd.WithOpacity(Ui.Brush("RdInk"), opacity), CornerRadius = new CornerRadius(radius) };
    }

    private Border UsageCard()
    {
        var snap = Model.Snapshot;
        var parts = UsageParts();
        long total = Math.Max(1, parts.Sum(p => p.Bytes));
        long used = (Model.Volume ?? snap?.Volume)?.UsedBytes ?? snap?.ScannedBytes ?? 0;
        var stack = new StackPanel();

        var head = new DockPanel();
        var open = Rd.Link("Open Explore →", () => Model.Visualize(0));
        DockPanel.SetDock(open, Dock.Right);
        head.Children.Add(open);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var title = Rd.Text(snap?.Mode == CategoryMode.Folder ? "What’s in this folder" : "What’s using your disk", 16, FontWeights.SemiBold);
        titleRow.Children.Add(title);
        var usedText = Rd.Text($"{ByteFormat.Format(used)} used", 12, null, "RdInk3");
        titleRow.Children.Add(Rd.BaselineRow(new Border(), 16, usedText, 12, 10));
        head.Children.Add(titleRow);
        stack.Children.Add(head);

        // The bar: each part a share of the width, 2 pt apart, clipped to radius 8.
        double barHeight = _class switch { Rd.WidthClass.Compact => 46, Rd.WidthClass.Regular => 58, _ => 70 };
        var bar = new Grid { Height = barHeight, Margin = new Thickness(0, 14, 0, 0), ClipToBounds = true };
        bar.SizeChanged += (_, e) =>
        {
            bar.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 8, 8);
            LayoutBar(bar, parts, total, e.NewSize.Width);
        };
        // The legend below carries the same parts for screen readers.
        bar.Focusable = false;
        stack.Children.Add(bar);

        // The legend: 4 columns (3 compact), 28 apart (22), rows 10 apart.
        int columns = _class == Rd.WidthClass.Compact ? 3 : 4;
        double colGap = _class == Rd.WidthClass.Compact ? 22 : 28;
        var legend = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        for (int c = 0; c < columns; c++)
        {
            if (c > 0) legend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(colGap) });
            legend.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (int i = 0; i < parts.Count; i++)
        {
            if (i % columns == 0)
            {
                if (i > 0) legend.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
                legend.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var item = LegendItem(parts[i]);
            Grid.SetColumn(item, (i % columns) * 2);
            Grid.SetRow(item, (i / columns) * 2);
            legend.Children.Add(item);
        }
        stack.Children.Add(legend);

        bool compact = _class == Rd.WidthClass.Compact;
        stack.Margin = compact ? new Thickness(20, 16, 20, 16) : new Thickness(28, 24, 28, 24);
        return Rd.Surface(stack, "RdCard", 12);
    }

    private void LayoutBar(Grid bar, List<UsagePart> parts, long total, double width)
    {
        bar.Children.Clear();
        double x = 0;
        foreach (var part in parts)
        {
            double w = width * part.Bytes / total;
            if (w >= 1)
            {
                var swatch = Swatch(part);
                swatch.HorizontalAlignment = HorizontalAlignment.Left;
                swatch.Width = Math.Max(1, w - 2);
                swatch.Margin = new Thickness(x, 0, 0, 0);
                swatch.ToolTip = $"{part.Title} · {ByteFormat.Format(part.Bytes)}";
                if (part.Category is { } c)
                {
                    swatch.Cursor = Cursors.Hand;
                    swatch.MouseLeftButtonUp += (_, _) => Open(c);
                }
                bar.Children.Add(swatch);
            }
            x += w;
        }
    }

    private FrameworkElement LegendItem(UsagePart part)
    {
        var row = new DockPanel { Background = Brushes.Transparent };
        var swatch = Swatch(part, 3);
        swatch.Width = 12;
        swatch.Height = 12;
        swatch.Margin = new Thickness(0, 0, 9, 0);
        DockPanel.SetDock(swatch, Dock.Left);
        row.Children.Add(swatch);
        var size = Rd.Figure(ByteFormat.Format(part.Bytes), 12);
        size.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        var title = Rd.Text(part.Title, 13);
        row.Children.Add(title);
        System.Windows.Automation.AutomationProperties.SetName(row, $"{part.Title}, {ByteFormat.Format(part.Bytes)}");
        if (part.Category is { } c)
        {
            row.Cursor = Cursors.Hand;
            row.ToolTip = OpenHelp(c);
            row.MouseEnter += (_, _) => title.Foreground = Ui.Brush("AppAccent");
            row.MouseLeave += (_, _) => title.Foreground = Ui.Brush("RdInk");
            row.MouseLeftButtonUp += (_, _) => Open(c);
        }
        else
        {
            row.ToolTip = part.Title;
        }
        return row;
    }

    /// <summary>Categories that are mostly loose files open Biggest Files filtered to them; the rest open Explore at their biggest folder.</summary>
    public static readonly IReadOnlySet<string> FileListCategories =
        new HashSet<string> { "installers", "ai", "media", "vms", "downloads", "documents" };

    private static void Open(StorageCategory cat)
    {
        if (cat.FileKind is { } kind)
        {
            Model.SearchQuery = $"kind:{kind}";
            Model.ShowPage("Find");
        }
        else if (FileListCategories.Contains(cat.Key))
        {
            FileListPage.PendingClassFilter = cat.Key;
            Model.ShowPage("Biggest Files");
        }
        else if (cat.NodeId is { } node)
        {
            Model.Select(node);
            Model.Visualize(node);
        }
        else
        {
            Model.ShowPage("Biggest Files");
        }
    }

    private static string OpenHelp(StorageCategory cat)
    {
        if (cat.FileKind is not null) return $"List every {cat.Title.ToLowerInvariant()} file in Find";
        if (FileListCategories.Contains(cat.Key)) return $"Show the biggest files in {cat.Title}";
        return cat.NodeId is not null ? "Show this folder in Explore" : "Show the biggest files";
    }

    // ---- Biggest places ----

    private Border BiggestPlaces(FileTree tree)
    {
        var totals = Model.Totals;
        var kids = tree.ChildrenOf(0, totals).OrderByDescending(k => k.Size).ToList();
        var top = kids.Take(6).Where(k => k.Size > 0).ToList();
        long restBytes = kids.Skip(6).Sum(k => k.Size);
        int restCount = Math.Max(0, kids.Count - 6);
        long largest = Math.Max(1, top.FirstOrDefault().Size);
        var files = Model.Counts.Files;

        var stack = new StackPanel();
        var open = Rd.Text("Open Explore →", 12, null, "RdInk2");
        open.Cursor = Cursors.Hand;
        open.MouseLeftButtonUp += (_, _) => Model.Visualize(0);
        stack.Children.Add(Rd.CardHeader("Biggest places", open));
        foreach (var (id, size) in top)
        {
            int count = id < files.Length ? files[id] : 0;
            int captured = id;
            stack.Children.Add(PlaceRow(tree.NameOf(id), count > 0 ? $"{count:N0} file{(count == 1 ? "" : "s")}" : null,
                size, (double)size / largest, () => { Model.Select(captured); Model.Visualize(captured); }));
        }
        if (restCount > 0)
            stack.Children.Add(PlaceRow("Everything else", $"{restCount} folder{(restCount == 1 ? "" : "s")}",
                restBytes, (double)restBytes / largest, () => Model.Visualize(0)));
        var card = Rd.Surface(stack, "RdCard", 12);
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, "biggest-places");
        return card;
    }

    private FrameworkElement PlaceRow(string name, string? detail, long bytes, double fraction, Action action)
    {
        double height = _class switch { Rd.WidthClass.Compact => 42, Rd.WidthClass.Regular => 48, _ => 52 };
        var fill = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = Ui.Brush("RdSoft"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(8, 6, 0, 6),
        };
        var text = new DockPanel { Margin = new Thickness(16, 0, 16, 0) };
        var size = Rd.Figure(ByteFormat.Format(bytes), 12);
        size.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(size, Dock.Right);
        text.Children.Add(size);
        var names = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(Rd.Text(name, 13, FontWeights.Medium));
        if (detail is not null)
        {
            var d = Rd.Text(detail, 12, null, "RdInk3");
            d.Margin = new Thickness(9, 0, 0, 0);
            names.Children.Add(d);
        }
        text.Children.Add(names);
        var layers = new Grid { Height = height, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        layers.Children.Add(fill);
        layers.Children.Add(text);
        layers.SizeChanged += (_, e) =>
            fill.Width = Math.Max(8, (e.NewSize.Width - 16) * 0.86 * Math.Min(1, fraction));
        layers.MouseEnter += (_, _) => fill.Background = Ui.Brush("RdAccentWash");
        layers.MouseLeave += (_, _) => fill.Background = Ui.Brush("RdSoft");
        layers.MouseLeftButtonUp += (_, _) => action();
        layers.ToolTip = $"Open {name} in Explore";
        System.Windows.Automation.AutomationProperties.SetName(layers, $"{name}, {ByteFormat.Format(bytes)}");
        return new Border
        {
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.55),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = layers,
        };
    }

    // ---- Ready to free ----

    private Border ReadyToFree()
    {
        bool ready = Model.Reviewables is not null;
        var items = Model.SafeReclaimables;
        long safe = Model.SafeReclaimableBytes;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });

        FrameworkElement? trailing = ready && safe > 0 ? Rd.Figure(ByteFormat.Format(safe), 12, null, "AppAccent") : null;
        grid.Children.Add(Rd.CardHeader("Ready to free", trailing));

        var list = new StackPanel();
        Grid.SetRow(list, 1);
        if (!ready)
        {
            var waiting = Rd.Text("Looking for things that rebuild by themselves…", 12, null, "RdInk2");
            waiting.HorizontalAlignment = HorizontalAlignment.Center;
            list.Children.Add(new Border { MinHeight = 120, Child = waiting });
        }
        else if (items.Count == 0)
        {
            var none = Rd.Text(Model.NothingTickedLine, 12, null, "RdInk2", wrap: true);
            none.TextAlignment = TextAlignment.Center;
            none.HorizontalAlignment = HorizontalAlignment.Center;
            list.Children.Add(new Border { MinHeight = 120, Padding = new Thickness(18, 0, 18, 0), Child = none });
        }
        else
        {
            foreach (var item in items.Take(_class == Rd.WidthClass.Compact ? 3 : 4))
                list.Children.Add(ReadyRow(item));
        }
        grid.Children.Add(list);

        var foot = new DockPanel { Margin = new Thickness(18, 0, 18, 0) };
        if (ready && items.Count > 0)
        {
            var all = Rd.Link($"See all {items.Count} →", () => Model.ShowPage("Safe to Review"), 12);
            DockPanel.SetDock(all, Dock.Right);
            foot.Children.Add(all);
        }
        foot.Children.Add(Rd.Text("Nothing is removed until you confirm.", 12, null, "RdInk2"));
        var footBorder = new Border
        {
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 1, 0, 0), Child = foot,
        };
        Grid.SetRow(footBorder, 2);
        grid.Children.Add(footBorder);
        var card = Rd.Surface(grid, "RdCard", 12);
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, "ready-to-free");
        return card;
    }

    private FrameworkElement ReadyRow(ReviewableTarget item)
    {
        double height = _class switch { Rd.WidthClass.Compact => 58, Rd.WidthClass.Regular => 70, _ => 78 };
        var row = new DockPanel { Margin = new Thickness(18, 0, 18, 0) };
        var tile = Rd.IconTile(Rd.IconForSymbol(item.Symbol));
        tile.Margin = new Thickness(0, 0, 14, 0);
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);
        var size = Rd.Figure(ByteFormat.Format(item.Bytes), 12);
        size.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Rd.Text(item.DisplayName, 13, FontWeights.Medium));
        if (_class != Rd.WidthClass.Compact)
        {
            var detail = Rd.Text(item.Consequence.Length == 0 ? item.Detail : item.Consequence, 12, null, "RdInk2");
            detail.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(detail);
        }
        row.Children.Add(text);
        var host = new Border
        {
            Height = height,
            Background = Brushes.Transparent,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.55),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
            Cursor = Cursors.Hand,
            ToolTip = "Review in Clean up",
        };
        host.MouseEnter += (_, _) => host.Background = Ui.Brush("RdHover");
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, _) => Model.ShowPage("Safe to Review");
        System.Windows.Automation.AutomationProperties.SetName(host,
            $"{item.DisplayName}, {ByteFormat.Format(item.Bytes)}. Review in Clean up");
        return host;
    }

    // ---- Notices and footer ----

    /// <summary>Say when the totals are short because folders were unreadable.</summary>
    private FrameworkElement? UnreadableNotice()
    {
        int count = Model.DeniedDirectories.Count;
        if (count == 0 || _hideUnreadable) return null;
        var banner = Rd.Banner($"{count:N0} folder{(count == 1 ? "" : "s")} couldn’t be read.",
            "Run freedisk.space as administrator to see everything, then rescan.",
            onDismiss: () => { _hideUnreadable = true; Rebuild(); });
        if (Model.Tree is { } tree && Model.RootPath is { } root)
            banner.ToolTip = string.Join("\n", Model.DeniedDirectories.Take(5).Select(id => tree.PathOf(id, root)));
        System.Windows.Automation.AutomationProperties.SetAutomationId(banner, "unreadable-folders-notice");
        return banner;
    }

    private FrameworkElement? Footer()
    {
        if (Model.Backend is not { Length: > 0 } backend || backend == "snapshot") return null;
        var row = new WrapPanel();
        bool quick = backend == "usn";
        row.Children.Add(Rd.Figure(ScanKindText(quick, Model.Elapsed, Model.ChangedRecords, Model.FallbackReason), 11, null, "RdInk3"));
        if (quick && Model.RootPath is { } rootPath)
        {
            var full = Rd.Link("Full Rescan", async () =>
            {
                if (Model.IsScanning) return;
                ScanCache.Remove(rootPath);
                await Model.RescanAsync();
            }, 11);
            full.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            full.Margin = new Thickness(6, 0, 0, 0);
            full.IsEnabled = !Model.IsScanning;
            row.Children.Add(full);
        }
        System.Windows.Automation.AutomationProperties.SetAutomationId(row, "scan-kind");
        return row;
    }

    public static string ScanKindText(bool quick, double seconds, int changed, string? reason)
    {
        string time = ShellFormat.Fixed(seconds, 1);
        if (quick)
        {
            return changed == 0
                ? $"Updated from your last scan in {time} s — nothing changed; a sample of folders was re-read to confirm."
                : $"Updated from your last scan in {time} s — {changed:N0} change{(changed == 1 ? "" : "s")} replayed from the change journal, unchanged folders spot-checked.";
        }
        string text = $"Full scan in {time} s.";
        return reason is { Length: > 0 } why ? text + $" (Walked in full: {why}.)" : text;
    }
}
