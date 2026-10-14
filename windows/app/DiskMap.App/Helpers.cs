using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskMap.Core;

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
/// kept re-rendering in the background), refreshed each time it appears,
/// and coalesced into one callback queued below input priority so a burst
/// of changes can't starve clicks.
/// </summary>
public static class ModelEvents
{
    /// <param name="selection">Also follow selection changes (the inspector, charts).</param>
    public static void WhileLoaded(FrameworkElement element, Action onChange, bool selection = false)
    {
        bool queued = false;
        void Queue()
        {
            if (queued) return;
            queued = true;
            element.Dispatcher.InvokeAsync(() =>
            {
                queued = false;
                onChange();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        EventHandler handler = (_, _) => Queue();
        element.Loaded += (_, _) =>
        {
            ScanModel.Shared.StateChanged += handler;
            if (selection) ScanModel.Shared.SelectionChanged += handler;
            Queue();
        };
        element.Unloaded += (_, _) =>
        {
            ScanModel.Shared.StateChanged -= handler;
            if (selection) ScanModel.Shared.SelectionChanged -= handler;
        };
    }

    /// <summary>Runs <paramref name="onSelection"/> (cheap: a highlight repaint) on selection changes while loaded.</summary>
    public static void SelectionWhileLoaded(FrameworkElement element, Action onSelection)
    {
        EventHandler handler = (_, _) => onSelection();
        element.Loaded += (_, _) => ScanModel.Shared.SelectionChanged += handler;
        element.Unloaded += (_, _) => ScanModel.Shared.SelectionChanged -= handler;
    }
}

/// <summary>
/// The one row menu — ⋯ and right-click on every list, the map's rows and
/// review pages: reveal, copy, open in the browser or the map, and add to
/// Cleanup (offered only when the item can be cleaned).
/// </summary>
public static class RowMenu
{
    public static void Open(UIElement anchor, int id, string reason)
    {
        var model = ScanModel.Shared;
        if (model.Tree is not { } tree || id < 0 || id >= tree.Count) return;
        model.Select(id);
        string path = model.PathOf(id);
        bool isDir = tree.IsDirectory[id];
        int parent = tree.Parent[id];
        var menu = new ContextMenu { PlacementTarget = anchor };
        MenuItem Item(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
            return item;
        }
        Item("Reveal in Explorer", () => Explorer.Reveal(path));
        Item("Copy Path", () => { try { Clipboard.SetText(path); } catch { /* clipboard busy */ } });
        Item(isDir ? "Open in File Browser" : "Show in File Browser", () =>
        {
            model.DrillTo(isDir ? id : parent);
            model.Select(id);
            model.ShowPage("File Browser");
        });
        Item("Show in Visualize", () =>
        {
            if (isDir) { model.Visualize(id); return; }
            if (parent >= 0) model.DrillTo(parent);
            model.Select(id);
            model.ShowPage("Visualize");
        });
        menu.Items.Add(new Separator());
        var verdict = model.VerdictOf(id);
        if (id == 0 || verdict.Advice == CleanupAdvice.Never || CleanupQueue.IsExcludedPath(path))
        {
            var locked = Item("Protected — can't be added to Cleanup", () => { });
            locked.IsEnabled = false;
            locked.ToolTip = verdict.Instead ?? verdict.Note ?? "Protected by the safety rules.";
            ToolTipService.SetShowOnDisabled(locked, true);
        }
        else
        {
            // Risky items ask first (Model.Stage shows the warning).
            Item(verdict.Advice == CleanupAdvice.Warn ? "Add to Cleanup…" : "Add to Cleanup",
                () => model.Stage(id, reason));
        }
        menu.IsOpen = true;
    }
}

public static class ByteFormat
{
    /// <summary>Same scale as macOS ByteCountFormatter (decimal units).</summary>
    public static string Format(long bytes) => Core.HumanUnits.Format(bytes);
}

/// <summary>
/// Explorer integration — the Windows counterpart of Quick Look / Reveal.
/// </summary>
public static class Explorer
{
    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(path) ?? path}\"");
        }
        catch { /* explorer missing is not fatal */ }
    }
}
