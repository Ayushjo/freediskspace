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

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null,
        string color = "RdInk", bool wrap = false) => new()
    {
        Text = text,
        FontSize = Ui.Scaled(size),
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = B(color),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Mac `figure`: mono 12 (sizes, counts, dates).</summary>
    public static TextBlock Figure(string text, double size = 12, FontWeight? weight = null, string color = "RdInk") => new()
    {
        Text = text,
        FontFamily = Mono,
        FontSize = Ui.Scaled(size),
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = B(color),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

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
    public static Border Banner(string title, string detail, string? actionTitle = null, Action? action = null)
    {
        var row = new DockPanel { LastChildFill = true };
        var dot = new Ellipse { Width = 7, Height = 7, Fill = B("AppAccent"), Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        if (actionTitle is not null)
        {
            var button = Ui.Button(actionTitle, null, Ui.ButtonStyle.Outline, action);
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
        bool fullWidth = false, bool hard = false)
    {
        var button = new Button
        {
            Content = text,
            Height = large ? 40 : 28,
            Padding = new Thickness(large ? 18 : 12, 0, large ? 18 : 12, 0),
            FontSize = Ui.Scaled(large ? 14 : 13),
            FontWeight = large ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = B("RdOnAccent"),
            Background = B("AppAccent"),
            Cursor = Cursors.Hand,
            HorizontalAlignment = fullWidth ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            Template = AccentTemplate(hard),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    private static ControlTemplate? _accentSoft, _accentHard;

    private static ControlTemplate AccentTemplate(bool hard)
    {
        if (hard && _accentHard is not null) return _accentHard;
        if (!hard && _accentSoft is not null) return _accentSoft;
        // Pressed: 85% fill (and a 2,2 nudge for the hard style); disabled: 40% fill.
        string shadow = hard
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
        if (hard) _accentHard = template; else _accentSoft = template;
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
    public static Brush Grid24()
    {
        var pen = new Pen(WithOpacity(B("RdInk"), 0.045), 1);
        var lines = new GeometryGroup();
        lines.Children.Add(new LineGeometry(new Point(0.5, 0), new Point(0.5, 24)));
        lines.Children.Add(new LineGeometry(new Point(0, 0.5), new Point(24, 0.5)));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        group.Children.Add(new GeometryDrawing(null, pen, lines));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 24, 24),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    // ---- Helpers ----

    /// <summary>RelativeTime: the page bar's "2 minutes ago", same thresholds as the Mac.</summary>
    public static string RelativeTime(DateTimeOffset since, DateTimeOffset now)
    {
        double s = Math.Max(0, (now - since).TotalSeconds);
        if (s < 45) return "just now";
        if (s < 90) return "a minute ago";
        if (s < 3_600) return $"{(int)Math.Round(s / 60)} minutes ago";
        if (s < 5_400) return "an hour ago";
        if (s < 86_400) return $"{(int)Math.Round(s / 3_600)} hours ago";
        int days = (int)Math.Round(s / 86_400);
        return days == 1 ? "yesterday" : $"{days} days ago";
    }

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
