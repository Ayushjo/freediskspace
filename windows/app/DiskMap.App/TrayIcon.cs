using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DiskMap.App;

/// <summary>
/// WIN-057: the tray presence — the Windows counterpart of the macOS
/// menu-bar extra. Right-click (or click) opens a flyout in the app's own
/// look rather than the stock Win32 menu: drive free space with its bar,
/// the last scan and what changed since, what's waiting in Cleanup, and
/// the app-level commands (they used to live in the window's ☰ menu).
/// Strictly passive — it never scans or cleans on its own.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly MainWindow _window;
    private Window? _flyout;
    private DateTime _closedAt;

    /// <summary>Settings › General › Show free space in the notification area.</summary>
    public bool Visible { get => _icon.Visible; set => _icon.Visible = value; }

    public TrayIcon(MainWindow window)
    {
        _window = window;
        // The icon comes out of the exe itself (ApplicationIcon embeds
        // app.ico into the PE resources) — no loose file in the output
        // dir, no CWD-dependent path to get wrong.
        var exeIcon = System.Drawing.Icon.ExtractAssociatedIcon(
            Environment.ProcessPath ?? "Freedisk.exe") ?? System.Drawing.SystemIcons.Application;
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = exeIcon,
            Text = "freedisk.space",
            Visible = AppSettings.Load().ShowTray,
        };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is System.Windows.Forms.MouseButtons.Right or System.Windows.Forms.MouseButtons.Left)
                ToggleFlyout();
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        ScanModel.Shared.StateChanged += (_, _) => UpdateTooltip();
        UpdateTooltip();
    }

    /// <summary>Hover text: free space at a glance, warning under 10%.</summary>
    private void UpdateTooltip()
    {
        if (ScanModel.Shared.Volume is not { } v) return;
        double freeFrac = v.TotalBytes > 0 ? (double)v.FreeBytes / v.TotalBytes : 0;
        string text = $"freedisk.space — {ByteFormat.Format(v.FreeBytes)} free{(freeFrac < 0.10 ? " ⚠" : "")}";
        _icon.Text = text.Length > 63 ? text[..63] : text;   // NotifyIcon's limit
    }

    /// <summary>
    /// Closes the flyout once. Closing deactivates it, and Deactivated closes it again — a second
    /// Close while the first is under way throws ("…while a Window is closing").
    /// </summary>
    private void CloseFlyout()
    {
        if (_flyout is not { } flyout) return;
        _flyout = null;
        _closedAt = DateTime.Now;
        flyout.Close();
    }

    private void ShowWindow()
    {
        CloseFlyout();
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ToggleFlyout()
    {
        // The click that dismissed the flyout (focus left it) also lands
        // here — don't reopen it in the same breath.
        if (_flyout is not null || (DateTime.Now - _closedAt).TotalMilliseconds < 250)
        {
            CloseFlyout();
            return;
        }
        var opened = BuildFlyout();
        _flyout = opened;
        bool Current() => ReferenceEquals(_flyout, opened);
        opened.Closed += (_, _) => { if (Current()) { _flyout = null; _closedAt = DateTime.Now; } };
        opened.Deactivated += (_, _) => { if (Current()) CloseFlyout(); };
        opened.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && Current()) CloseFlyout(); };
        opened.Opacity = 0;
        opened.Show();
        // Position after layout: bottom-right of the work area, above the
        // taskbar (or wherever the work area ends), like system flyouts.
        var area = SystemParameters.WorkArea;
        opened.Left = area.Right - opened.ActualWidth - 4;
        opened.Top = area.Bottom - opened.ActualHeight - 4;
        opened.Opacity = 1;
        if (Current()) opened.Activate();
    }

    /// <summary>Changes under 50 MB are noise (caches, logs), not news.</summary>
    private const long Noise = 50_000_000;

    /// <summary>
    /// The status panel (Mac: MenuBarStatusView): the drive, its free space and used bar, what
    /// you can free, what changed, the last scan, then Rescan and Open freedisk.space. The
    /// Windows-only commands (no menu bar here) sit in a quiet row underneath.
    /// </summary>
    private Window BuildFlyout()
    {
        var model = ScanModel.Shared;
        var body = new StackPanel();
        void add(FrameworkElement e, double top = 14) { if (body.Children.Count > 0) e.Margin = new Thickness(0, top, 0, 0); body.Children.Add(e); }

        var volume = model.Volume ?? DiskMap.Core.VolumeStats.Of(System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\");
        if (volume is { } v)
        {
            bool low = v.TotalBytes > 0 && (double)v.FreeBytes / v.TotalBytes < 0.10;
            var drive = new StackPanel();
            var head = new DockPanel();
            if (AppSettings.Load().ShowDusty)
            {
                var dusty = Ui.BrandIcon(26);
                dusty.ToolTip = low ? "Dusty’s gathering dust: space is low" : "Plenty of room";
                DockPanel.SetDock(dusty, Dock.Right);
                head.Children.Add(dusty);
            }
            var name = Rd.Text(v.DriveLabel, 12, null, "RdInk2");
            DockPanel.SetDock(name, Dock.Left);
            head.Children.Add(name);
            if (low)
            {
                var tag = Rd.Tag("Low on space", Rd.TagTone.Ink);
                tag.Margin = new Thickness(6, 0, 0, 0);
                tag.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(tag, Dock.Left);
                head.Children.Add(tag);
            }
            head.Children.Add(new Border());
            drive.Children.Add(head);
            var figure = Rd.TightText(DiskMap.Core.ShellFormat.HeroBytes(v.FreeBytes), 34, 0);
            var of = Rd.Text($"free of {DiskMap.Core.ShellFormat.HeroBytes(v.TotalBytes)}", 12, null, "RdInk3");
            var line = Rd.BaselineRow(figure, 34, of, 12, 6);
            line.Margin = new Thickness(0, 8, 0, 0);
            drive.Children.Add(line);
            double used = v.TotalBytes > 0 ? Math.Clamp((double)v.UsedBytes / v.TotalBytes, 0, 1) : 0;
            var bar = new Grid { Height = 8, Margin = new Thickness(0, 8, 0, 0) };
            bar.Children.Add(new Border { Background = Rd.FreeStripes(), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1) });
            var fill = new Border { Background = Ui.Brush("RdInk"), HorizontalAlignment = HorizontalAlignment.Left };
            bar.Children.Add(fill);
            bar.SizeChanged += (_, e) =>
            {
                fill.Width = e.NewSize.Width * used;
                bar.Clip = new System.Windows.Media.RectangleGeometry(new Rect(e.NewSize), 2, 2);
            };
            System.Windows.Automation.AutomationProperties.SetName(bar, $"{Math.Round(used * 100)} percent used");
            drive.Children.Add(bar);
            add(drive);
        }

        long safe = model.SafeReclaimableBytes;
        if (safe > 0)
        {
            var card = new DockPanel { Margin = new Thickness(12) };
            var go = Rd.Text("Clean up →", 12, null, "RdAccentDeep");
            go.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(go, Dock.Right);
            card.Children.Add(go);
            var text = new StackPanel();
            text.Children.Add(Rd.Text("You can free", 12, null, "RdInk2"));
            var amount = Rd.Figure(DiskMap.Core.ShellFormat.HeroBytes(safe), 18, FontWeights.SemiBold, "AppAccent");
            amount.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(amount);
            card.Children.Add(text);
            var box = new Border
            {
                CornerRadius = new CornerRadius(10), Background = Ui.Brush("RdAccentWash"),
                BorderBrush = Rd.WithOpacity(Ui.Brush("AppAccent"), 0.35), BorderThickness = new Thickness(1),
                Child = card, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Opens Clean up. Nothing is removed from here.",
            };
            box.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShowWindow(); model.ShowPage("Safe to Review"); };
            add(box);
        }

        if (model.Tree is not null && model.RootPath is { } root)
        {
            var status = new StackPanel();
            string change = model.HistoryComparison is { } week && WeekLine(week) is { } weekLine
                ? weekLine
                : "Free space is about the same as at your last scan.";
            status.Children.Add(Rd.Text(change, 13, null, "RdInk", wrap: true));
            string when = model.LastScanFinishedAt is { } at ? DiskMap.Core.ShellFormat.RelativeTime(at, DateTimeOffset.Now) : "";
            var last = Rd.Figure($"Last scan {DiskMap.Core.CanonicalPath.DisplayPath(root)} · {when}", 11, null, "RdInk3");
            last.TextWrapping = TextWrapping.Wrap;
            last.MaxHeight = 30;
            last.Margin = new Thickness(0, 4, 0, 0);
            status.Children.Add(last);
            add(status);
        }
        else
        {
            add(Rd.Text("No scan yet. Open freedisk.space to see what fills your PC.", 13, null, "RdInk2", wrap: true));
        }

        add(new Border { Height = 1, Background = Ui.Brush("RdLine") });
        var buttons = new DockPanel();
        var rescan = Rd.SecondaryButton(model.IsScanning ? "Scanning…" : "Rescan", () => { ShowWindow(); _ = model.RescanAsync(); });
        rescan.IsEnabled = !model.IsScanning && model.RootPath is not null;
        rescan.Margin = new Thickness(0, 0, 8, 0);
        DockPanel.SetDock(rescan, Dock.Left);
        buttons.Children.Add(rescan);
        buttons.Children.Add(Rd.AccentButton("Open freedisk.space", ShowWindow, fullWidth: true));
        add(buttons);

        // Windows has no app menu bar: its commands live here, quietly.
        var more = new WrapPanel();
        void link(string label, Action run, bool enabled = true)
        {
            var t = Rd.Link(label, () => { CloseFlyout(); run(); }, 12, "RdInk3");
            t.IsEnabled = enabled;
            t.Opacity = enabled ? 1 : 0.45;
            t.Margin = new Thickness(0, 0, 14, 0);
            more.Children.Add(t);
        }
        bool hasScan = model.Tree is not null;
        link("Scan folder…", () => { ShowWindow(); _ = _window.ScanFolder(); }, !model.IsScanning);
        link("Export…", () => { ShowWindow(); _window.ExportScan(); }, hasScan);
        link("Settings", () => { ShowWindow(); _window.OpenSettings(); });
        link("About", () => { ShowWindow(); _window.OpenAbout(); });
        link("Quit", () => _window.Close());
        add(more, 12);

        var surface = new Border
        {
            Background = Ui.Brush("RdPage"), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Margin = new Thickness(16), Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.22, Direction = 270 },
        };
        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, SizeToContent = SizeToContent.Height,
            Width = 320 + 32, Content = surface, Topmost = true,
        };
        var menu = new ContextMenu();
        var hide = new MenuItem { Header = "Hide from the notification area" };
        hide.Click += (_, _) =>
        {
            var settings = AppSettings.Load();
            settings.ShowTray = false;
            settings.Save();
            CloseFlyout();
            Visible = false;
        };
        menu.Items.Add(hide);
        surface.ContextMenu = menu;
        return window;
    }

    /// <summary>"Free space −12 GB this week · AppData +8 GB" (Mac: MenuBarText.week).</summary>
    public static string? WeekLine(DiskMap.Core.StorageHistory.Comparison comparison)
    {
        var parts = new List<string>();
        string period = comparison.IsWeek ? "this week" : $"since {comparison.Since.LocalDateTime:d MMM yyyy}";
        if (Math.Abs(comparison.FreeDelta) >= Noise)
            parts.Add($"Free space {(comparison.FreeDelta < 0 ? "−" : "+")}{ByteFormat.Format(Math.Abs(comparison.FreeDelta))} {period}");
        if (comparison.Growers.Count > 0)
            parts.Add($"{comparison.Growers[0].Path} +{ByteFormat.Format(comparison.Growers[0].Delta)}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Harness: renders the flyout to a PNG without showing it at the tray.</summary>
    internal void SaveFlyoutShot(string path)
    {
        var flyout = BuildFlyout();
        MainWindow.SaveWindowShot(flyout, path);
        flyout.Close();
    }

    public void Dispose()
    {
        CloseFlyout();
        _icon.Dispose();
    }
}
