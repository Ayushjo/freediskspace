using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.App.Controls;
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
/// Click selects, double-click opens a folder, arrows move, Enter opens, Backspace goes up,
/// Shift+F10 or the menu key opens the selected tile's menu. Screen readers get the map as a
/// list of its tiles and hear each arrow-key move.
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
    /// <summary>Every drawn tile's box, top level and nested: hit boxes for screen readers.</summary>
    private Dictionary<int, Rect> _cells = [];
    private int? _hovered;
    private Size _laidFor;
    private int _laidNode = -1;
    private long[]? _laidTotals;

    // Everything a paint draws with, made and frozen once (a page rebuild after a theme change
    // makes a new map). Hover repaints used to allocate new pens, brushes and text per tile.
    private readonly Pen _outline = Frozen(new Pen(Rd.WithOpacity(Ui.Brush("RdInk"), 0.88), 1));
    private readonly Pen _innerLine = Frozen(new Pen(Rd.WithOpacity(Ui.Brush("RdInk3"), 0.75), 1));
    private readonly Pen _accentPen = Frozen(new Pen(Rd.WithOpacity(Ui.Brush("AppAccent"), 1), 1));
    private readonly Pen _selectPen = Frozen(new Pen(Rd.WithOpacity(Ui.Brush("AppAccent"), 1), 2.5));
    private readonly Pen _tileSelectPen = Frozen(new Pen(Rd.WithOpacity(Ui.Brush("AppAccent"), 1), 2));
    private readonly Pen _stripe = Frozen(new Pen(Rd.WithOpacity(Ui.Brush("AppAccent"), 0.55), 1.5));
    private readonly Brush _card = Rd.WithOpacity(Ui.Brush("RdCard"), 1);
    private readonly Brush _wash = Rd.WithOpacity(Ui.Brush("RdAccentWash"), 1);
    private readonly Brush _sizeOnHatch = Rd.WithOpacity(Ui.Brush("RdAccentDeep"), 0.8);
    private readonly Dictionary<string, Brush> _inks = [];
    private readonly Dictionary<(string, Typeface, double, Brush), FormattedText> _texts = [];
    private readonly Dictionary<Rect, Geometry> _clips = [];
    private readonly Dictionary<Rect, Geometry> _hatches = [];
    private double _pixelsPerDip;

    private static readonly Brush TileLabel = Frozen(new SolidColorBrush(Color.FromArgb((byte)(255 * 0.85), 37, 43, 49)));
    private static readonly Brush TileSize = Frozen(new SolidColorBrush(Color.FromArgb((byte)(255 * 0.6), 37, 43, 49)));
    private static readonly Brush TileLight = Frozen(new LinearGradientBrush(Color.FromArgb(36, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90));
    private static readonly Brush TileLightHover = Frozen(new LinearGradientBrush(Color.FromArgb(71, 255, 255, 255), Color.FromArgb(31, 255, 255, 255), 90));
    private static readonly Dictionary<Color, Brush> TileFills = [];
    private static readonly Dictionary<string, Color> HexColors = [];

    public ExploreMap(ExploreColorMode mode, FreeableIndex freeable)
    {
        _mode = mode;
        _freeable = freeable;
        Focusable = true;   // keeps the default focus ring: keyboard users see where they are
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        // Filled per opening (OnContextMenuOpening): a right-click, Shift+F10 or the menu key.
        ContextMenu = new ContextMenu();
    }

    private static T Frozen<T>(T freezable) where T : Freezable { freezable.Freeze(); return freezable; }

    private bool Blueprint => _mode == ExploreColorMode.Freeable;

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
        new ChartPeer(this, "Map", () => _cells.Keys
            .Where(id => id < Model.Totals.Length).OrderByDescending(id => Model.Totals[id]).Take(60),
            id => _cells.TryGetValue(id, out var r) ? r : null);

    // ---- Layout ----

    private void EnsureLayout()
    {
        var tree = Model.Tree;
        var totals = Model.Totals;
        var size = new Size(ActualWidth, ActualHeight);
        if (tree is null || totals.Length != tree.Count || size.Width <= 0 || size.Height <= 0)
        {
            _rects = [];
            _cells = [];
            return;
        }
        int node = Model.ZoomedNode;
        if (node == _laidNode && size == _laidFor && ReferenceEquals(totals, _laidTotals)) return;
        _laidNode = node;
        _laidFor = size;
        _laidTotals = totals;
        _clips.Clear();
        _hatches.Clear();
        var items = tree.ChildrenOf(node, totals).Where(c => c.Size > 0).ToList();
        _rects = SquarifiedTreemap.Layout(items, new DmRect(0, 0, size.Width, size.Height));
        (_children, _more) = Blueprint ? NestedLayout(_rects, tree, totals) : ([], []);
        // FileTypes keeps every folder's dominant kind for these totals: one pass, not a walk per tile.
        _folderTypeHex = _mode == ExploreColorMode.Type
            ? _rects.Where(r => tree.IsDirectory[r.Id]).ToDictionary(r => r.Id, r => FileTypes.TileColorOf(FileTypes.DominantKind(tree, totals, r.Id)))
            : [];
        // The drawn box; a sliver too thin to draw keeps its laid-out rect (it still takes clicks).
        Rect Cell(DmRect laid, double inset) => Inset(ToRect(laid), inset) is { IsEmpty: false } box ? box : ToRect(laid);
        var cells = new Dictionary<int, Rect>();
        foreach (var r in _rects)
        {
            cells[r.Id] = Cell(r.Rect, Blueprint ? 2 : 1.5);
            foreach (var c in _children.GetValueOrDefault(r.Id) ?? []) cells[c.Id] = Cell(c.Rect, 2);
        }
        _cells = cells;
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

    /// <summary>The model moved (zoom, selection, basis): redraw; the layout follows the zoom, size and totals itself.</summary>
    public void Refresh() => InvalidateVisual();

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi != _pixelsPerDip) { _pixelsPerDip = dpi; _texts.Clear(); }
        EnsureLayout();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Model.Tree is not { } tree) return;
        var totals = Model.Totals;
        if (Blueprint) DrawBlueprint(dc, tree, totals); else DrawTiles(dc, tree, totals);
    }

    private bool Selected(int id) => id == Model.SelectedNode || Model.MultiSelection.Contains(id);

    private Brush Ink(string key)
    {
        if (!_inks.TryGetValue(key, out var brush)) _inks[key] = brush = Rd.WithOpacity(Ui.Brush(key), 1);
        return brush;
    }

    /// <summary>The same text in the same face, size and colour is laid out once, not on every hover.</summary>
    private FormattedText Text(string text, Typeface face, double size, Brush brush)
    {
        var key = (text, face, size, brush);
        if (_texts.TryGetValue(key, out var formatted)) return formatted;
        if (_texts.Count > 4_000) _texts.Clear();   // a long session of zooming around: start over
        formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, _pixelsPerDip);
        _texts[key] = formatted;
        return formatted;
    }

    private Geometry ClipOf(Rect rect)
    {
        if (!_clips.TryGetValue(rect, out var clip)) _clips[rect] = clip = Frozen(new RectangleGeometry(rect));
        return clip;
    }

    private void DrawBlueprint(DrawingContext dc, FileTree tree, long[] totals)
    {
        foreach (var r in _rects)
        {
            var box = Inset(ToRect(r.Rect), 2);
            if (box.IsEmpty || box.Width <= 1 || box.Height <= 1) continue;
            bool free = _freeable.IsMostly(r.Id, tree, totals);
            if (free) DrawHatch(dc, box);
            else dc.DrawRectangle(r.Id == _hovered && !_children.ContainsKey(r.Id) ? _wash : _card, null, box);
            dc.DrawRectangle(null, free ? _accentPen : _outline, Half(box));
            Label(dc, tree.NameOf(r.Id), totals[r.Id], box, strong: true, free ? "RdAccentDeep" : "RdInk", onHatch: free);
            foreach (var c in _children.GetValueOrDefault(r.Id) ?? [])
            {
                var cell = Inset(ToRect(c.Rect), 2);
                if (cell.IsEmpty || cell.Width <= 1 || cell.Height <= 1) continue;
                bool cfree = free || _freeable.IsMostly(c.Id, tree, totals);
                if (cfree) DrawHatch(dc, cell);
                else dc.DrawRectangle(c.Id == _hovered ? _wash : _card, null, cell);
                dc.DrawRectangle(null, cfree ? _accentPen : _innerLine, Half(cell));
                if (cell.Width > 58 && cell.Height > 22)
                    Label(dc, tree.NameOf(c.Id), totals[c.Id], cell, strong: false, cfree ? "RdAccentDeep" : "RdInk", onHatch: cfree);
                if (Selected(c.Id)) dc.DrawRectangle(null, _selectPen, Inset(cell, 0.5) is { IsEmpty: false } s ? s : cell);
            }
            if (_more.TryGetValue(r.Id, out var more))
            {
                var cell = Inset(more.Rect, 2);
                if (!cell.IsEmpty && cell.Width > 1 && cell.Height > 1)
                {
                    dc.DrawRectangle(_card, null, cell);
                    dc.DrawRectangle(null, _innerLine, Half(cell));
                    if (cell.Width > 58 && cell.Height > 22)
                        Label(dc, "+ more", more.Bytes, cell, strong: false, "RdInk3");
                }
            }
            if (Selected(r.Id)) dc.DrawRectangle(null, _selectPen, Inset(box, 0.5) is { IsEmpty: false } s ? s : box);
        }
    }

    /// <summary>A 1 px stroke drawn on pixel centres, like the Mac's crisp hairlines.</summary>
    private static Rect Half(Rect r) => new(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));

    /// <summary>HatchFill: accent wash, then 1.5 pt violet stripes every 7 pt at 55%, clipped to the box.</summary>
    private void DrawHatch(DrawingContext dc, Rect box)
    {
        dc.DrawRectangle(_wash, null, box);
        if (!_hatches.TryGetValue(box, out var lines))
        {
            var stripes = new StreamGeometry();
            using (var g = stripes.Open())
            {
                for (double x = box.Left - box.Height; x < box.Right; x += 7)
                {
                    g.BeginFigure(new Point(x, box.Bottom), false, false);
                    g.LineTo(new Point(x + box.Height, box.Top), true, false);
                }
            }
            _hatches[box] = lines = Frozen(stripes);
        }
        dc.PushClip(ClipOf(box));
        dc.DrawGeometry(null, _stripe, lines);
        dc.Pop();
    }

    private static readonly Typeface NameStrong = new(Rd.FontFor(12), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface NameMedium = new(Rd.FontFor(12), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    private static readonly Typeface MonoFace = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>"Name  12.4 GB" in the top-left corner, cut to fit; a card pill under it on stripes.</summary>
    private void Label(DrawingContext dc, string name, long bytes, Rect rect, bool strong, string tint, bool onHatch = false)
    {
        if (rect.Width <= 40 || rect.Height <= 18) return;
        string size = ByteFormat.Format(bytes);
        double available = rect.Width - 14;
        bool showSize = available > (name.Length + size.Length) * 6.6 + 10;
        int maxChars = Math.Max(3, (int)(available / 6.9));
        string shown = name.Length > maxChars ? name[..(maxChars - 1)] + "…" : name;
        var nameText = Text(shown, strong ? NameStrong : NameMedium, Ui.Scaled(12), Ink(tint));
        FormattedText? sizeText = showSize
            ? Text("  " + size, MonoFace, Ui.Scaled(11), onHatch ? _sizeOnHatch : Ink("RdInk3"))
            : null;
        double width = nameText.WidthIncludingTrailingWhitespace + (sizeText?.WidthIncludingTrailingWhitespace ?? 0);
        double height = Math.Max(nameText.Height, sizeText?.Height ?? 0);
        if (onHatch)
        {
            var pill = new Rect(rect.X + 4, rect.Y + 3, Math.Min(available + 6, width + 6), height + 4);
            dc.DrawRoundedRectangle(_card, null, pill, 3, 3);
        }
        dc.PushClip(ClipOf(rect));
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
        foreach (var r in _rects)
        {
            var inset = Inset(ToRect(r.Rect), 1.5);
            if (inset.IsEmpty || inset.Width <= 0.5 || inset.Height <= 0.5) continue;
            double radius = Math.Min(6, Math.Min(inset.Width, inset.Height) / 3);
            bool hovered = r.Id == _hovered;
            dc.DrawRoundedRectangle(Fill(ColorFor(r.Id, tree)), null, inset, radius, radius);
            dc.DrawRoundedRectangle(hovered ? TileLightHover : TileLight, null, inset, radius, radius);
            if (Selected(r.Id)) dc.DrawRoundedRectangle(null, _tileSelectPen, inset, radius, radius);
            if (inset.Width > 52 && inset.Height > 22)
            {
                string name = tree.NameOf(r.Id);
                int keep = Math.Max(4, (int)(inset.Width / 7) - 3);
                var text = Text(name.Length > keep ? name[..keep] : name, NameMedium, Ui.Scaled(12), TileLabel);
                dc.PushClip(ClipOf(inset));
                dc.DrawText(text, new Point(inset.X + 8, inset.Y + 7));
                if (inset.Width > 80 && inset.Height > 46)
                    dc.DrawText(Text(ByteFormat.Format(totals[r.Id]), MonoFace, Ui.Scaled(11), TileSize), new Point(inset.X + 8, inset.Y + 25));
                dc.Pop();
            }
        }
    }

    /// <summary>One frozen brush per tile colour; the colourings use a few dozen at most.</summary>
    private static Brush Fill(Color color)
    {
        if (!TileFills.TryGetValue(color, out var brush)) TileFills[color] = brush = Frozen(new SolidColorBrush(color));
        return brush;
    }

    private static readonly string[] DataPalette = ["849BB8", "A795C7", "C78797", "7BA89C", "B9A071", "8FACC0", "A2A4AC"];

    private static Color Hex(string hex)
    {
        if (!HexColors.TryGetValue(hex, out var color))
            HexColors[hex] = color = (Color)ColorConverter.ConvertFromString("#" + hex.TrimStart('#'));
        return color;
    }

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

    /// <summary>
    /// The tile's menu: the one under the pointer for a right-click, the selected one from the
    /// keyboard (WPF passes -1 for the cursor then). Nothing under either: no menu.
    /// </summary>
    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        base.OnContextMenuOpening(e);
        bool fromKeyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        int? target = fromKeyboard
            ? (_cells.ContainsKey(Model.SelectedNode) ? Model.SelectedNode : null)
            : Hit(Mouse.GetPosition(this));
        if (target is not { } id || Model.Tree is not { } tree || ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }
        Model.Select(id);
        string path = Model.PathOf(id);
        menu.Items.Clear();
        void item(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        if (tree.IsDirectory[id]) item("Open", () => Model.DrillTo(id));
        item("Show in File Explorer", () => Explorer.Reveal(path));
        item("Add to basket", () => Model.Stage(id, "Explore: " + tree.NameOf(id)));
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
                if (Neighbor(tiles.Any(t => t.Id == selected) ? selected : null, e.Key, tiles) is { } next)
                {
                    Model.Select(next);
                    ChartPeer.Announce(this, next);
                }
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
