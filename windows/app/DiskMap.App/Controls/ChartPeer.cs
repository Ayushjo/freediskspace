using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;

namespace DiskMap.App.Controls;

/// <summary>
/// WIN-061: UI Automation peer shared by the charts and Explore's map — the chart announces
/// itself ("Sunburst — Home") and exposes its biggest cells as list items with their place on
/// screen and an Invoke that selects them.
/// </summary>
public sealed class ChartPeer : FrameworkElementAutomationPeer
{
    private readonly string _chartName;
    private readonly Func<IEnumerable<int>> _topIds;
    private readonly Func<int, Rect?> _bounds;

    /// <param name="bounds">A cell's rectangle in the chart's own coordinates, null when it isn't drawn.</param>
    public ChartPeer(FrameworkElement owner, string chartName, Func<IEnumerable<int>> topIds, Func<int, Rect?> bounds)
        : base(owner)
    {
        _chartName = chartName;
        _topIds = topIds;
        _bounds = bounds;
    }

    protected override string GetNameCore()
    {
        var model = ScanModel.Shared;
        var tree = model.Tree;
        return tree is null
            ? $"{_chartName} — no scan"
            : $"{_chartName} — {tree.NameOf(model.ZoomedNode)}";
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override List<AutomationPeer> GetChildrenCore()
    {
        var peers = new List<AutomationPeer>();
        var model = ScanModel.Shared;
        var tree = model.Tree;
        if (tree is null || model.ZoomedNode >= model.Totals.Length) return peers;
        foreach (int id in _topIds())
            if (ItemText(id) is { } text)
                peers.Add(new ChartItemPeer((FrameworkElement)Owner, id, text, _bounds));
        return peers;
    }

    /// <summary>"Photos, 12 GB, 31%, folder — press Enter to open": how a cell reads.</summary>
    public static string ItemName(string name, long bytes, long parentBytes, bool isDirectory)
    {
        string share = $"{100.0 * bytes / Math.Max(1, parentBytes):0.#}%";
        return $"{name}, {ByteFormat.Format(bytes)}, {share}" + (isDirectory ? ", folder — press Enter to open" : "");
    }

    /// <summary>The cell's words for the current scan and zoom, or null for an id the scan doesn't have.</summary>
    public static string? ItemText(int id)
    {
        var model = ScanModel.Shared;
        if (model.Tree is not { } tree || id < 0 || id >= tree.Count || id >= model.Totals.Length
            || model.ZoomedNode < 0 || model.ZoomedNode >= model.Totals.Length) return null;
        return ItemName(tree.NameOf(id), model.Totals[id], model.Totals[model.ZoomedNode], tree.IsDirectory[id]);
    }

    /// <summary>
    /// Says the newly selected cell after an arrow-key move: the chart keeps the keyboard, so
    /// without this a screen reader heard nothing as the selection moved.
    /// </summary>
    public static void Announce(UIElement chart, int id)
    {
        if (ItemText(id) is not { } text) return;
        UIElementAutomationPeer.FromElement(chart)?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent, text, "chart-selection");
    }
}

/// <summary>One accessible chart cell: its words, its place on screen, Invoke to select it.</summary>
public sealed class ChartItemPeer : AutomationPeer, IInvokeProvider
{
    private readonly FrameworkElement _chart;
    private readonly int _id;
    private readonly string _name;
    private readonly Func<int, Rect?> _bounds;

    public ChartItemPeer(FrameworkElement chart, int id, string name, Func<int, Rect?> bounds)
    {
        _chart = chart;
        _id = id;
        _name = name;
        _bounds = bounds;
    }

    /// <summary>Screen pixels, as UI Automation wants them; empty when the cell or the chart isn't shown.</summary>
    private Rect ScreenRect()
    {
        if (_bounds(_id) is not { IsEmpty: false } local || PresentationSource.FromVisual(_chart) is null) return Rect.Empty;
        return new Rect(_chart.PointToScreen(local.TopLeft), _chart.PointToScreen(local.BottomRight));
    }

    public void Invoke()
    {
        var model = ScanModel.Shared;
        model.ClearMulti();
        model.Select(_id);
    }

    protected override string GetNameCore() => _name;
    protected override string GetItemTypeCore() => "chart cell";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override string GetClassNameCore() => "ChartCell";
    protected override string GetAutomationIdCore() => $"node-{_id}";
    protected override string GetAcceleratorKeyCore() => "";
    protected override string GetAccessKeyCore() => "";
    protected override string GetHelpTextCore() => "";
    protected override string GetItemStatusCore() => ScanModel.Shared.SelectedNode == _id ? "Selected" : "";
    protected override AutomationPeer? GetLabeledByCore() => null;
    protected override bool IsKeyboardFocusableCore() => false;
    protected override bool IsOffscreenCore() => ScreenRect().IsEmpty;
    protected override bool IsPasswordCore() => false;
    protected override bool IsRequiredForFormCore() => false;
    protected override bool IsEnabledCore() => true;
    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
    protected override bool HasKeyboardFocusCore() => false;
    protected override Rect GetBoundingRectangleCore() => ScreenRect();

    protected override Point GetClickablePointCore()
    {
        var rect = ScreenRect();
        return rect.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    protected override void SetFocusCore() { }
    protected override AutomationPeer? GetPeerFromPointCore(Point point) => null;
    protected override List<AutomationPeer>? GetChildrenCore() => null;
    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke ? this : null;
}
