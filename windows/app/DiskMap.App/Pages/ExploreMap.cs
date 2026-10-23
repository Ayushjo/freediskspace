using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>The four colourings of Explore's map (Mac: ExploreColorMode).</summary>
public enum ExploreColorMode { Freeable, Type, Folder, Age }

public static class ExploreColorModeText
{
    public static string Title(this ExploreColorMode mode) => mode switch
    {
        ExploreColorMode.Freeable => "Can be freed",
        ExploreColorMode.Type => "By type",
        ExploreColorMode.Folder => "By folder",
        _ => "By age",
    };
}

/// <summary>
/// WIN-107: Explore's treemap (Mac: ExploreTreemapView). "Can be freed" draws the redesign's
/// outlined two-level map — each big folder holds its biggest children and a "+ more" cell —
/// with violet stripes on what can be freed; the other colourings draw coloured tiles.
/// Click selects, double-click opens a folder, arrows move, Enter opens, Backspace goes up.
/// </summary>
internal sealed class ExploreMap : FrameworkElement
{
    private static ScanModel Model => ScanModel.Shared;

    public const double HeaderHeight = 24;
    public const double InnerPadding = 6;

    private readonly ExploreColorMode _mode;
    private readonly FreeableIndex _freeable;
    private List<TreemapRect> _rects = [];
    private Dictionary<int, List<TreemapRect>> _children = [];
    private Dictionary<int, (Rect Rect, long Bytes)> _more = [];
    private Dictionary<int, string> _folderTypeHex = [];
    private int? _hovered;
    private Size _laidFor;
    private int _laidNode = -1;

    public ExploreMap(ExploreColorMode mode, FreeableIndex freeable)
    {
        _mode = mode;
        _freeable = freeable;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        System.Windows.Automation.AutomationProperties.SetName(this, "Map of the folder");
    }

    private bool Blueprint => _mode == ExploreColorMode.Freeable;

    // ---- Layout ----

    private void EnsureLayout()
    {
        var tree = Model.Tree;
        var totals = Model.Totals;
        var size = new Size(ActualWidth, ActualHeight);
        if (tree is null || totals.Length != tree.Count || size.Width <= 0 || size.Height <= 0)
        {
            _rects = [];
            return;
        }
        int node = Model.ZoomedNode;
        if (node == _laidNode && size == _laidFor) return;
        _laidNode = node;
        _laidFor = size;
        var items = tree.ChildrenOf(node, totals).Where(c => c.Size > 0).ToList();
        _rects = SquarifiedTreemap.Layout(items, new DmRect(0, 0, size.Width, size.Height));
        (_children, _more) = Blueprint ? NestedLayout(_rects, tree, totals) : ([], []);
        _folderTypeHex = _mode == ExploreColorMode.Type
            ? _rects.Where(r => tree.IsDirectory[r.Id]).ToDictionary(r => r.Id, r => FileTypes.TileColorOf(FileTypes.DominantKind(tree, totals, r.Id)))
            : [];
    }

    /// <summary>
    /// Children inside each big enough folder tile: the biggest few (at most 9, stopping once 90%
    /// is shown with at least 2), and the rest as one "+ more" cell, so slivers never fill the map.
    /// </summary>
    public static (Dictionary<int, List<TreemapRect>>, Dictionary<int, (Rect, long)>) NestedLayout(
        List<TreemapRect> rects, FileTree tree, long[] totals)
    {
        var kids = new Dictionary<int, List<TreemapRect>>();
        var more = new Dictionary<int, (Rect, long)>();
        foreach (var r in rects)
        {
            if (!tree.IsDirectory[r.Id]) continue;
            var box = Inset(ToRect(r.Rect), 2);
            var inner = new Rect(box.X + InnerPadding, box.Y + HeaderHeight,
                Math.Max(0, box.Width - InnerPadding * 2), Math.Max(0, box.Height - HeaderHeight - InnerPadding));
            if (inner.Width < 60 || inner.Height < 40) continue;
            var children = tree.ChildrenOf(r.Id, totals).Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
            if (children.Count == 0) continue;
            long total = Math.Max(1, children.Sum(c => c.Size));
            var shown = new List<(int Id, long Size)>();
            long running = 0;
            foreach (var c in children)
            {
                if (shown.Count >= 9 || (shown.Count >= 2 && (double)running / total > 0.9)) break;
                shown.Add(c);
                running += c.Size;
            }
            long rest = total - running;
            var items = new List<(int Id, long Size)>(shown);
            if (rest > 0) items.Add((-1, rest));
            var laid = SquarifiedTreemap.Layout(items, new DmRect(inner.X, inner.Y, inner.Width, inner.Height));
            kids[r.Id] = laid.Where(l => l.Id >= 0).ToList();
            if (laid.FirstOrDefault(l => l.Id == -1) is { Id: -1 } m) more[r.Id] = (ToRect(m.Rect), rest);
        }
        return (kids, more);
    }

    private static Rect ToRect(DmRect r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    private static Rect Inset(Rect r, double by) =>
        r.Width > by * 2 && r.Height > by * 2 ? new Rect(r.X + by, r.Y + by, r.Width - by * 2, r.Height - by * 2) : Rect.Empty;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    /// <summary>The model moved (zoom, selection, basis): lay out again if needed and redraw.</summary>
    public void Refresh()
    {
        _laidNode = -1;
        InvalidateVisual();
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        EnsureLayout();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Model.Tree is not { } tree) return;
        var totals = Model.Totals;
        if (Blueprint) DrawBlueprint(dc, tree, totals); else DrawTiles(dc, tree, totals);
    }

    private bool Selected(int id) => id == Model.SelectedNode || Model.MultiSelection.Contains(id);

    private void DrawBlueprint(DrawingContext dc, FileTree tree, long[] totals)
    {
        var outline = new Pen(Rd.WithOpacity(Ui.Brush("RdInk"), 0.88), 1);
        var innerLine = new Pen(Rd.WithOpacity(Ui.Brush("RdInk3"), 0.75), 1);
        var accentPen = new Pen(Ui.Brush("AppAccent"), 1);
        var selectPen = new Pen(Ui.Brush("AppAccent"), 2.5);
        foreach (var r in _rects)
        {
            var box = Inset(ToRect(r.Rect), 2);
            if (box.IsEmpty || box.Width <= 1 || box.Height <= 1) continue;
            bool free = _freeable.IsMostly(r.Id, tree, totals);
            if (free) DrawHatch(dc, box);
            else dc.DrawRectangle(r.Id == _hovered && !_children.ContainsKey(r.Id) ? Ui.Brush("RdAccentWash") : Ui.Brush("RdCard"), null, box);
            dc.DrawRectangle(null, Snapped(free ? accentPen : outline), Half(box));
            Label(dc, tree.NameOf(r.Id), totals[r.Id], box, strong: true, free ? "RdAccentDeep" : "RdInk", onHatch: free);
            foreach (var c in _children.GetValueOrDefault(r.Id) ?? [])
            {
                var cell = Inset(ToRect(c.Rect), 2);
                if (cell.IsEmpty || cell.Width <= 1 || cell.Height <= 1) continue;
                bool cfree = free || _freeable.IsMostly(c.Id, tree, totals);
                if (cfree) DrawHatch(dc, cell);
                else dc.DrawRectangle(c.Id == _hovered ? Ui.Brush("RdAccentWash") : Ui.Brush("RdCard"), null, cell);
                dc.DrawRectangle(null, Snapped(cfree ? accentPen : innerLine), Half(cell));
                if (cell.Width > 58 && cell.Height > 22)
                    Label(dc, tree.NameOf(c.Id), totals[c.Id], cell, strong: false, cfree ? "RdAccentDeep" : "RdInk", onHatch: cfree);
                if (Selected(c.Id)) dc.DrawRectangle(null, selectPen, Inset(cell, 0.5) is { IsEmpty: false } s ? s : cell);
            }
            if (_more.TryGetValue(r.Id, out var more))
            {
                var cell = Inset(more.Rect, 2);
                if (!cell.IsEmpty && cell.Width > 1 && cell.Height > 1)
                {
                    dc.DrawRectangle(Ui.Brush("RdCard"), null, cell);
                    dc.DrawRectangle(null, Snapped(innerLine), Half(cell));
                    if (cell.Width > 58 && cell.Height > 22)
                        Label(dc, "+ more", more.Bytes, cell, strong: false, "RdInk3");
                }
            }
            if (Selected(r.Id)) dc.DrawRectangle(null, selectPen, Inset(box, 0.5) is { IsEmpty: false } s ? s : box);
        }
    }

    /// <summary>A 1 px stroke drawn on pixel centres, like the Mac's crisp hairlines.</summary>
    private static Rect Half(Rect r) => new(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));

    private static Pen Snapped(Pen pen) => pen;

    /// <summary>HatchFill: accent wash, then 1.5 pt violet stripes every 7 pt at 55%, clipped to the box.</summary>
    private static void DrawHatch(DrawingContext dc, Rect box)
    {
        dc.DrawRectangle(Ui.Brush("RdAccentWash"), null, box);
        var stripe = new Pen(Rd.WithOpacity(Ui.Brush("AppAccent"), 0.55), 1.5);
        dc.PushClip(new RectangleGeometry(box));
        var lines = new StreamGeometry();
        using (var g = lines.Open())
        {
            for (double x = box.Left - box.Height; x < box.Right; x += 7)
            {
                g.BeginFigure(new Point(x, box.Bottom), false, false);
                g.LineTo(new Point(x + box.Height, box.Top), true, false);
            }
        }
        lines.Freeze();
        dc.DrawGeometry(null, stripe, lines);
        dc.Pop();
    }

    private static readonly Typeface NameStrong = new(Rd.FontFor(12), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NameMedium = new(Rd.FontFor(12), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly Typeface MonoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>"Name  12.4 GB" in the top-left corner, cut to fit; a card pill under it on stripes.</summary>
    private void Label(DrawingContext dc, string name, long bytes, Rect rect, bool strong, string tint, bool onHatch = false)
    {
        if (rect.Width <= 40 || rect.Height <= 18) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string size = ByteFormat.Format(bytes);
        double available = rect.Width - 14;
        bool showSize = available > (name.Length + size.Length) * 6.6 + 10;
        int maxChars = Math.Max(3, (int)(available / 6.9));
        string shown = name.Length > maxChars ? name[..(maxChars - 1)] + "…" : name;
        var nameText = new FormattedText(shown, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            strong ? NameStrong : NameMedium, Ui.Scaled(12), Ui.Brush(tint), dpi);
        FormattedText? sizeText = showSize
            ? new FormattedText("  " + size, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, MonoFace, Ui.Scaled(11),
                onHatch ? Rd.WithOpacity(Ui.Brush("RdAccentDeep"), 0.8) : Ui.Brush("RdInk3"), dpi)
            : null;
        double width = nameText.WidthIncludingTrailingWhitespace + (sizeText?.WidthIncludingTrailingWhitespace ?? 0);
        double height = Math.Max(nameText.Height, sizeText?.Height ?? 0);
        if (onHatch)
        {
            var pill = new Rect(rect.X + 4, rect.Y + 3, Math.Min(available + 6, width + 6), height + 4);
            dc.DrawRoundedRectangle(Ui.Brush("RdCard"), null, pill, 3, 3);
        }
        dc.PushClip(new RectangleGeometry(new Rect(rect.X, rect.Y, rect.Width, rect.Height)));
        dc.DrawText(nameText, new Point(rect.X + 7, rect.Y + 5));
        if (sizeText is not null)
        {
            // Same baseline as the name.
            double baselineShift = nameText.Baseline - sizeText.Baseline;
            dc.DrawText(sizeText, new Point(rect.X + 7 + nameText.WidthIncludingTrailingWhitespace, rect.Y + 5 + baselineShift));
        }
        dc.Pop();
    }

    /// <summary>By folder / By type / By age: rounded coloured tiles with a soft top light.</summary>
    private void DrawTiles(DrawingContext dc, FileTree tree, long[] totals)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new SolidColorBrush(Color.FromRgb(37, 43, 49));
        foreach (var r in _rects)
        {
            var inset = Inset(ToRect(r.Rect), 1.5);
            if (inset.IsEmpty || inset.Width <= 0.5 || inset.Height <= 0.5) continue;
            double radius = Math.Min(6, Math.Min(inset.Width, inset.Height) / 3);
            bool hovered = r.Id == _hovered;
            dc.DrawRoundedRectangle(new SolidColorBrush(ColorFor(r.Id, tree)), null, inset, radius, radius);
            var light = new LinearGradientBrush(
                Color.FromArgb((byte)(hovered ? 71 : 36), 255, 255, 255),
                Color.FromArgb((byte)(hovered ? 31 : 0), 255, 255, 255), 90);
            dc.DrawRoundedRectangle(light, null, inset, radius, radius);
            if (Selected(r.Id)) dc.DrawRoundedRectangle(null, new Pen(Ui.Brush("AppAccent"), 2), inset, radius, radius);
            if (inset.Width > 52 && inset.Height > 22)
            {
                string name = tree.NameOf(r.Id);
                int keep = Math.Max(4, (int)(inset.Width / 7) - 3);
                var text = new FormattedText(name.Length > keep ? name[..keep] : name, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, NameMedium, Ui.Scaled(12), Rd.WithOpacity(label, 0.85), dpi);
                dc.PushClip(new RectangleGeometry(inset));
                dc.DrawText(text, new Point(inset.X + 8, inset.Y + 7));
                if (inset.Width > 80 && inset.Height > 46)
                {
                    var size = new FormattedText(ByteFormat.Format(totals[r.Id]), CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, MonoFace, Ui.Scaled(11), Rd.WithOpacity(label, 0.6), dpi);
                    dc.DrawText(size, new Point(inset.X + 8, inset.Y + 25));
                }
                dc.Pop();
            }
        }
    }

    private static readonly string[] DataPalette = ["849BB8", "A795C7", "C78797", "7BA89C", "B9A071", "8FACC0", "A2A4AC"];

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString("#" + hex.TrimStart('#'));

    /// <summary>A data tint composited over the light canvas so tiles look the same in both appearances.</summary>
    private static Color Wash(Color c, double strength)
    {
        double a = Math.Clamp(strength, 0, 1);
        byte mix(byte v, double bg) => (byte)Math.Round(v * a + bg * (1 - a));
        return Color.FromRgb(mix(c.R, 248), mix(c.G, 247), mix(c.B, 244));
    }

    private static int AncestorDepth(int id, FileTree tree)
    {
        int d = 0;
        for (int c = id; c >= 0 && d <= 64; c = tree.Parent[c]) d++;
        return d;
    }

    /// <summary>ExploreColoring.color: folder hue, dominant type, or age band.</summary>
    private Color ColorFor(int id, FileTree tree)
    {
        switch (_mode)
        {
            case ExploreColorMode.Folder:
            {
                int top = id;
                for (int c = id; c > 0 && tree.Parent[c] > 0; c = tree.Parent[c]) top = tree.Parent[c];
                string name = tree.NameOf(top).ToLowerInvariant();
                var baseColor = name switch
                {
                    "appdata" or "library" => Hex("A795C7"),
                    "downloads" => Hex("C78797"),
                    "desktop" or "documents" => Hex("849BB8"),
                    "program files" or "applications" => Hex("B9A071"),
                    _ => Hex(DataPalette[Math.Abs(top) % DataPalette.Length]),
                };
                int depth = Math.Max(0, AncestorDepth(id, tree) - AncestorDepth(top, tree));
                return depth == 0 ? baseColor : Wash(baseColor, 1.0 - Math.Min(0.35, depth * 0.08));
            }
            case ExploreColorMode.Type:
                if (tree.IsDirectory[id])
                    return _folderTypeHex.TryGetValue(id, out var hex) && hex.Length > 0 ? Hex(hex) : Wash(Hex(DataPalette[6]), 0.85);
                return Hex(FileTypes.TileColorOf(FileTypes.KindOf(tree, id)));
            case ExploreColorMode.Age:
                return AgeMap.Bucket(tree.ModifiedDay[id], AgeMap.Today()) switch
                {
                    AgeBucket.Under30 => Hex("7BA89C"),
                    AgeBucket.Days30To90 => Hex("8FACC0"),
                    AgeBucket.Days90To365 => Hex("B9A071"),
                    AgeBucket.OneToTwoYears => Hex("C99A7E"),
                    AgeBucket.OverTwoYears => Hex("C78797"),
                    _ => Wash(Hex("949AA2"), 0.5),
                };
            default:
            {
                (byte, byte, byte)[] levels = [(232, 232, 228), (242, 242, 238), (222, 222, 217), (248, 248, 245)];
                var l = levels[AncestorDepth(id, tree) % levels.Length];
                return Color.FromRgb(l.Item1, l.Item2, l.Item3);
            }
        }
    }

    // ---- Input ----

    /// <summary>The deepest tile under the point: a child when the map shows one.</summary>
    private int? Hit(Point p)
    {
        if (SquarifiedTreemap.HitTest(_rects, new DmPoint(p.X, p.Y)) is not { } top) return null;
        if (Blueprint && _children.TryGetValue(top, out var kids) && SquarifiedTreemap.HitTest(kids, new DmPoint(p.X, p.Y)) is { } child)
            return child;
        return top;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = Hit(e.GetPosition(this));
        if (hit != _hovered)
        {
            _hovered = hit;
            if (Model.Tree is { } tree && hit is { } id && id < Model.Totals.Length)
                ToolTip = $"{tree.NameOf(id)} — {ByteFormat.Format(Model.Totals[id])}{(_freeable.Contains(id, tree) ? " · can be freed" : "")}";
            else ToolTip = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hovered = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (Hit(e.GetPosition(this)) is not { } id || Model.Tree is not { } tree) return;
        if (e.ClickCount == 2)
        {
            if (tree.IsDirectory[id]) Model.DrillTo(id);
            Model.Select(id);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Model.ToggleMulti(id);
        }
        else
        {
            Model.ClearMulti();
            Model.Select(id);
        }
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (Hit(e.GetPosition(this)) is not { } id || Model.Tree is not { } tree) return;
        Model.Select(id);
        string path = Model.PathOf(id);
        var menu = new System.Windows.Controls.ContextMenu();
        void item(string header, Action action)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        if (tree.IsDirectory[id]) item("Open", () => Model.DrillTo(id));
        item("Show in File Explorer", () => Explorer.Reveal(path));
        item("Add to basket", () => Model.Stage(id, "Explore: " + tree.NameOf(id)));
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Model.Tree is not { } tree) return;
        int selected = Model.SelectedNode;
        switch (e.Key)
        {
            case Key.Enter when selected >= 0 && selected < tree.Count && tree.IsDirectory[selected]
                                && _rects.Any(r => r.Id == selected):
                Model.DrillTo(selected);
                e.Handled = true;
                break;
            case Key.Back when Model.ZoomedNode > 0:
                Model.DrillToAncestor(tree.Parent[Model.ZoomedNode]);
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var tiles = _rects.Select(r => (r.Id, ToRect(r.Rect))).ToList();
                if (Neighbor(tiles.Any(t => t.Id == selected) ? selected : null, e.Key, tiles) is { } next) Model.Select(next);
                e.Handled = true;
                break;
        }
    }

    /// <summary>The tile next to <paramref name="from"/> in a direction: nearest centre that way (ChartNavigation.neighbor).</summary>
    private static int? Neighbor(int? from, Key key, List<(int Id, Rect Rect)> tiles)
    {
        if (tiles.Count == 0) return null;
        if (from is null) return tiles[0].Id;
        var origin = tiles.First(t => t.Id == from).Rect;
        Point o = new(origin.X + origin.Width / 2, origin.Y + origin.Height / 2);
        return tiles.Where(t => t.Id != from)
            .Select(t => (t.Id, C: new Point(t.Rect.X + t.Rect.Width / 2, t.Rect.Y + t.Rect.Height / 2)))
            .Where(t => key switch
            {
                Key.Left => t.C.X < o.X - 1,
                Key.Right => t.C.X > o.X + 1,
                Key.Up => t.C.Y < o.Y - 1,
                _ => t.C.Y > o.Y + 1,
            })
            .OrderBy(t => Math.Pow(t.C.X - o.X, 2) + Math.Pow(t.C.Y - o.Y, 2))
            .Select(t => (int?)t.Id)
            .FirstOrDefault();
    }
}
