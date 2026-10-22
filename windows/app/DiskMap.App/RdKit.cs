using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DiskMap.App;

/// <summary>
/// WIN-103: the Mac v0.3.0 redesign kit (Sources/DiskMapApp/Kit/Redesign.swift) for the
/// code-built WPF pages. Same metrics as the Mac views: radii, heights, paddings, strokes
/// and colours come from there; only the fonts differ (Segoe UI Variable / Cascadia Mono).
/// Brushes are read at build time; a theme change rebuilds the pages.
/// </summary>
public static class Rd
{
    private static Brush B(string key) => Ui.Brush(key);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    // ---- Type (DiskMapType + Redesign.swift) ----

    /// <summary>Page titles ("Choose what to free"): 26 semibold.</summary>
    public static TextBlock PageTitle(string text) => Text(text, 26, FontWeights.SemiBold);

    /// <summary>Card titles ("Biggest places"): 14 semibold.</summary>
    public static TextBlock CardTitle(string text) => Text(text, 14, FontWeights.SemiBold);

    /// <summary>The hero figure on Overview ("120 GB"), sized by the caller.</summary>
    public static TextBlock Hero(string text, double size) => Text(text, size, FontWeights.SemiBold);

    /// <summary>Segoe UI Variable's display cut for big type (SF switches to Display at 20 pt).</summary>
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");

    public static FontFamily FontFor(double size) => size >= 20 ? DisplayFont : TextFont;

    /// <summary>
    /// SF's natural line height (ascender + descender ≈ 1.19 × size; SF Mono ≈ 1.21). Segoe's
    /// is ≈ 1.33, so unpinned text sat a little lower and every stack drifted down a few
    /// points against the Mac. Text in the kit uses the Mac's line height.
    /// </summary>
    public const double TextLineHeight = 1.19;
    public const double MonoLineHeight = 1.21;

    private static T Pinned<T>(T block, double size, double factor) where T : TextBlock
    {
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        block.LineHeight = Math.Ceiling(Ui.Scaled(size) * factor);
        return block;
    }

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null,
        string color = "RdInk", bool wrap = false) => Pinned(new TextBlock
    {
        Text = text,
        FontFamily = FontFor(size),
        FontSize = Ui.Scaled(size),
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = B(color),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    }, size, TextLineHeight);

    /// <summary>Mac `figure`: mono 12 (sizes, counts, dates).</summary>
    public static TextBlock Figure(string text, double size = 12, FontWeight? weight = null, string color = "RdInk") => Pinned(new TextBlock
    {
        Text = text,
        FontFamily = Mono,
        FontSize = Ui.Scaled(size),
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = B(color),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    }, size, MonoLineHeight);

    /// <summary>Mac `MonoLabel`: 10 medium mono, uppercase, ink3.</summary>
    public static TextBlock MonoLabel(string text, string color = "RdInk3") =>
        Figure(text.ToUpperInvariant(), 10, FontWeights.Medium, color);

    // ---- Surfaces ----

    /// <summary>BoardCard: card fill, 1 px line, radius 12, clipped.</summary>
    public static Border Card(UIElement child, double padding = 0)
    {
        var border = new Border
        {
            Background = B("RdCard"),
            BorderBrush = B("RdLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(padding),
            Child = child,
        };
        ClipToRoundedBounds(border, 12);
        return border;
    }

    /// <summary>BoardCardHeader: 44 tall, 16 side padding, title left, trailing right, bottom hairline.</summary>
    public static Border CardHeader(string title, UIElement? trailing = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(CardTitle(title));
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 1);
            if (trailing is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(trailing);
        }
        return new Border
        {
            Height = 44,
            Padding = new Thickness(16, 0, 16, 0),
            BorderBrush = B("RdLine"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    /// <summary>BoardBanner: a one-line notice, violet dot and a violet rule on the leading edge.</summary>
    public static Border Banner(string title, string detail, string? actionTitle = null, Action? action = null,
        Action? onDismiss = null)
    {
        var row = new DockPanel { LastChildFill = true };
        var dot = new Ellipse { Width = 7, Height = 7, Fill = B("AppAccent"), Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        if (onDismiss is not null)
        {
            var close = IconButton(Icons.Cancel, "Dismiss", onDismiss, 24);
            close.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(close, Dock.Right);
            row.Children.Add(close);
        }
        if (actionTitle is not null)
        {
            var button = SecondaryButton(actionTitle, action);
            button.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.Medium, Foreground = B("RdInk"), FontSize = Ui.Scaled(13) });
        text.Inlines.Add(new System.Windows.Documents.Run("  " + detail) { Foreground = B("RdInk2"), FontSize = Ui.Scaled(13) });
        row.Children.Add(text);
        var rule = new Border
        {
            Width = 3, HorizontalAlignment = HorizontalAlignment.Left,
            Background = B("AppAccent"), CornerRadius = new CornerRadius(10, 0, 0, 10),
        };
        var body = new Border { Padding = new Thickness(14, 0, 10, 0), MinHeight = 44, Child = row };
        row.VerticalAlignment = VerticalAlignment.Center;
        var stack = new Grid();
        stack.Children.Add(body);
        stack.Children.Add(rule);
        return new Border
        {
            Background = B("RdCard"),
            BorderBrush = B("RdLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
        };
    }

    /// <summary>IconTile: 34 square, radius 9, card fill, line stroke, 14 pt ink glyph.</summary>
    public static Border IconTile(string icon, double size = 34)
    {
        var glyph = Ui.Glyph(icon, 14, B("RdInk"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Width = size, Height = size,
            Background = B("RdCard"),
            BorderBrush = B("RdLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = glyph,
        };
    }

    /// <summary>The Mac's SF Symbol names, as Bootstrap icons.</summary>
    public static string IconForSymbol(string symbol) => symbol switch
    {
        "internaldrive" => "BsDeviceHdd",
        "hammer" => "BsHammer",
        "shippingbox" => "BsBox",
        "folder" => "BsFolder",
        "globe" or "safari" => "BsGlobe",
        "music.note" => "BsMusicNote",
        "chevron.left.forwardslash.chevron.right" => "BsCodeSlash",
        "bubble.left" or "bubble.left.and.bubble.right" => "BsChatLeft",
        "video" => "BsCameraVideo",
        "paintbrush.pointed" => "BsBrush",
        "paintpalette" => "BsPalette",
        "doc.text" => "BsFileText",
        "paperplane" => "BsSend",
        _ => "BsFolder",
    };

    public enum TagTone { Neutral, Accent, Ink }

    /// <summary>BoardTag: mono 10 medium uppercase, 18 tall, 6 side padding, radius 5.</summary>
    public static Border Tag(string text, TagTone tone = TagTone.Neutral)
    {
        string ink;
        Brush fill, stroke;
        switch (tone)
        {
            case TagTone.Accent: ink = "RdAccentDeep"; fill = B("RdAccentWash"); stroke = WithOpacity(B("AppAccent"), 0.35); break;
            case TagTone.Ink: ink = "RdOnInk"; fill = B("RdInk"); stroke = Brushes.Transparent; break;
            default: ink = "RdInk2"; fill = Brushes.Transparent; stroke = B("RdLine"); break;
        }
        return new Border
        {
            Height = 18,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(5),
            Background = fill,
            BorderBrush = stroke,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Figure(text.ToUpperInvariant(), 10, FontWeights.Medium, ink),
        };
    }

    // ---- Controls ----

    /// <summary>
    /// AccentButtonStyle: violet fill, white text, radius 8; 28 tall (40 when large);
    /// 12 (18) side padding; `hard` adds the boards' black 3,3 offset shadow and a black outline.
    /// </summary>
    public static Button AccentButton(string text, Action? onClick = null, bool large = false,
        bool fullWidth = false, bool hard = false, bool shadow = false)
    {
        var button = new Button
        {
            Content = Label(text, B("RdOnAccent"), large ? 14 : 13, large ? FontWeights.SemiBold : FontWeights.Medium),
            Height = large ? 40 : 28,
            Padding = new Thickness(large ? 18 : 12, 0, large ? 18 : 12, 0),
            FontSize = Ui.Scaled(large ? 14 : 13),
            FontWeight = large ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = B("RdOnAccent"),
            Background = B("AppAccent"),
            Cursor = Cursors.Hand,
            HorizontalAlignment = fullWidth ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            Template = AccentTemplate(hard, shadow || hard),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>PrimaryButtonStyle: ink fill, onInk 13 medium text, radius 6, 28 tall, 12 side padding.</summary>
    public static Button PrimaryButton(string text, Action? onClick = null, bool fullWidth = false) =>
        PlainButton(text, onClick, fullWidth, B("RdInk"), B("RdOnInk"), null, 6);

    /// <summary>SecondaryButtonStyle: raised @ 60% fill, 1 px line, ink 13 medium text, radius 6, 28 tall.</summary>
    public static Button SecondaryButton(string text, Action? onClick = null, bool fullWidth = false) =>
        PlainButton(text, onClick, fullWidth, WithOpacity(B("RdCard"), 0.6), B("RdInk"), B("RdLine"), 6);

    private static Button PlainButton(string text, Action? onClick, bool fullWidth, Brush fill, Brush ink,
        Brush? stroke, double radius)
    {
        var button = new Button
        {
            Content = Label(text, ink, 13, FontWeights.Medium),
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = Ui.Scaled(13),
            FontWeight = FontWeights.Medium,
            Foreground = ink,
            Background = fill,
            BorderBrush = stroke ?? Brushes.Transparent,
            BorderThickness = new Thickness(stroke is null ? 0 : 1),
            Cursor = Cursors.Hand,
            HorizontalAlignment = fullWidth ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            Template = PlainTemplate(radius),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// A button's label with its colour set on the TextBlock itself: the Fluent theme's implicit
    /// TextBlock style beats the template's inherited TextElement.Foreground, which left the
    /// Primary button's white label drawn in ink on ink (invisible).
    /// </summary>
    private static TextBlock Label(string text, Brush ink, double size, FontWeight weight) => new()
    {
        Text = text,
        Foreground = ink,
        FontSize = Ui.Scaled(size),
        FontWeight = weight,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static readonly Dictionary<double, ControlTemplate> _plainTemplates = [];

    private static ControlTemplate PlainTemplate(double radius)
    {
        if (_plainTemplates.TryGetValue(radius, out var cached)) return cached;
        // Pressed: 82% (Mac: ink @ 0.82 / ink @ 8% wash); disabled: 30%.
        string xaml = $@"
<ControlTemplate TargetType=""Button""
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Border x:Name=""Face"" CornerRadius=""{radius}"" Background=""{{TemplateBinding Background}}""
          BorderBrush=""{{TemplateBinding BorderBrush}}"" BorderThickness=""{{TemplateBinding BorderThickness}}""
          Padding=""{{TemplateBinding Padding}}"" SnapsToDevicePixels=""True"">
    <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""
                      TextElement.Foreground=""{{TemplateBinding Foreground}}""/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsPressed"" Value=""True"">
      <Setter TargetName=""Face"" Property=""Opacity"" Value=""0.82""/>
    </Trigger>
    <Trigger Property=""IsEnabled"" Value=""False"">
      <Setter TargetName=""Face"" Property=""Opacity"" Value=""0.3""/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";
        var template = (ControlTemplate)XamlReader.Parse(xaml);
        _plainTemplates[radius] = template;
        return template;
    }

    private static readonly Dictionary<(bool, bool), ControlTemplate> _accentTemplates = [];

    /// <summary>
    /// <paramref name="hard"/>: black outline, black 3,3 shadow and a 2,2 press nudge (the first
    /// launch's Scan button). <paramref name="withShadow"/> alone: just the black 3,3 block
    /// behind the button (Overview's Review button).
    /// </summary>
    private static ControlTemplate AccentTemplate(bool hard, bool withShadow)
    {
        if (_accentTemplates.TryGetValue((hard, withShadow), out var cached)) return cached;
        // Pressed: 85% fill (and a 2,2 nudge for the hard style); disabled: 40% fill.
        string shadow = withShadow
            ? "<Border CornerRadius=\"8\" Background=\"Black\" Margin=\"3,3,-3,-3\"/>"
            : "";
        string outline = hard ? "BorderBrush=\"Black\" BorderThickness=\"1\"" : "";
        string pressedNudge = hard
            ? "<Setter TargetName=\"Face\" Property=\"RenderTransform\"><Setter.Value><TranslateTransform X=\"2\" Y=\"2\"/></Setter.Value></Setter>"
            : "";
        string xaml = $@"
<ControlTemplate TargetType=""Button""
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <Grid>
    {shadow}
    <Border x:Name=""Face"" CornerRadius=""8"" Background=""{{TemplateBinding Background}}"" {outline}
            Padding=""{{TemplateBinding Padding}}"">
      <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""
                        TextElement.Foreground=""{{TemplateBinding Foreground}}""/>
    </Border>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property=""IsPressed"" Value=""True"">
      <Setter TargetName=""Face"" Property=""Opacity"" Value=""0.85""/>
      {pressedNudge}
    </Trigger>
    <Trigger Property=""IsEnabled"" Value=""False"">
      <Setter TargetName=""Face"" Property=""Opacity"" Value=""0.4""/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";
        var template = (ControlTemplate)XamlReader.Parse(xaml);
        _accentTemplates[(hard, withShadow)] = template;
        return template;
    }

    public sealed record Segment(string Title, bool IsOn, Action OnClick);

    /// <summary>
    /// SegmentSwitch: soft well with a 1 px line, radius 8, 2 px inset; items 24 tall,
    /// 11 side padding, radius 6, the selected one on segmentOn with a faint shadow.
    /// </summary>
    public static Border SegmentSwitch(IEnumerable<Segment> segments)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        bool first = true;
        foreach (var segment in segments)
        {
            var label = Text(segment.Title, 13, FontWeights.Medium, segment.IsOn ? "RdInk" : "RdInk2");
            var item = new Border
            {
                Height = 24,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(6),
                Background = segment.IsOn ? B("RdSegmentOn") : Brushes.Transparent,
                Margin = new Thickness(first ? 0 : 2, 0, 0, 0),
                Cursor = Cursors.Hand,
                Child = label,
                Effect = segment.IsOn
                    ? new System.Windows.Media.Effects.DropShadowEffect
                    { Color = Colors.Black, Opacity = 0.06, BlurRadius = 2, ShadowDepth = 1, Direction = 270 }
                    : null,
            };
            var action = segment.OnClick;
            item.MouseLeftButtonUp += (_, _) => action();
            System.Windows.Automation.AutomationProperties.SetName(item, segment.Title);
            row.Children.Add(item);
            first = false;
        }
        return new Border
        {
            Background = B("RdSoft"),
            BorderBrush = B("RdLine"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = row,
        };
    }

    /// <summary>LeaderRow: "label ········ value" on a shared baseline (the receipt rows).</summary>
    public static Grid LeaderRow(string label, string value, bool emphasis = false,
        bool accentValue = false, bool faint = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = Text(label, emphasis ? 16 : 13, emphasis ? FontWeights.SemiBold : null, faint ? "RdInk3" : "RdInk");
        left.VerticalAlignment = VerticalAlignment.Bottom;
        var right = Figure(value, emphasis ? 18 : 12, emphasis ? FontWeights.Medium : null,
            accentValue ? "AppAccent" : faint ? "RdInk3" : "RdInk");
        right.VerticalAlignment = VerticalAlignment.Bottom;
        var leader = new Line
        {
            X1 = 0, X2 = 1, Stretch = Stretch.Fill, Height = 1,
            Stroke = B("RdInk3"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 1, 3 },
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 8, 4), Opacity = 0.7,
        };
        Grid.SetColumn(leader, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(leader);
        grid.Children.Add(right);
        return grid;
    }

    // ---- Fills ----

    /// <summary>Hatch: violet diagonal stripes, 3 wide every 7, on accentWash. The "can be freed" mark.</summary>
    public static Brush Hatch(string stripe = "AppAccent", string ground = "RdAccentWash")
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(B(ground), null, new RectangleGeometry(new Rect(0, 0, 7, 7))));
        var pen = new Pen(B(stripe), 3);
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(-7, 7), new Point(0, 0)));
        lines.Children.Add(new LineGeometry(new Point(0, 7), new Point(7, 0)));
        lines.Children.Add(new LineGeometry(new Point(7, 7), new Point(14, 0)));
        group.Children.Add(new GeometryDrawing(null, pen, lines));
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 7, 7));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 7, 7),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 7, 7),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Hatch with any colours, spacing and stroke (Mac Hatch(stripe:ground:spacing:width:)).</summary>
    public static Brush HatchBrush(Brush stripe, Brush ground, double spacing, double width)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(ground, null, new RectangleGeometry(new Rect(0, 0, spacing, spacing))));
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(-spacing, spacing), new Point(0, 0)));
        lines.Children.Add(new LineGeometry(new Point(0, spacing), new Point(spacing, 0)));
        lines.Children.Add(new LineGeometry(new Point(spacing, spacing), new Point(spacing * 2, 0)));
        group.Children.Add(new GeometryDrawing(null, new Pen(stripe, width), lines));
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, spacing, spacing));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, spacing, spacing),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, spacing, spacing),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>FreeStripes: 2 px vertical line stripes every 6 on the card colour (free space in a bar).</summary>
    public static Brush FreeStripes()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(B("RdCard"), null, new RectangleGeometry(new Rect(0, 0, 6, 1))));
        group.Children.Add(new GeometryDrawing(WithOpacity(B("RdLine"), 0.9), null, new RectangleGeometry(new Rect(0, 0, 2, 1))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 6, 1),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>BoardGrid: a faint 24 pt grid (ink @ 4.5%) behind some surfaces.</summary>
    public static Brush Grid24(Brush? tint = null, double step = 24)
    {
        var pen = new Pen(tint ?? WithOpacity(B("RdInk"), 0.045), 1);
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0.5, 0), new Point(0.5, step)));
        lines.Children.Add(new LineGeometry(new Point(0, 0.5), new Point(step, 0.5)));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, step, step))));
        group.Children.Add(new GeometryDrawing(null, pen, lines));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, step, step),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    // ---- Layout ----

    public enum WidthClass { Compact, Regular, Wide }

    /// <summary>The boards' breakpoints by window width: 13" Air, 14" Pro, a large display.</summary>
    public static WidthClass ClassFor(double windowWidth) =>
        windowWidth < 1_400 ? WidthClass.Compact : windowWidth < 1_700 ? WidthClass.Regular : WidthClass.Wide;

    public static double PagePadding(this WidthClass c) => c switch { WidthClass.Compact => 18, WidthClass.Regular => 24, _ => 28 };
    public static double Gap(this WidthClass c) => c switch { WidthClass.Compact => 12, WidthClass.Regular => 16, _ => 18 };

    /// <summary>
    /// tightText: the hero figure with tracking (negative kerning on every character but the
    /// last) and tabular digits, as one row of single-character runs.
    /// </summary>
    public static StackPanel TightText(string text, double size, double kerning, string color = "RdInk",
        FontWeight? weight = null, Brush? brush = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < text.Length; i++)
        {
            var glyph = new TextBlock
            {
                Text = text[i].ToString(),
                FontFamily = FontFor(size),
                FontSize = Ui.Scaled(size),
                FontWeight = weight ?? FontWeights.SemiBold,
                Foreground = brush ?? B(color),
                Margin = new Thickness(0, 0, i < text.Length - 1 ? Ui.Scaled(kerning) : 0, 0),
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(glyph, FontNumeralAlignment.Tabular);
            Pinned(glyph, size, TextLineHeight);
            row.Children.Add(glyph);
        }
        System.Windows.Automation.AutomationProperties.SetName(row, text);
        return row;
    }

    /// <summary>
    /// Two runs on one baseline (Mac HStack(alignment: .lastTextBaseline)): "120 GB" then
    /// "of 500 GB". Bottom-aligned, the smaller one lifted by the difference in descent.
    /// </summary>
    public static StackPanel BaselineRow(FrameworkElement big, double bigSize, FrameworkElement small, double smallSize,
        double gap)
    {
        // With a pinned line height WPF keeps the baseline at the font's proportion of the line.
        double descent(double size) =>
            Math.Ceiling(Ui.Scaled(size) * TextLineHeight) * (1 - FontFor(size).Baseline / FontFor(size).LineSpacing);
        big.VerticalAlignment = VerticalAlignment.Bottom;
        small.VerticalAlignment = VerticalAlignment.Bottom;
        small.Margin = new Thickness(gap, 0, 0, Math.Max(0, descent(bigSize) - descent(smallSize)));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(big);
        row.Children.Add(small);
        return row;
    }

    /// <summary>LinkButtonStyle: accent 13 medium text, 70% while pressed, hand cursor.</summary>
    public static TextBlock Link(string text, Action onClick, double size = 13, string color = "AppAccent",
        FontWeight? weight = null)
    {
        var link = Text(text, size, weight ?? FontWeights.Medium, color);
        link.Cursor = Cursors.Hand;
        link.Focusable = true;
        link.MouseLeftButtonDown += (_, e) => { link.Opacity = 0.7; e.Handled = true; };
        link.MouseLeave += (_, _) => link.Opacity = 1;
        link.MouseLeftButtonUp += (_, e) =>
        {
            if (link.Opacity < 1) { link.Opacity = 1; onClick(); }
            e.Handled = true;
        };
        link.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { onClick(); e.Handled = true; } };
        System.Windows.Automation.AutomationProperties.SetName(link, text);
        return link;
    }

    /// <summary>IconButtonStyle: a square glyph button with a hover fill.</summary>
    public static Border IconButton(string icon, string label, Action onClick, double size = 28)
    {
        var glyph = Ui.Glyph(icon, 12, B("RdInk2"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent, Child = glyph, Cursor = Cursors.Hand, ToolTip = label,
        };
        button.MouseEnter += (_, _) => button.Background = B("RdHover");
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) => { onClick(); e.Handled = true; };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>A rounded surface with a fill and an optional hairline, clipped to its corners.</summary>
    public static Border Surface(UIElement child, string fill, double radius, string? stroke = "RdLine")
    {
        var border = new Border
        {
            Background = B(fill),
            BorderBrush = stroke is null ? null : B(stroke),
            BorderThickness = new Thickness(stroke is null ? 0 : 1),
            CornerRadius = new CornerRadius(radius),
            Child = child,
        };
        ClipToRoundedBounds(border, radius);
        return border;
    }

    // ---- Helpers ----

    /// <summary>RelativeTime: the page bar's "2 minutes ago", same thresholds and rounding as the Mac.</summary>
    public static string RelativeTime(DateTimeOffset since, DateTimeOffset now) =>
        DiskMap.Core.ShellFormat.RelativeTime(since, now);

    /// <summary>A nav figure's compact size, "31.6G" / "640M" (AppShellView.compactBytes).</summary>
    public static string CompactBytes(long bytes) => DiskMap.Core.ShellFormat.CompactBytes(bytes);

    public static Brush WithOpacity(Brush brush, double opacity)
    {
        var clone = brush.CloneCurrentValue();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }

    private static void ClipToRoundedBounds(Border border, double radius)
    {
        border.SizeChanged += (_, e) =>
            border.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
    }
}
