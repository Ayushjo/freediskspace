using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskMap.Core;
using DiskMap.Core.Native;

namespace DiskMap.App;

/// <summary>
/// Stable per-node colors: hue derived from the node id so the same node
/// renders the same color across pages, matching the macOS build's
/// hash-based palette (id * 2654435761 % 360, saturation .55, value .85).
/// </summary>
public static class NodeColors
{
    public static Color ColorFor(int nodeId)
    {
        unchecked
        {
            // Same hash→hue as macOS nodeColor: id * 2654435761 % 360,
            // saturation .55, brightness .82.
            double hue = (uint)((ulong)nodeId * 2654435761UL) % 360;
            return Hsv(hue, 0.55, 0.82);
        }
    }

    /// <summary>Collapsed "Other" slices — gray at 55% like the macOS build.</summary>
    public static readonly Brush OtherBrush = Freeze(new SolidColorBrush(Color.FromArgb(140, 128, 128, 128)));

    // Hues are whole degrees, so 360 frozen brushes cover every node — the
    // charts used to allocate a brush per cell per render.
    private static readonly Brush[] HueBrushes = Enumerable.Range(0, 360)
        .Select(h => (Brush)Freeze(new SolidColorBrush(Hsv(h, 0.55, 0.82)))).ToArray();

    public static Brush BrushFor(int nodeId) =>
        HueBrushes[unchecked((uint)((ulong)nodeId * 2654435761UL) % 360)];

    public static Brush BrushFor(int nodeId, ScanModel model)
    {
        var tree = model.Tree;
        if (tree is null || model.Totals.Length != tree.Count || nodeId < 0 || nodeId >= tree.Count)
            return BrushFor(nodeId);
        return model.ColoringMode switch
        {
            "type" => Ui.Hex(FileTypes.TileColorOf(FileTypes.DominantKind(tree, model.Totals, nodeId))),
            "age" => DominantAgeBrush(tree, model.Totals, nodeId),
            "folder" => FolderBrush(tree.NameOf(nodeId)),
            _ => BrushFor(nodeId),
        };
    }

    private static Brush FolderBrush(string name)
    {
        int hash = 0;
        foreach (char c in name) hash = unchecked(hash * 31 + char.ToLowerInvariant(c));
        return Ui.Data((int)((uint)hash % Ui.DataPalette.Length));
    }

    private static Brush DominantAgeBrush(FileTree tree, long[] totals, int node)
    {
        var buckets = new Dictionary<AgeBucket, long>();
        var stack = new Stack<int>([node]);
        while (stack.TryPop(out int id))
        {
            if (tree.IsDirectory[id])
            {
                int child = tree.FirstChild[id];
                while (child != -1) { stack.Push(child); child = tree.NextSibling[child]; }
            }
            else
            {
                var bucket = Core.AgeMap.Bucket(tree.ModifiedDay[id], Core.AgeMap.Today());
                buckets[bucket] = buckets.GetValueOrDefault(bucket) + totals[id];
            }
        }
        return buckets.Count == 0
            ? OtherBrush
            : AgeBucket(buckets.OrderByDescending(pair => pair.Value).First().Key);
    }

    private static Color Hsv(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(
            (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public static readonly Brush DirectoryOverlay = Freeze(new SolidColorBrush(Color.FromArgb(0x20, 0, 0, 0)));
    public static readonly Brush Stroke = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)));
    public static readonly Pen StrokePen = Freeze(new Pen(Stroke, 1));
    public static readonly Pen ConnectorPen = Freeze(new Pen(Stroke, 1.5));

    /// <summary>Focused-cell ring — the white stroke the treemap draws (WIN-061).</summary>
    public static readonly Pen SelectionPen = Freeze(new Pen(Brushes.White, 2.5));

    /// <summary>Multi-select ring — the accent pen; resolved per render so theme swaps follow.</summary>
    public static Pen MultiPen() => new(Ui.Brush("AppAccent"), 2);

    /// <summary>Age-map bucket colors — ported from macOS AgeMapView.</summary>
    public static Brush AgeBucket(AgeBucket bucket) => Freeze(new SolidColorBrush(bucket switch
    {
        Core.AgeBucket.Under30 => Hsv(0.42 * 360, 0.45, 0.72),
        Core.AgeBucket.Days30To90 => Hsv(0.38 * 360, 0.5, 0.62),
        Core.AgeBucket.Days90To365 => Hsv(0.12 * 360, 0.55, 0.78),
        Core.AgeBucket.OneToTwoYears => Hsv(0.06 * 360, 0.65, 0.72),
        Core.AgeBucket.OverTwoYears => Hsv(0.02 * 360, 0.7, 0.55),
        _ => Hsv(0, 0, 0.6),
    }));

    private static T Freeze<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }
}

/// <summary>
/// How every page and chart listens to <see cref="ScanModel"/>: only while
/// on screen (they used to subscribe forever, so every page ever opened
/// kept re-rendering in the background), refreshed when it appears if the
/// model moved while it was away, and coalesced into one callback queued
/// below input priority so a burst of changes can't starve clicks.
/// </summary>
public static class ModelEvents
{
    public static void WhileLoaded(FrameworkElement element, Action onChange)
    {
        bool queued = false;
        int builtFor = -1;   // the model version the last callback saw
        void Queue()
        {
            if (queued) return;
            queued = true;
            element.Dispatcher.InvokeAsync(() =>
            {
                queued = false;
                builtFor = ScanModel.Shared.Version;
                onChange();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        EventHandler handler = (_, _) => Queue();
        element.Loaded += (_, _) =>
        {
            ScanModel.Shared.StateChanged += handler;
            // A cached page shown again rebuilt in full on every sidebar click, changed or not.
            if (builtFor != ScanModel.Shared.Version) Queue();
        };
        element.Unloaded += (_, _) => ScanModel.Shared.StateChanged -= handler;
    }
}

/// <summary>
/// A button around content the caller draws (a nav row, a picker chip, a swatch): Tab reaches
/// it, Enter and Space click it, and it has a name for screen readers. Rd's PlainTemplate
/// pattern, with the content stretched to the face and no label of its own.
/// </summary>
public static class PlainButton
{
    private static readonly Dictionary<double, ControlTemplate> Templates = [];

    public static Button Create(UIElement content, string name, Action? onClick, double radius = 0)
    {
        var button = new Button
        {
            Content = content,
            Template = Template(radius),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            // Laid out like the Border it replaces, whatever the theme's Button style says.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    private static ControlTemplate Template(double radius)
    {
        if (Templates.TryGetValue(radius, out var cached)) return cached;
        // Pressed: 82%, as Rd's buttons.
        string xaml = $@"
<ControlTemplate TargetType=""Button""
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Border x:Name=""Face"" CornerRadius=""{radius.ToString(System.Globalization.CultureInfo.InvariantCulture)}""
          Background=""{{TemplateBinding Background}}"" BorderBrush=""{{TemplateBinding BorderBrush}}""
          BorderThickness=""{{TemplateBinding BorderThickness}}"" Padding=""{{TemplateBinding Padding}}""
          SnapsToDevicePixels=""True"">
    <ContentPresenter HorizontalAlignment=""{{TemplateBinding HorizontalContentAlignment}}""
                      VerticalAlignment=""{{TemplateBinding VerticalContentAlignment}}""/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsPressed"" Value=""True"">
      <Setter TargetName=""Face"" Property=""Opacity"" Value=""0.82""/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";
        var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        Templates[radius] = template;
        return template;
    }
}

/// <summary>
/// Window sizes that fit the screen: 1366×768 at 125 % leaves about 1093×576 DIPs, less than
/// the main and Settings windows wanted, even as a minimum.
/// </summary>
public static class WindowFit
{
    /// <summary>A preferred size and minimum, neither bigger than what the screen has.</summary>
    internal static (double Size, double Minimum) Clamp(double preferred, double minimum, double available) =>
        (Math.Min(preferred, available), Math.Min(minimum, available));

    public static void ToWorkArea(Window window)
    {
        // shortcut: the primary screen's work area; read the window's own monitor if they open elsewhere.
        var area = SystemParameters.WorkArea;
        (window.Width, window.MinWidth) = Clamp(window.Width, window.MinWidth, area.Width);
        (window.Height, window.MinHeight) = Clamp(window.Height, window.MinHeight, area.Height);
    }
}

public static class ByteFormat
{
    /// <summary>Same scale as macOS ByteCountFormatter (decimal units).</summary>
    public static string Format(long bytes) => Core.HumanUnits.Format(bytes);
}

/// <summary>
/// Explorer integration — the Windows counterpart of Quick Look / Reveal. Everything starts
/// through File Explorer by its full path and with the signed-in user's own rights
/// (<see cref="UnelevatedLaunch"/>), never this administrator process's; programs and scripts
/// are revealed, never run.
/// </summary>
public static class Explorer
{
    private static string ExplorerExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Extensions Open reveals instead of running (Quick Look never runs anything).</summary>
    internal static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".scr", ".pif", ".lnk", ".url", ".reg",
        ".cpl", ".hta", ".jar", ".application", ".appref-ms", ".gadget", ".inf", ".sys", ".dll",
        ".py", ".pyw", ".chm",
    };

    /// <summary>Selects the item in File Explorer, or opens its folder when the item is gone.</summary>
    public static void Reveal(string path) =>
        Launch(File.Exists(path) || Directory.Exists(path)
            ? "/select," + Quote(path)
            : Quote(Path.GetDirectoryName(path) ?? path));

    public static void OpenFolder(string path) => Launch(Quote(path));

    /// <summary>A folder opens in File Explorer, a document in its app; programs and scripts are revealed instead.</summary>
    public static void Open(string path)
    {
        if (Directory.Exists(path)) OpenFolder(path);
        else if (!File.Exists(path) || RunnableExtensions.Contains(Path.GetExtension(path))) Reveal(path);
        else Launch(Quote(path));
    }

    /// <summary>A Windows Settings page; the caller checks it is an ms-settings: URI.</summary>
    public static void OpenSettings(string uri) => Launch(uri);

    /// <summary>A terminal in the folder (Windows Terminal, else Command Prompt). Nothing runs; the person types the command.</summary>
    public static void OpenTerminal(string folder)
    {
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(wt) && UnelevatedLaunch.TryStart(wt, "-d " + Quote(folder), folder)) return;
        if (!UnelevatedLaunch.TryStart(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "", folder))
            ScanModel.Shared.Toast("Couldn't open a terminal");
    }

    private static void Launch(string arguments)
    {
        if (!UnelevatedLaunch.TryStart(ExplorerExe, arguments))
            ScanModel.Shared.Toast("Couldn't open File Explorer");
    }

    /// <summary>Quoted for a command line; a drive root keeps a trailing "." so its "\" can't escape the closing quote.</summary>
    internal static string Quote(string path)
    {
        string p = path.TrimEnd('\\', '/');
        if (p.EndsWith(':')) p += @"\.";
        return "\"" + p + "\"";
    }
}
