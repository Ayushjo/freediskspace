using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DiskMap.Core;

namespace DiskMap.App;

/// <summary>
/// WIN-115: Settings (Ctrl+,), the Mac's redesign board 14 (SettingsView). Only real choices:
/// every switch changes what the app does. A section list on the left, two columns of groups
/// on the right; changes save as you make them.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Action<string> _onAppearance;
    private readonly Action<double> _onTextScale;
    private readonly Action _onGeneralChanged;
    private readonly Grid _root = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly Dictionary<string, FrameworkElement> _groups = [];
    private string _section = "General";

    private static readonly (string Title, string Icon)[] Sections =
    [
        ("General", Icons.Settings), ("Scanning", Icons.Search), ("Clean up", Icons.Cleanup), ("Updates", "BsCheck2"),
        ("Appearance", Icons.DarkMode), ("Saved scans", "BsClockHistory"), ("Privacy", "BsLock"),
    ];

    public SettingsWindow(Action<string> onAppearance, Action<double> onTextScale, Action onGeneralChanged)
    {
        _onAppearance = onAppearance;
        _onTextScale = onTextScale;
        _onGeneralChanged = onGeneralChanged;
        Title = "freedisk.space Settings";
        Width = 1080;
        Height = 780;
        MinWidth = 1000;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Brush("RdPage");
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = _root;
        // Esc closes, as the Mac's Settings window does with ⌘W.
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
        Rebuild();
    }

    internal void Rebuild()
    {
        // The theme may have changed: brushes are swapped, not recoloured.
        Background = Ui.Brush("RdPage");
        double offset = _scroll.VerticalOffset;
        _root.Children.Clear();
        _root.Children.Add(Sidebar());
        var line = new Border { Background = Ui.Brush("RdLine") };
        Grid.SetColumn(line, 1);
        _root.Children.Add(line);
        _scroll.Content = Body();
        Grid.SetColumn(_scroll, 2);
        _root.Children.Add(_scroll);
        _scroll.ScrollToVerticalOffset(offset);
        // A rebuild drops the control that had the focus (the theme switch you just clicked);
        // with nothing focused no key arrives at all, so Esc did nothing. Keep it in the window.
        Dispatcher.InvokeAsync(() => { if (IsActive && !IsKeyboardFocusWithin) Focus(); },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---- Sidebar ----

    private FrameworkElement Sidebar()
    {
        var dock = new DockPanel { Margin = new Thickness(12) };
        var foot = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var v = Rd.Figure(version is null ? "dev" : $"{version.Major}.{version.Minor}.{version.Build}", 11, null, "RdInk3");
        DockPanel.SetDock(v, Dock.Right);
        foot.Children.Add(v);
        foot.Children.Add(Rd.Figure("freedisk.space", 11, null, "RdInk3"));
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);
        var list = new StackPanel();
        foreach (var (title, icon) in Sections)
        {
            bool on = _section == title;
            var row = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
            var glyph = Ui.Glyph(icon, 13, Ui.Brush(on ? "RdInk" : "RdInk2"));
            glyph.Width = 18;
            glyph.Margin = new Thickness(0, 0, 10, 0);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            row.Children.Add(Rd.Text(title, 13, null, on ? "RdInk" : "RdInk2"));
            var layers = new Grid();
            layers.Children.Add(row);
            if (on) layers.Children.Add(new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Brush("AppAccent") });
            var item = new Border
            {
                Height = 32, CornerRadius = new CornerRadius(7), Child = layers, Cursor = Cursors.Hand,
                Background = on ? Ui.Brush("RdSoft") : Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2),
            };
            string captured = title;
            item.MouseLeftButtonUp += (_, _) =>
            {
                _section = captured;
                Rebuild();
                if (_groups.TryGetValue(captured, out var group)) Dispatcher.InvokeAsync(group.BringIntoView);
            };
            System.Windows.Automation.AutomationProperties.SetName(item, title);
            list.Children.Add(item);
        }
        dock.Children.Add(list);
        return new Border { Background = Ui.Brush("RdSidebar"), Child = dock };
    }

    // ---- Body ----

    private FrameworkElement Body()
    {
        _groups.Clear();
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(Rd.PageTitle("Settings"));
        var sub = Rd.Text("Changes save as you make them.", 13, null, "RdInk2");
        sub.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(sub);
        var columns = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        var right = new StackPanel { };
        Grid.SetColumn(right, 2);
        void add(StackPanel col, string key, FrameworkElement group)
        {
            if (col.Children.Count > 0) group.Margin = new Thickness(0, 22, 0, 0);
            _groups[key] = group;
            col.Children.Add(group);
        }
        add(left, "General", General());
        add(left, "Scanning", Scanning());
        add(left, "Saved scans", SavedScans());
        add(right, "Clean up", CleanUp());
        add(right, "Updates", Updates());
        add(right, "Appearance", Appearance());
        add(right, "Privacy", Privacy());
        columns.Children.Add(left);
        columns.Children.Add(right);
        stack.Children.Add(columns);
        return stack;
    }

    private FrameworkElement General() => Group("General",
        Row("Show free space in the notification area", "The free space and the last scan, one click away.",
            Switch(_settings.ShowTray, on => { _settings.ShowTray = on; Save(); })));

    private FrameworkElement Scanning() => Group("Scanning",
        Row("ReFS block clones", CloneExplanation(_settings.CloneAccounting),
            Picker(["Count every copy", "Count each family once"], _settings.CloneAccounting ? 1 : 0,
                i => { _settings.CloneAccounting = i == 1; Save(); }), stacked: true),
        Row("Keep storage history",
            "A few hundred folder sizes per scan, so Overview can say what grew. Kept on this PC, never sent anywhere.",
            Switch(_settings.KeepHistory, on => { _settings.KeepHistory = on; Save(); })));

    private static string CloneExplanation(bool on) => on
        ? "Block-cloned copies count once so sizes match the disk; each scan maps every file's extents, which is slower on large drives."
        : "Fastest, but a block-cloned copy (ReFS, Dev Drive) is counted again for every copy, so totals can exceed what the disk uses.";

    private FrameworkElement SavedScans()
    {
        string dir = System.IO.Path.GetDirectoryName(SnapshotStore.DefaultDirectory()) ?? SnapshotStore.DefaultDirectory();
        return Group("Saved scans",
            Row("Stored on this PC", CanonicalPath.DisplayPath(dir),
                Rd.SecondaryButton("Show in File Explorer", () => Explorer.Reveal(SnapshotStore.DefaultDirectory())), mono: true));
    }

    private FrameworkElement CleanUp()
    {
        var choices = new StackPanel { Margin = new Thickness(16) };
        choices.Children.Add(Rd.Text("What to tick for you", 13));
        var a = Choice(_settings.TickRebuilds, "Only things that rebuild by themselves", "Caches and build files. Recommended.",
            () => { _settings.TickRebuilds = true; Save(); });
        a.Margin = new Thickness(0, 8, 0, 0);
        choices.Children.Add(a);
        var b = Choice(!_settings.TickRebuilds, "Nothing, I'll choose", "Every box starts empty.",
            () => { _settings.TickRebuilds = false; Save(); });
        b.Margin = new Thickness(0, 8, 0, 0);
        choices.Children.Add(b);
        return Group("Clean up", new Border { Child = choices, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1) });
    }

    private FrameworkElement Updates()
    {
        var note = new DockPanel { Margin = new Thickness(12) };
        var dot = new Ellipse { Width = 6, Height = 6, Fill = Ui.Brush("AppAccent"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dot, Dock.Left);
        note.Children.Add(dot);
        note.Children.Add(Rd.Text("freedisk.space for Windows never uses the internet. Scans and cleanups stay on this PC; new versions come from the website.", 12, null, "RdAccentDeep", wrap: true));
        var dashed = new Grid { Margin = new Thickness(12) };
        dashed.Children.Add(new Rectangle
        {
            RadiusX = 8, RadiusY = 8, Stroke = Rd.WithOpacity(Ui.Brush("AppAccent"), 0.4), StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        });
        dashed.Children.Add(note);
        var toggle = Switch(false, _ => { });
        toggle.IsEnabled = false;
        toggle.Opacity = 0.45;
        return Group("Updates",
            Row("Check for updates automatically", "This build has no update feed, so it never contacts a server.", toggle),
            dashed);
    }

    private FrameworkElement Appearance()
    {
        string mode = _settings.Appearance;
        var theme = Rd.SegmentSwitch(
        [
            new Rd.Segment("Light", mode == "light", () => SetAppearance("light")),
            new Rd.Segment("Dark", mode == "dark", () => SetAppearance("dark")),
            new Rd.Segment("System", mode == "system", () => SetAppearance("system")),
        ]);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        foreach (var (id, title) in new[] { ("light", "Light"), ("dark", "Dark"), ("system", "System") })
        {
            bool on = mode == id;
            var swatch = new Grid { Width = 72, Height = 46 };
            var light = new SolidColorBrush(Color.FromRgb(250, 250, 250));
            var dark = new SolidColorBrush(Color.FromRgb(20, 20, 20));
            if (id == "system")
            {
                swatch.ColumnDefinitions.Add(new ColumnDefinition());
                swatch.ColumnDefinitions.Add(new ColumnDefinition());
                swatch.Children.Add(new Border { Background = light, CornerRadius = new CornerRadius(8, 0, 0, 8) });
                var d = new Border { Background = dark, CornerRadius = new CornerRadius(0, 8, 8, 0) };
                Grid.SetColumn(d, 1);
                swatch.Children.Add(d);
            }
            else swatch.Children.Add(new Border { Background = id == "light" ? light : dark, CornerRadius = new CornerRadius(8) });
            var frame = new Border
            {
                CornerRadius = new CornerRadius(8), BorderBrush = Ui.Brush(on ? "AppAccent" : "RdLine"),
                BorderThickness = new Thickness(on ? 2 : 1), Child = swatch,
            };
            Grid.SetColumnSpan(frame, 2);
            var item = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            item.Children.Add(frame);
            var label = Rd.Text(title, 12, null, on ? "RdInk" : "RdInk2");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 6, 0, 0);
            item.Children.Add(label);
            string captured = id;
            item.MouseLeftButtonUp += (_, _) => SetAppearance(captured);
            System.Windows.Automation.AutomationProperties.SetName(item, $"{title} theme");
            swatches.Children.Add(item);
        }
        swatches.Children.Add(Rd.Text("System follows Windows, including at night.", 12, null, "RdInk3", wrap: true));
        int scaleIndex = Array.FindIndex(TextScales, t => Math.Abs(t.Scale - _settings.TextScale) < 0.001);
        return Group("Appearance",
            Row("Theme", null, theme),
            new Border { Child = swatches, BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1) },
            Row("Text size", null, Picker(TextScales.Select(t => t.Label).ToArray(), Math.Max(0, scaleIndex), i =>
            {
                _settings.TextScale = TextScales[i].Scale;
                _onTextScale(TextScales[i].Scale);
                Rebuild();
            })),
            Row("Show Dusty", "The mascot, in empty states and after a cleanup. Never on a step that removes files.",
                Switch(_settings.ShowDusty, on => { _settings.ShowDusty = on; Save(); })));
    }

    private static readonly (double Scale, string Label)[] TextScales =
        [(0.9, "Smaller"), (1.0, "Standard"), (1.15, "Larger"), (1.3, "Largest")];

    private FrameworkElement Privacy()
    {
        bool admin = Harness.IsElevated();
        FrameworkElement access = admin
            ? Rd.Tag("Allowed", Rd.TagTone.Accent)
            : Rd.Tag("Off");
        return Group("Privacy",
            Row("Administrator access", admin
                ? "On. freedisk.space can read every folder and use the fast MFT scan."
                : "Off. Some system folders are left out of scans, and scans walk folders instead of reading the MFT.", access),
            Row("Analytics and crash reports", "None. There is nothing to turn on.", Rd.Tag("Never")),
            Row("Reset all settings", "Saved scans and History are kept.", Rd.SecondaryButton("Reset…", ConfirmReset)));
    }

    // ---- Actions ----

    private void Save()
    {
        _settings.Save();
        _onGeneralChanged();
        Rebuild();
    }

    private void SetAppearance(string mode)
    {
        _settings.Appearance = mode;
        _onAppearance(mode);
        Rebuild();
    }

    private void ConfirmReset()
    {
        if (!Dialogs.Confirm(this, "Reset all settings?",
                "Every choice on this page goes back to how it started. Saved scans and History are kept.",
                [], "Reset settings"))
            return;
        var fresh = new AppSettings();
        fresh.Save();
        _settings.Appearance = fresh.Appearance;
        _settings.TextScale = fresh.TextScale;
        _settings.KeepHistory = fresh.KeepHistory;
        _settings.CloneAccounting = fresh.CloneAccounting;
        _settings.ShowDusty = fresh.ShowDusty;
        _settings.TickRebuilds = fresh.TickRebuilds;
        _settings.ShowTray = fresh.ShowTray;
        _onTextScale(fresh.TextScale);
        _onAppearance(fresh.Appearance);
        _onGeneralChanged();
        Rebuild();
    }

    // ---- Pieces ----

    /// <summary>A titled card of rows (cardTitle 14 semibold, 4 in; a card with radius 12).</summary>
    private static FrameworkElement Group(string title, params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        var t = Rd.CardTitle(title);
        t.Margin = new Thickness(4, 0, 0, 10);
        stack.Children.Add(t);
        var body = new StackPanel();
        foreach (var row in rows) body.Children.Add(row);
        stack.Children.Add(Rd.Surface(body, "RdCard", 12));
        return stack;
    }

    /// <summary>Title and detail on the left, the control on the right (or below when stacked); min 58 tall.</summary>
    private static FrameworkElement Row(string title, string? detail, FrameworkElement control, bool mono = false, bool stacked = false)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Rd.Text(title, 13));
        if (detail is not null)
        {
            var d = mono ? Rd.Figure(detail, 11, null, "RdInk3") : Rd.Text(detail, 12, null, "RdInk3", wrap: true);
            if (mono) d.TextWrapping = TextWrapping.Wrap;
            d.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(d);
        }
        FrameworkElement layout;
        if (stacked)
        {
            var stack = new StackPanel();
            stack.Children.Add(text);
            control.Margin = new Thickness(0, 12, 0, 0);
            control.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(control);
            layout = stack;
        }
        else
        {
            var dock = new DockPanel();
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(16, 0, 0, 0);
            DockPanel.SetDock(control, Dock.Right);
            dock.Children.Add(control);
            dock.Children.Add(text);
            layout = dock;
        }
        System.Windows.Automation.AutomationProperties.SetName(layout, title);
        return new Border
        {
            MinHeight = 58, Padding = new Thickness(16, 12, 16, 12), Child = layout,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>The macOS switch: 38 x 22 capsule, accent when on, a white knob.</summary>
    private static Border Switch(bool on, Action<bool> changed)
    {
        var knob = new Ellipse
        {
            Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.25 },
        };
        var track = new Border
        {
            Width = 38, Height = 22, CornerRadius = new CornerRadius(11),
            Background = on ? Ui.Brush("AppAccent") : Rd.WithOpacity(Ui.Brush("RdInk"), 0.15),
            Child = knob, Cursor = Cursors.Hand, Focusable = true,
        };
        track.MouseLeftButtonUp += (_, _) => changed(!on);
        track.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { changed(!on); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(track, on ? "On" : "Off");
        return track;
    }

    /// <summary>A pop-up picker: the current value as a chip; a click shows the options.</summary>
    private static Border Picker(string[] options, int selected, Action<int> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Rd.Text(options[selected], 13));
        var chevrons = Ui.Glyph("BsChevronExpand", 9, Ui.Brush("RdInk3"));
        chevrons.Margin = new Thickness(8, 0, 0, 0);
        chevrons.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(chevrons);
        var box = new Border
        {
            Height = 28, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(6),
            Background = Ui.Brush("RdCard"), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1),
            Child = row, Cursor = Cursors.Hand,
        };
        box.MouseLeftButtonUp += (_, _) =>
        {
            var menu = new ContextMenu();
            for (int i = 0; i < options.Length; i++)
            {
                var item = new MenuItem { Header = (i == selected ? "✓  " : "     ") + options[i] };
                int captured = i;
                item.Click += (_, _) => pick(captured);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = box;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        System.Windows.Automation.AutomationProperties.SetName(box, options[selected]);
        return box;
    }

    /// <summary>A radio choice: dot, title over detail; the accent wash when picked.</summary>
    private static Border Choice(bool on, string title, string detail, Action action)
    {
        var dot = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        dot.Children.Add(new Ellipse { Stroke = Ui.Brush(on ? "AppAccent" : "RdInk3"), StrokeThickness = 1.5 });
        if (on) dot.Children.Add(new Ellipse { Fill = Ui.Brush("AppAccent"), Margin = new Thickness(4) });
        var row = new DockPanel();
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        var text = new StackPanel();
        text.Children.Add(Rd.Text(title, 13));
        var d = Rd.Text(detail, 12, null, "RdInk3");
        d.Margin = new Thickness(0, 2, 0, 0);
        text.Children.Add(d);
        row.Children.Add(text);
        var box = new Border
        {
            Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Child = row, Cursor = Cursors.Hand,
            Background = on ? Ui.Brush("RdAccentWash") : Brushes.Transparent,
        };
        box.MouseLeftButtonUp += (_, _) => action();
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        return box;
    }
}
