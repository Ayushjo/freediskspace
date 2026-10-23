using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using DiskMap.Core;

namespace DiskMap.App.Pages;

/// <summary>
/// WIN-109: Duplicates (Mac: DuplicatesView.swift, redesign board 07). Groups of
/// byte-identical files with one copy kept and the others ticked, a choice of which copy
/// stays, and the hard links and ReFS block clones that only look like duplicates set
/// aside. The search starts from the page and runs in the background (DuplicateSearch);
/// the ticked copies are staged into the basket, never deleted here.
/// </summary>
public sealed class DuplicatesBoardPage : UserControl
{
    private static ScanModel Model => ScanModel.Shared;

    private readonly Grid _board = new();
    private readonly Grid _main = new();
    private readonly Border _headerHost = new();
    private readonly Border _bannerHost = new();
    private readonly Border _filterHost = new();
    private readonly Border _listHost = new();
    private readonly ItemsControl _list;
    private readonly Grid _summary = new();
    private readonly HashSet<CardHost> _liveCards = [];
    private readonly Dictionary<int, FrameworkElement> _rows = [];
    private readonly Dictionary<string, int> _keeperOverride = [];
    private readonly Dictionary<DuplicateGroup, int?> _keeperCache = new(ReferenceEqualityComparer.Instance);
    private HashSet<int> _checked = [];
    private DuplicateKeepPolicy _policy = DuplicateReview.ParsePolicy(AppSettings.Load().DuplicatesKeep);
    private DuplicateKind _kindFilter = DuplicateKind.All;
    private object? _tickedFor;
    private List<DuplicateGroup> _shown = [];
    private Rd.WidthClass _class = Rd.WidthClass.Regular;
    private bool _isStaging;
    private bool _harnessStarted;
    /// <summary>Bumped whenever what a card shows may have changed; a card rebuilds when it is behind.</summary>
    private int _version;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public DuplicatesBoardPage()
    {
        Background = Ui.Brush("RdPage");
        Focusable = true;
        FocusVisualStyle = null;

        _list = BuildList();
        _board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        _board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        _board.Children.Add(_main);
        var line = new Border { Background = Ui.Brush("RdLine") };
        Grid.SetColumn(line, 1);
        _board.Children.Add(line);
        _summary.Background = Ui.Brush("RdSidebar");
        _summary.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(_summary, 2);
        _board.Children.Add(_summary);
        for (int i = 0; i < 3; i++) _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_bannerHost, 1);
        Grid.SetRow(_filterHost, 2);
        Grid.SetRow(_listHost, 3);
        _main.Children.Add(_headerHost);
        _main.Children.Add(_bannerHost);
        _main.Children.Add(_filterHost);
        _main.Children.Add(_listHost);

        ModelEvents.WhileLoaded(this, Rebuild);
        Loaded += (_, _) =>
        {
            Model.StagedChanged += OnStaged;
            DuplicateSearch.Changed += OnSearchChanged;
        };
        Unloaded += (_, _) =>
        {
            Model.StagedChanged -= OnStaged;
            DuplicateSearch.Changed -= OnSearchChanged;
            _clock.Stop();
        };
        _clock.Tick += (_, _) => { if (DuplicateSearch.IsRunning) Rebuild(); else _clock.Stop(); };
        SizeChanged += (_, _) =>
        {
            if (Rd.ClassFor(WindowWidth) != _class) Rebuild();
        };
        KeyDown += OnKey;
    }

    private void OnStaged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        _keeperCache.Clear();   // a kept copy that went into the basket can't stay the kept one
        // ...and the copy that stays instead is the kept one now, so it isn't ticked.
        if (Model.Tree is { } tree)
            foreach (var group in AllGroups)
                if (Keeper(tree, group) is { } keep) _checked.Remove(keep);
        Rebuild();
    });

    private static bool IsStaged(int id) => Model.IsStaged(Model.PathOf(id));
    private void OnSearchChanged() => Dispatcher.InvokeAsync(Rebuild);

    private double WindowWidth =>
        Harness.ContentSize is { } pinned ? pinned.Width
        : Window.GetWindow(this)?.Content is FrameworkElement root && root.ActualWidth > 0 ? root.ActualWidth
        : 1280;

    // ---- Groups and figures ----

    private static IReadOnlyList<DuplicateGroup> AllGroups => Model.Duplicates;

    /// <summary>Groups that free something; whole-group clones are listed in the banner instead.</summary>
    private static List<DuplicateGroup> RealGroups => AllGroups.Where(g => !g.SharesStorage).ToList();
    private static List<DuplicateGroup> CloneGroups => AllGroups.Where(g => g.SharesStorage).ToList();

    private static long Allocated(FileTree tree, int id) => id >= 0 && id < tree.Count ? tree.AllocatedSize[id] : 0;

    private static string NodeName(FileTree tree, int id) => id >= 0 && id < tree.Count ? tree.NameOf(id) : "";

    private static int? FirstId(DuplicateGroup g) => g.FileIDs.Count > 0 ? g.FileIDs[0] : null;

    /// <summary>On-disk size of one member, the basis every reclaim figure uses.</summary>
    private static long OnDisk(FileTree tree, DuplicateGroup g) =>
        g.FileIDs.Count > 0 ? g.FileIDs.Max(id => Allocated(tree, id)) : g.SizeEach;

    private static long Reclaim(FileTree tree, DuplicateGroup g, IReadOnlySet<int> deleting) =>
        g.ReclaimableBytes(deleting, id => Allocated(tree, id));

    /// <summary>The copy that stays: the person's choice, else the policy's. Cached per group (paths cost a walk).</summary>
    private int? Keeper(FileTree tree, DuplicateGroup g)
    {
        if (_keeperCache.TryGetValue(g, out int? cached)) return cached;
        // Only a copy that isn't in the basket can stay (while there is one).
        var free = g.FileIDs.Where(id => !IsStaged(id)).ToList();
        var pool = free.Count > 0 && free.Count < g.FileIDs.Count ? g with { FileIDs = free } : g;
        int? keep = _keeperOverride.TryGetValue(DuplicateReview.GroupKey(g), out int chosen) && pool.FileIDs.Contains(chosen)
            ? chosen
            : DuplicateReview.Keeper(pool, _policy, id => tree.ModifiedDay[id], id => Model.PathOf(id).Length);
        _keeperCache[g] = keep;
        return keep;
    }

    private HashSet<int> Extras(FileTree tree, DuplicateGroup g)
    {
        int? keep = Keeper(tree, g);
        return g.FileIDs.Where(id => id != keep).ToHashSet();
    }

    /// <summary>What removing every copy but the kept one would free.</summary>
    private long ExtraCopiesBytes(FileTree tree) => RealGroups.Sum(g => Reclaim(tree, g, Extras(tree, g)));

    private long Reclaimable(FileTree tree) => AllGroups.Sum(g => Reclaim(tree, g, _checked));

    private List<DuplicateGroup> ShownGroups(FileTree tree)
    {
        var groups = _kindFilter == DuplicateKind.All
            ? RealGroups
            : RealGroups.Where(g => DuplicateReview.KindOf(NodeName(tree, FirstId(g) ?? 0)) == _kindFilter).ToList();
        return groups
            .Select(g => (Group: g, Bytes: Reclaim(tree, g, Extras(tree, g))))
            .OrderByDescending(x => x.Bytes)
            .Select(x => x.Group)
            .ToList();
    }

    /// <summary>Extra copies start ticked; the kept copy of each stays (Mac: selectOtherCopies).</summary>
    private void SelectOtherCopies()
    {
        if (Model.Tree is not { } tree) { _checked = []; return; }
        _checked = DuplicateReview.PreTicked(AllGroups, g => Keeper(tree, g), Model.PathOf, Model.IsStaged);
    }

    // ---- Build ----

    private void Rebuild()
    {
        _class = Rd.ClassFor(WindowWidth);
        if (Model.Tree is not { } tree)
        {
            Model.ShowPage("Overview");   // the first launch lives on Overview
            return;
        }
        DuplicateSearch.SyncWith(tree);
        if (Harness.FindDuplicates && !_harnessStarted && !Model.DuplicatesSearched && !DuplicateSearch.IsRunning)
        {
            _harnessStarted = true;
            DuplicateSearch.Start();
        }
        // New groups (a search, a rescan) re-tick from the defaults and forget the swaps.
        if (!ReferenceEquals(_tickedFor, Model.Duplicates))
        {
            _tickedFor = Model.Duplicates;
            _kindFilter = DuplicateKind.All;   // the switch may not even show for the new groups
            _keeperOverride.Clear();
            _keeperCache.Clear();
            SelectOtherCopies();
        }
        if (DuplicateSearch.IsRunning) { if (!_clock.IsEnabled) _clock.Start(); }
        else _clock.Stop();

        bool hasGroups = AllGroups.Count > 0 && !DuplicateSearch.IsRunning && DuplicateSearch.Error is null;
        if (hasGroups)
        {
            BuildBoard(tree);
            if (!ReferenceEquals(Content, _board)) Content = _board;
        }
        else
        {
            _shown = [];
            _list.ItemsSource = null;
            Content = EmptyOrWorking();
        }
    }

    private void BuildBoard(FileTree tree)
    {
        double pad = _class.PagePadding(), gap = _class.Gap();
        _board.ColumnDefinitions[2].Width = new GridLength(_class switch
        {
            Rd.WidthClass.Compact => 290, Rd.WidthClass.Regular => 320, _ => 360,
        });
        _main.Margin = new Thickness(pad, _class == Rd.WidthClass.Compact ? 14 : 20, pad, 0);
        long extra = ExtraCopiesBytes(tree);

        _headerHost.Child = Header(extra);
        bool clones = CloneGroups.Count > 0;
        _bannerHost.Child = clones ? CloneBanner(tree, extra) : null;
        _bannerHost.Margin = new Thickness(0, clones ? gap : 0, 0, 0);
        _filterHost.Child = FilterRow(tree);
        _filterHost.Margin = new Thickness(0, gap, 0, 0);
        _listHost.Margin = new Thickness(0, gap, 0, 0);
        _version++;

        var shown = ShownGroups(tree);
        if (shown.Count == 0)
        {
            _shown = [];
            _list.ItemsSource = null;
            var none = Rd.Text(RealGroups.Count == 0
                    ? "Every duplicate here is a hard link or ReFS block clone, so there is nothing to free."
                    : $"No duplicate {_kindFilter.Title().ToLowerInvariant()} here.", 13, null, "RdInk3", wrap: true);
            none.HorizontalAlignment = HorizontalAlignment.Center;
            none.VerticalAlignment = VerticalAlignment.Top;
            none.TextAlignment = TextAlignment.Center;
            none.Margin = new Thickness(0, 30, 0, 0);
            _listHost.Child = none;
        }
        else
        {
            _listHost.Child = _list;
            if (!shown.SequenceEqual(_shown, ReferenceEqualityComparer.Instance))
            {
                _shown = shown;
                _list.ItemsSource = shown;
            }
            else
            {
                _shown = shown;
                RefreshCards();
            }
        }
        BuildSummary(tree);
    }

    private FrameworkElement Header(long extra)
    {
        var dock = new DockPanel();
        var again = Rd.SecondaryButton("Search again", DuplicateSearch.Start);
        again.VerticalAlignment = VerticalAlignment.Top;
        again.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(again, Dock.Right);
        dock.Children.Add(again);

        var titles = new StackPanel();
        var title = Rd.Text("", 26, FontWeights.SemiBold);
        title.Inlines.Add(new Run("Duplicates") { Foreground = Ui.Brush("RdInk") });
        title.Inlines.Add(new Run(" · ") { Foreground = Ui.Brush("RdInk3") });
        if (extra > 0)
        {
            title.Inlines.Add(new Run(ShellFormat.HeroBytes(extra)) { Foreground = Ui.Brush("AppAccent") });
            title.Inlines.Add(new Run(" you can free") { Foreground = Ui.Brush("RdInk") });
        }
        else
        {
            title.Inlines.Add(new Run("nothing to free") { Foreground = Ui.Brush("RdInk3") });
        }
        title.TextTrimming = TextTrimming.None;
        System.Windows.Automation.AutomationProperties.SetName(title,
            extra > 0 ? $"Duplicates · {ShellFormat.HeroBytes(extra)} you can free" : "Duplicates · nothing to free");
        // lineLimit(1) + minimumScaleFactor(0.8): shrink to fit, never below 80%.
        var fit = new Viewbox
        {
            Child = title, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        title.SizeChanged += (_, e) => fit.MinWidth = e.NewSize.Width * 0.8;
        titles.ClipToBounds = true;
        titles.Children.Add(fit);
        var sub = Rd.Text($"Files with exactly the same contents. We keep the {_policy.KeepPhrase()} and tick the rest, but never files inside projects or apps.",
            13, null, "RdInk2");
        sub.Margin = new Thickness(0, 6, 0, 0);
        sub.ToolTip = sub.Text;
        titles.Children.Add(sub);
        dock.Children.Add(titles);
        return dock;
    }

    private FrameworkElement CloneBanner(FileTree tree, long extra)
    {
        var clones = CloneGroups;
        int count = clones.Count;
        var row = new DockPanel();
        var left = new StackPanel { MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        left.Children.Add(Rd.Text($"{count} {(count == 1 ? "group looks" : "groups look")} like duplicates but {(count == 1 ? "shares" : "share")} storage.",
            13, FontWeights.Medium, "RdInk", wrap: true));
        var why = Rd.Text("They are hard links or ReFS block clones, so removing one frees nothing. "
            + (extra > 0 ? $"We left them out of the {ShellFormat.HeroBytes(extra)}." : ""), 13, null, "RdInk2", wrap: true);
        why.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(why);
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);
        if (_class != Rd.WidthClass.Compact)
        {
            var list = new StackPanel { Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            foreach (var group in clones.Take(3))
            {
                var line = new DockPanel { Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 6, 0, 0) };
                var icon = Ui.Glyph(Icons.Copy, 11, Ui.Brush("RdInk3"));
                icon.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(icon, Dock.Left);
                line.Children.Add(icon);
                var figure = Rd.Figure($"{group.FileIDs.Count} × {ShellFormat.HeroBytes(OnDisk(tree, group))} · 0 freed", 11, null, "RdInk3");
                figure.Margin = new Thickness(8, 0, 0, 0);
                DockPanel.SetDock(figure, Dock.Right);
                line.Children.Add(figure);
                line.Children.Add(Rd.Text(NodeName(tree, FirstId(group) ?? 0), 13, null, "RdInk2"));
                list.Children.Add(line);
            }
            if (count > 3)
            {
                var more = Rd.Text($"and {count - 3} more", 12, null, "RdInk3");
                more.Margin = new Thickness(0, 6, 0, 0);
                list.Children.Add(more);
            }
            row.Children.Add(list);
        }
        var body = new Border { Padding = new Thickness(20, 16, 20, 16), Child = row };
        var rule = new Border
        {
            Width = 3, HorizontalAlignment = HorizontalAlignment.Left,
            Background = Ui.Brush("RdInk3"), CornerRadius = new CornerRadius(12, 0, 0, 12),
        };
        var layers = new Grid();
        layers.Children.Add(body);
        layers.Children.Add(rule);
        var card = Rd.Card(layers);
        System.Windows.Automation.AutomationProperties.SetName(card,
            $"{count} {(count == 1 ? "group shares" : "groups share")} storage; removing one frees nothing");
        return card;
    }

    private FrameworkElement FilterRow(FileTree tree)
    {
        var present = RealGroups.Select(g => DuplicateReview.KindOf(NodeName(tree, FirstId(g) ?? 0))).ToHashSet();
        var kinds = Enum.GetValues<DuplicateKind>().Where(k => k == DuplicateKind.All || present.Contains(k)).ToList();
        var dock = new DockPanel { LastChildFill = false };
        var policies = Rd.SegmentSwitch(Enum.GetValues<DuplicateKeepPolicy>().Select(p =>
            new Rd.Segment(p.Title(), _policy == p, () => SetPolicy(p))));
        System.Windows.Automation.AutomationProperties.SetName(policies, $"Keep: {_policy.Title()}");
        DockPanel.SetDock(policies, Dock.Right);
        dock.Children.Add(policies);
        var keep = Rd.Text("Keep", 12, null, "RdInk3");
        keep.Margin = new Thickness(8, 0, 12, 0);
        DockPanel.SetDock(keep, Dock.Right);
        dock.Children.Add(keep);
        if (kinds.Count > 2)
        {
            var switcher = Rd.SegmentSwitch(kinds.Select(k =>
                new Rd.Segment(k.Title(), _kindFilter == k, () => { _kindFilter = k; Rebuild(); })));
            System.Windows.Automation.AutomationProperties.SetName(switcher, $"Show: {_kindFilter.Title()}");
            DockPanel.SetDock(switcher, Dock.Left);
            dock.Children.Add(switcher);
        }
        return dock;
    }

    private void SetPolicy(DuplicateKeepPolicy policy)
    {
        if (policy == _policy) return;
        _policy = policy;
        var settings = AppSettings.Load();
        settings.DuplicatesKeep = policy.Key();
        settings.Save();
        _keeperOverride.Clear();
        _keeperCache.Clear();
        SelectOtherCopies();
        Rebuild();
    }

    // ---- The list (virtualized: a home folder can hold thousands of groups) ----

    private ItemsControl BuildList()
    {
        var factory = new FrameworkElementFactory(typeof(CardHost));
        var list = new ItemsControl
        {
            ItemTemplate = new DataTemplate { VisualTree = factory },
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            Template = ListTemplate(),
            Focusable = false,
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(list, true);
        System.Windows.Automation.AutomationProperties.SetName(list, "Duplicate groups");
        return list;
    }

    private static ControlTemplate ListTemplate() => (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate TargetType=""ItemsControl""
    xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
  <ScrollViewer Focusable=""False"" CanContentScroll=""True""
                VerticalScrollBarVisibility=""Auto"" HorizontalScrollBarVisibility=""Disabled"">
    <ItemsPresenter/>
  </ScrollViewer>
</ControlTemplate>");

    private void RefreshCards()
    {
        foreach (var host in _liveCards.ToList()) host.Refresh();
    }

    /// <summary>One group card; the list's item template builds it from the page's state.</summary>
    internal sealed class CardHost : ContentControl
    {
        // The page that owns this card, found up the visual tree: a template factory can't hold
        // the page itself (it's a Visual — "not a valid value for FrameworkElementFactory").
        private DuplicatesBoardPage? _page;

        public DuplicatesBoardPage? Page => _page ??= FindPage(this);

        private static DuplicatesBoardPage? FindPage(DependencyObject from)
        {
            for (var node = VisualTreeHelper.GetParent(from); node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is DuplicatesBoardPage page) return page;
            return null;
        }

        private object? _builtFor;
        private int _builtVersion = -1;

        public CardHost()
        {
            Focusable = false;
            DataContextChanged += (_, _) => Refresh();
            Loaded += (_, _) => { Page?._liveCards.Add(this); Refresh(); };
            Unloaded += (_, _) => { Page?._liveCards.Remove(this); _page = null; };
        }

        /// <summary>Builds the card for its group unless it already shows this group at the page's version.</summary>
        public void Refresh()
        {
            if (Page is not { } page || DataContext is not DuplicateGroup group || Model.Tree is not { } tree) return;
            if (ReferenceEquals(_builtFor, group) && _builtVersion == page._version) return;
            _builtFor = group;
            _builtVersion = page._version;
            Content = page.GroupCard(tree, group);
            // LazyVStack(spacing: gap) inside .padding(.bottom, pagePadding): the last card carries the padding.
            bool last = page._shown.Count > 0 && ReferenceEquals(page._shown[^1], group);
            Margin = new Thickness(0, 0, 0, last ? page._class.PagePadding() : page._class.Gap());
        }
    }

    private FrameworkElement GroupCard(FileTree tree, DuplicateGroup group)
    {
        int? keep = Keeper(tree, group);
        var ordered = DuplicateReview.Ordered(group, keep);
        string name = NodeName(tree, keep ?? FirstId(group) ?? 0);
        long frees = Reclaim(tree, group, _checked);

        var stack = new StackPanel();
        var head = new DockPanel();
        var badge = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8),
            Background = Ui.Brush("RdSoft"), BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 14, 0),
            Child = new Border
            {
                Padding = new Thickness(7, 0, 0, 5),
                Child = Badge(ListKit.Badge(name, FileKinds.Classify(name, name))),
            },
        };
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        var freed = Rd.Figure(frees > 0 ? ShellFormat.HeroBytes(frees) : "0 B", 14, FontWeights.Medium, frees > 0 ? "AppAccent" : "RdInk3");
        freed.ToolTip = "Freed by the ticked copies in this group";
        freed.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(freed, Dock.Right);
        head.Children.Add(freed);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // Name, then "3 COPIES · 1.2 GB EACH" and the policy tag right after it; the name gives way.
        var line = new DockPanel { HorizontalAlignment = HorizontalAlignment.Left };
        var labels = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        labels.Children.Add(Rd.Figure($"{group.FileIDs.Count} copies · {ShellFormat.HeroBytes(OnDisk(tree, group))} each".ToUpperInvariant(),
            11, FontWeights.Medium, "RdInk3"));
        if (_class != Rd.WidthClass.Compact)
        {
            var tag = new Border
            {
                Height = 22, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(5),
                BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(1), Margin = new Thickness(10, 0, 0, 0),
                Child = Rd.Figure(_keeperOverride.ContainsKey(DuplicateReview.GroupKey(group)) ? "you chose which stays" : _policy.Tag(),
                    11, FontWeights.Medium, "RdInk2"),
            };
            labels.Children.Add(tag);
        }
        DockPanel.SetDock(labels, Dock.Right);
        line.Children.Add(labels);
        line.Children.Add(Rd.Text(name, 15, FontWeights.SemiBold));
        texts.Children.Add(line);
        if (group.CloneSets.Count > 0)
        {
            var note = Rd.Text("Some copies are hard links or ReFS block clones of each other: removing one frees nothing until its clones go too.",
                12, null, "RdInk3", wrap: true);
            note.MaxHeight = Ui.Scaled(12) * 1.34 * 2 + 1;   // lineLimit(2)
            note.TextTrimming = TextTrimming.CharacterEllipsis;
            note.Margin = new Thickness(0, 4, 0, 0);
            texts.Children.Add(note);
        }
        head.Children.Add(texts);
        stack.Children.Add(new Border
        {
            Padding = new Thickness(20, 14, 20, 14), Child = head,
            BorderBrush = Ui.Brush("RdLine"), BorderThickness = new Thickness(0, 0, 0, 1),
        });
        foreach (int id in ordered) stack.Children.Add(CopyRow(tree, id, group, id == keep));
        var card = Rd.Card(stack);
        System.Windows.Automation.AutomationProperties.SetName(card,
            $"{name}, {group.FileIDs.Count} copies, frees {(frees > 0 ? ShellFormat.HeroBytes(frees) : "0 B")}");
        return card;
    }

    /// <summary>The tile's type, mono 9.5 medium ink2, sitting in the bottom-left corner.</summary>
    private static TextBlock Badge(string text)
    {
        var badge = Rd.Figure(text, 9.5, FontWeights.Medium, "RdInk2");
        badge.HorizontalAlignment = HorizontalAlignment.Left;
        badge.VerticalAlignment = VerticalAlignment.Bottom;
        return badge;
    }

    private FrameworkElement CopyRow(FileTree tree, int id, DuplicateGroup group, bool isKeeper)
    {
        string path = Model.PathOf(id);
        bool staged = Model.IsStaged(path);
        bool isOn = _checked.Contains(id);
        bool selected = Model.SelectedNode == id;
        string name = NodeName(tree, id);

        var grid = new Grid { Margin = new Thickness(20, 0, 20, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        FrameworkElement tick;
        if (staged)
        {
            var check = Ui.Glyph("BsCheckLg", 10, Ui.Brush("AppAccent"));
            check.HorizontalAlignment = HorizontalAlignment.Center;
            check.ToolTip = "Already in the basket";
            System.Windows.Automation.AutomationProperties.SetName(check, $"{name} is already in the basket");
            tick = check;
        }
        else
        {
            var box = CleanUpPage.TickBox(isOn ? CleanUpPage.TickState.On : CleanUpPage.TickState.Off,
                () => Toggle(tree, id, group, isKeeper));
            box.HorizontalAlignment = HorizontalAlignment.Center;
            box.ToolTip = isKeeper ? "Tick to remove this one and keep another copy instead"
                : isOn ? "Untick to keep this copy too" : "Tick to remove this copy";
            System.Windows.Automation.AutomationProperties.SetName(box, isOn ? $"Unmark {name}" : $"Mark {name} for removal");
            tick = box;
        }
        grid.Children.Add(tick);

        var place = ExploreInspector.Fitted(
            Rd.Figure(DuplicateReview.Place(path, Model.RootPath ?? ""), 12.5, null, isOn || isKeeper ? "RdInk" : "RdInk2"), 1, middle: true);
        place.Margin = new Thickness(14, 0, 0, 0);
        place.ToolTip = path;
        Grid.SetColumn(place, 1);
        grid.Children.Add(place);

        int day = id >= 0 && id < tree.Count ? tree.ModifiedDay[id] : 0;
        var date = Rd.Figure(DuplicateReview.DateText(day), 12, null, "RdInk3");
        date.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(date, 2);
        grid.Children.Add(date);

        FrameworkElement trailing = isKeeper ? Rd.Tag("Kept", Rd.TagTone.Ink)
            : staged ? Rd.Tag("In basket", Rd.TagTone.Accent)
            : Rd.Figure(ShellFormat.HeroBytes(Allocated(tree, id)), 12);
        trailing.HorizontalAlignment = HorizontalAlignment.Right;
        trailing.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(trailing, 3);
        grid.Children.Add(trailing);

        var host = new Border
        {
            Height = 44, Child = grid,
            Background = selected ? Rd.WithOpacity(Ui.Brush("RdAccentWash"), 0.6) : Brushes.Transparent,
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.7), BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.Hand,
        };
        host.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            SelectRow(tree, id);
            Focus();
        };
        var menu = new ContextMenu();
        var reveal = new MenuItem { Header = "Show in File Explorer" };
        reveal.Click += (_, _) => Explorer.Reveal(path);
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => ExploreInspector.OpenItem(path, false);
        menu.Items.Add(reveal);
        menu.Items.Add(open);
        host.ContextMenu = menu;
        System.Windows.Automation.AutomationProperties.SetName(host,
            $"{name}, {ByteFormat.Format(Allocated(tree, id))}{(isKeeper ? ", kept" : isOn ? ", ticked" : "")}");
        _rows[id] = host;
        return host;
    }

    private static void SelectRow(FileTree tree, int id)
    {
        Model.Select(id);
        int parent = id >= 0 && id < tree.Count ? tree.Parent[id] : -1;
        if (parent >= 0) Model.DrillToAncestor(parent);
    }

    private void Toggle(FileTree tree, int id, DuplicateGroup group, bool isKeeper)
    {
        var result = DuplicateReview.Toggle(group, id, isKeeper, _checked, IsStaged);
        _checked = result.Checked;
        if (result.NewKeeper is { } next)
        {
            _keeperOverride[DuplicateReview.GroupKey(group)] = next;
            _keeperCache.Remove(group);
        }
        if (result.KeepOneRefused) Model.Toast("Keep at least one copy");
        Rebuild();
    }

    // ---- Summary column ----

    private void BuildSummary(FileTree tree)
    {
        var groups = RealGroups;
        int ticked = groups.Sum(g => g.FileIDs.Count(_checked.Contains));
        int kept = groups.Count(g => g.FileIDs.Any(id => !_checked.Contains(id)));
        int clones = CloneGroups.Count;
        _summary.Children.Clear();

        var stack = new StackPanel { Margin = new Thickness(20) };
        var receipt = new StackPanel();
        var head = new DockPanel();
        string root = Model.RootPath?.TrimEnd('\\', '/') ?? "";
        int cut = root.LastIndexOfAny(['\\', '/']);
        string rootName = cut >= 0 && cut < root.Length - 1 ? root[(cut + 1)..] : root;
        var rootLabel = Rd.Figure(rootName.ToUpperInvariant(), 10, FontWeights.Medium, "RdInk3");
        rootLabel.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(rootLabel, Dock.Right);
        head.Children.Add(rootLabel);
        head.Children.Add(Rd.Text("Duplicates", 16, FontWeights.SemiBold));
        receipt.Children.Add(head);
        void add(FrameworkElement element, double top = 12)
        {
            element.Margin = new Thickness(0, top, 0, 0);
            receipt.Children.Add(element);
        }
        add(Rd.LeaderRow("Extra copies", ticked.ToString("N0")));
        add(Rd.LeaderRow("Groups", groups.Count.ToString("N0")));
        add(Rd.LeaderRow("Originals kept", $"{kept:N0} of {groups.Count:N0}"));
        if (clones > 0) add(Rd.LeaderRow("Clones skipped", $"{clones:N0} · 0 B", faint: true));
        var rule = new Border { Height = 1, Background = Ui.Brush("RdLine") };
        add(rule, 16);
        add(Rd.LeaderRow("You get back", ShellFormat.HeroBytes(Reclaimable(tree)), emphasis: true, accentValue: true), 16);
        add(Rd.LeaderRow("Where it goes", "The Recycle Bin"));
        add(Rd.LeaderRow("Undo", "Put Back, any time"));
        stack.Children.Add(Rd.Card(receipt, 18));

        var check = new StackPanel();
        check.Children.Add(Rd.Text("How we check", 13, FontWeights.Medium));
        var how = Rd.Text("Same size, then the same contents byte by byte. Names don't matter. A file is never a duplicate of itself through a link.",
            13, null, "RdInk2", wrap: true);
        how.Margin = new Thickness(0, 6, 0, 0);
        check.Children.Add(how);
        var checkLayers = new Grid();
        checkLayers.Children.Add(new Border { Padding = new Thickness(16), Child = check });
        checkLayers.Children.Add(new Border
        {
            Width = 3, HorizontalAlignment = HorizontalAlignment.Left,
            Background = Ui.Brush("AppAccent"), CornerRadius = new CornerRadius(10, 0, 0, 10),
        });
        var checkCard = Rd.Surface(checkLayers, "RdCard", 10);
        checkCard.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(checkCard);
        var hint = Rd.Text("Untick a copy to keep it. Tick a kept file to swap which one stays.", 12, null, "RdInk3", wrap: true);
        hint.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(hint);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Content = stack,
        };
        _summary.Children.Add(scroll);

        var actions = new StackPanel { Margin = new Thickness(20) };
        var toggleAll = Rd.SecondaryButton(_checked.Count == 0 ? "Tick the copies to keep" : "Untick all", () =>
        {
            if (_checked.Count == 0) SelectOtherCopies(); else _checked.Clear();
            Rebuild();
        }, fullWidth: true);
        actions.Children.Add(toggleAll);
        int count = _checked.Count;
        var add2 = Rd.AccentButton($"Add {count:N0} {(count == 1 ? "copy" : "copies")} to basket", StageSelected,
            large: true, fullWidth: true, hard: count > 0);
        add2.IsEnabled = count > 0;
        add2.Margin = new Thickness(0, 10, count > 0 ? 3 : 0, 0);
        actions.Children.Add(add2);
        var note = Rd.Text("Originals stay where they are", 12, null, "RdInk3");
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.Margin = new Thickness(0, 10, 0, 0);
        actions.Children.Add(note);
        Grid.SetRow(actions, 1);
        _summary.Children.Add(actions);
    }

    // ---- Staging ----

    /// <summary>
    /// Every ticked copy into the basket after one confirmation (Mac: stageSelected →
    /// confirmStageMany). Each clone set is its own shared-storage group, so the basket
    /// counts its blocks once and only when every member goes. Risky items are skipped with
    /// the same toast as everywhere else; what didn't go in stays ticked.
    /// </summary>
    private void StageSelected()
    {
        if (_isStaging || Model.Tree is not { } tree || _checked.Count == 0) return;
        _isStaging = true;
        try
        {
            var requests = new List<(int Id, string Reason, string? Group, int Count)>();
            int keptWhole = 0;
            var heldBack = new HashSet<int>();
            foreach (var group in AllGroups)
            {
                // Never every copy: what's ticked plus what's already in the basket must leave one.
                if (group.FileIDs.Any(_checked.Contains) && group.FileIDs.All(id => _checked.Contains(id) || IsStaged(id)))
                {
                    keptWhole++;
                    heldBack.UnionWith(group.FileIDs.Where(_checked.Contains));
                    continue;
                }
                foreach (int id in group.FileIDs.Where(_checked.Contains))
                {
                    var set = group.CloneSetOf(id);
                    requests.Add((id, set is not null ? "Shared copy" : "Duplicate copy",
                        set is not null ? "clone-" + string.Join("-", set) : null,
                        set?.Count ?? group.FileIDs.Count));
                }
            }
            var safe = requests.Where(r => Model.VerdictOf(r.Id).Advice is not (CleanupAdvice.Warn or CleanupAdvice.Never)).ToList();
            var risky = requests.Except(safe).Select(r => NodeName(tree, r.Id)).ToList();
            var totals = Model.Totals;
            if (safe.Count > 0)
            {
                var items = safe.Select(r => new Dialogs.ConfirmItem(NodeName(tree, r.Id), Model.PathOf(r.Id),
                    r.Id < totals.Length ? totals[r.Id] : Allocated(tree, r.Id))).ToList();
                string? footnote = risky.Count > 0
                    ? $"{risky.Count} risky item{(risky.Count == 1 ? "" : "s")} (WSL/Docker disks, apps, browser data…) won't be added — add those one at a time to see the warning."
                    : null;
                bool ok = Model.ConfirmBulk?.Invoke("Duplicates",
                    "Nothing is deleted yet — you review the basket and confirm again before anything moves to the Recycle Bin.",
                    items, footnote) == true;
                if (!ok) return;
            }
            int added = 0, already = 0;
            var rejected = new HashSet<int>(requests.Except(safe).Select(r => r.Id));
            rejected.UnionWith(heldBack);   // held back to keep a copy: stays ticked
            foreach (var (id, reason, group, count) in safe)
            {
                if (Model.Stage(id, reason, group, count, notify: false)) added++;
                else if (Model.IsStaged(Model.PathOf(id))) already++;
                else rejected.Add(id);
            }
            if (risky.Count > 0)
            {
                string addedText = added == 1 ? "Added 1 item to the basket" : $"Added {added:N0} items to the basket";
                string names = string.Join(", ", risky.Take(2)) + (risky.Count > 2 ? $" +{risky.Count - 2}" : "");
                Model.Toast($"{(added > 0 ? addedText + " · " : "")}skipped {risky.Count} risky item{(risky.Count == 1 ? "" : "s")} ({names}) — add those one at a time to see the warning");
            }
            else if (added > 0 && keptWhole > 0)
                Model.Toast($"Added {added:N0} item{(added == 1 ? "" : "s")} to the basket · left {keptWhole:N0} group{(keptWhole == 1 ? "" : "s")} alone to keep one copy");
            else if (added > 0) Model.ToastAdded(added);
            else if (keptWhole > 0) Model.Toast("Keep at least one copy");
            else if (already > 0) Model.Toast("Already in the basket");
            // What went in leaves the ticks; what was refused stays ticked.
            _checked = _checked.Where(rejected.Contains).ToHashSet();
        }
        finally
        {
            _isStaging = false;
            Rebuild();
        }
    }

    private void StageOne(FileTree tree, int id)
    {
        if (AllGroups.FirstOrDefault(g => g.FileIDs.Contains(id)) is { } group
            && group.FileIDs.Where(other => other != id).All(other => Model.IsStaged(Model.PathOf(other))))
        {
            Model.Toast("Keep at least one copy");
            return;
        }
        Model.Stage(id, $"Duplicate of {NodeName(tree, id)}");
    }

    // ---- Keyboard (Mac: listKeyboard) ----

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or System.Windows.Controls.Primitives.ButtonBase || Model.Tree is not { } tree || Content != _board) return;
        var ids = _shown.SelectMany(g => DuplicateReview.Ordered(g, Keeper(tree, g))).ToList();
        int selected = AllGroups.Any(g => g.FileIDs.Contains(Model.SelectedNode)) ? Model.SelectedNode : -1;
        int index = selected >= 0 ? ids.IndexOf(selected) : -1;
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Down or Key.J when mods is ModifierKeys.None or ModifierKeys.Shift && ids.Count > 0:
                Move(tree, ids[index < 0 ? 0 : Math.Min(ids.Count - 1, index + 1)]);
                e.Handled = true;
                break;
            case Key.Up or Key.K when mods is ModifierKeys.None or ModifierKeys.Shift && ids.Count > 0:
                Move(tree, ids[index < 0 ? ids.Count - 1 : Math.Max(0, index - 1)]);
                e.Handled = true;
                break;
            case Key.Space when selected >= 0:
                ExploreInspector.OpenItem(Model.PathOf(selected), false);
                e.Handled = true;
                break;
            case Key.Enter when selected >= 0 && mods == ModifierKeys.None:
                Explorer.Reveal(Model.PathOf(selected));
                e.Handled = true;
                break;
            case Key.A when mods == ModifierKeys.Control:
                SelectOtherCopies();
                Rebuild();
                e.Handled = true;
                break;
            case Key.Escape:
                _checked.Clear();
                Rebuild();
                e.Handled = true;
                break;
            case Key.Delete when selected >= 0:
            case Key.Back when selected >= 0 && mods == ModifierKeys.Control:
                StageOne(tree, selected);
                e.Handled = true;
                break;
        }
    }

    private void Move(FileTree tree, int id)
    {
        SelectRow(tree, id);
        int index = _shown.FindIndex(g => g.FileIDs.Contains(id));
        if (index < 0) return;
        // Realize the card first (the list is virtualized), then bring the row itself into view.
        if (FindPanel(_list) is { } panel) panel.BringIndexIntoViewPublic(index);
        Dispatcher.InvokeAsync(() =>
        {
            if (_rows.TryGetValue(id, out var row) && row.IsLoaded) row.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private static VirtualizingStackPanel? FindPanel(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingStackPanel panel) return panel;
            if (FindPanel(child) is { } found) return found;
        }
        return null;
    }

    // ---- Empty and working states ----

    private FrameworkElement EmptyOrWorking()
    {
        if (DuplicateSearch.IsRunning) return LoadingState();
        if (DuplicateSearch.Error is { } error)
        {
            return EmptyState(Icons.Warning, "Couldn’t finish duplicate search", error,
                ("Try again", DuplicateSearch.Start), ("Clear", DuplicateSearch.Clear), dusty: false);
        }
        if (DuplicateSearch.Phase == DuplicateScanPhase.Cancelled && AllGroups.Count == 0)
        {
            return EmptyState(Icons.Cancel, "Search cancelled",
                "No duplicate groups were kept from the cancelled run. Start again when you’re ready.",
                ("Find Duplicates", DuplicateSearch.Start), null, dusty: false);
        }
        bool ran = Model.DuplicatesSearched;
        return EmptyState(Icons.Duplicates,
            ran ? "No duplicates found" : "Find files with identical contents",
            ran ? "Nothing in this scan is stored twice."
                : "freedisk.space compares local files in stages, on this PC. Cloud-only placeholders are skipped; hard links and ReFS block clones are recognised.",
            (ran ? "Search Again" : "Find Duplicates", DuplicateSearch.Start), null, dusty: ran);
    }

    /// <summary>DiskMapEmptyState: glyph (20, ink3) or Dusty, title, message, buttons; spacing 12, padding 32.</summary>
    private static FrameworkElement EmptyState(string icon, string title, string message,
        (string Title, Action Action)? primary, (string Title, Action Action)? secondary, bool dusty)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        FrameworkElement glyph = dusty && AppSettings.Load().ShowDusty
            ? Ui.BrandIcon(Ui.Scaled(92))
            : Ui.Glyph(icon, 20, Ui.Brush("RdInk3"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        if (dusty) glyph.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(glyph);
        var head = Rd.Text(title, 13, FontWeights.Medium);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        head.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(head);
        var body = Rd.Text(message, 12, null, "RdInk2", wrap: true);
        body.TextAlignment = TextAlignment.Center;
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.MaxWidth = 420;
        body.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(body);
        if (primary is not null || secondary is not null)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            if (primary is { } p) buttons.Children.Add(Rd.PrimaryButton(p.Title, p.Action));
            if (secondary is { } s)
            {
                var b = Rd.SecondaryButton(s.Title, s.Action);
                b.Margin = new Thickness(primary is null ? 0 : 8, 0, 0, 0);
                buttons.Children.Add(b);
            }
            stack.Children.Add(buttons);
        }
        System.Windows.Automation.AutomationProperties.SetName(stack, $"{title}. {message}");
        return new Border { Background = Ui.Brush("RdPage"), Padding = new Thickness(32), Child = stack };
    }

    /// <summary>DiskMapLoadingState: the bar, the phase, the detail, "N of M size groups", Cancel; spacing 16.</summary>
    private static FrameworkElement LoadingState()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        int total = DuplicateSearch.Total, done = DuplicateSearch.Done;
        var bar = total > 0
            ? new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Clamp((double)done / total, 0, 1), Width = 320, Height = 4 }
            : new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4 };
        System.Windows.Automation.AutomationProperties.SetName(bar, "Duplicate search progress");
        stack.Children.Add(bar);
        var head = Rd.Text(DuplicateSearch.Phase.Title(), 13, FontWeights.Medium);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        head.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(head);
        var body = Rd.Text(DuplicateSearch.Detail(DateTime.UtcNow), 12, null, "RdInk2", wrap: true);
        body.TextAlignment = TextAlignment.Center;
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.MaxWidth = 420;
        body.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(body);
        if (total > 0)
        {
            var count = Rd.Figure($"{done:N0} of {total:N0} size groups", 11, null, "RdInk2");
            count.HorizontalAlignment = HorizontalAlignment.Center;
            count.Margin = new Thickness(0, 16, 0, 0);
            stack.Children.Add(count);
        }
        var cancel = Rd.SecondaryButton("Cancel", DuplicateSearch.Cancel);
        cancel.HorizontalAlignment = HorizontalAlignment.Center;
        cancel.Margin = new Thickness(0, 24, 0, 0);
        stack.Children.Add(cancel);
        System.Windows.Automation.AutomationProperties.SetName(stack, DuplicateSearch.Phase.Title());
        return new Border { Background = Ui.Brush("RdPage"), Padding = new Thickness(32), Child = stack };
    }
}

/// <summary>
/// The duplicate search, outside the page so it survives navigation and a theme change
/// (Mac: ScanModel.findDuplicates / cancelDuplicateSearch). It runs in the background, reports
/// its phase and progress, can be cancelled, and publishes its groups to
/// <see cref="ScanModel.Duplicates"/> only when it finishes for the scan it started on.
/// </summary>
internal static class DuplicateSearch
{
    private static ScanModel Model => ScanModel.Shared;
    private static readonly object Gate = new();
    private static CancellationTokenSource? _cts;
    private static FileTree? _tree;
    private static int _uiQueued;
    private static DateTime _lastPost = DateTime.MinValue;

    public static bool IsRunning { get; private set; }
    public static DuplicateScanPhase Phase { get; private set; } = DuplicateScanPhase.Idle;
    public static string? Error { get; private set; }
    public static int Done { get; private set; }
    public static int Total { get; private set; }
    public static int Examined { get; private set; }
    public static int CandidateCount { get; private set; }
    public static DateTime StartedAt { get; private set; }
    public static DateTime LastProgressAt { get; private set; }

    /// <summary>Raised on the UI thread when anything above changes.</summary>
    public static event Action? Changed;

    private static void Raise() => Changed?.Invoke();

    /// <summary>A new scan makes the old search's state meaningless: stop it and start clean.</summary>
    public static void SyncWith(FileTree? tree)
    {
        if (ReferenceEquals(_tree, tree)) return;
        _cts?.Cancel();
        _cts = null;
        _tree = tree;
        IsRunning = false;
        Phase = DuplicateScanPhase.Idle;
        Error = null;
    }

    public static async void Start()
    {
        if (Model.Tree is not { } tree || Model.RootPath is not { } root) return;
        Cancel(raise: false);
        var cts = new CancellationTokenSource();
        _cts = cts;
        _tree = tree;
        Error = null;
        IsRunning = true;
        Phase = DuplicateScanPhase.Preparing;
        Done = Total = Examined = CandidateCount = 0;
        StartedAt = LastProgressAt = DateTime.UtcNow;
        Raise();
        var dispatcher = Dispatcher.CurrentDispatcher;
        void report(Action apply)
        {
            if (!ReferenceEquals(Volatile.Read(ref _cts), cts)) return;
            lock (Gate)
            {
                apply();
                LastProgressAt = DateTime.UtcNow;
            }
            // A few repaints a second, not one per size group.
            if ((DateTime.UtcNow - _lastPost).TotalMilliseconds < 200) return;
            if (Interlocked.Exchange(ref _uiQueued, 1) != 0) return;
            _lastPost = DateTime.UtcNow;
            dispatcher.InvokeAsync(() =>
            {
                _uiQueued = 0;
                if (ReferenceEquals(_cts, cts)) Raise();
            }, DispatcherPriority.Background);
        }
        try
        {
            var groups = await Task.Run(async () =>
            {
                report(() => Phase = DuplicateScanPhase.Collecting);
                var files = DuplicateFinder.SizeCollidingCandidates(tree, root, cts.Token,
                    (examined, candidates) => report(() => { Examined = examined; CandidateCount = candidates; }),
                    minBytes: 1_000_000);
                cts.Token.ThrowIfCancellationRequested();
                report(() => Phase = DuplicateScanPhase.Grouping);
                return await DuplicateFinder.FindDuplicatesAsync(files,
                    (phase, done, total) => report(() => { Phase = phase; Done = done; Total = total; }), cts.Token);
            }, cts.Token);
            if (!ReferenceEquals(_cts, cts) || !ReferenceEquals(Model.Tree, tree)) return;
            Phase = DuplicateScanPhase.Assembling;
            Model.Duplicates = groups;
            Model.MarkDuplicatesSearched();
            Phase = groups.Count == 0 ? DuplicateScanPhase.NoResults : DuplicateScanPhase.Complete;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts)) Phase = DuplicateScanPhase.Cancelled;
        }
        catch (Exception e)
        {
            if (ReferenceEquals(_cts, cts))
            {
                Error = e.Message;
                Phase = DuplicateScanPhase.Failed;
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                IsRunning = false;
                Raise();
            }
            cts.Dispose();
        }
    }

    public static void Cancel() => Cancel(raise: true);

    private static void Cancel(bool raise)
    {
        _cts?.Cancel();
        _cts = null;
        if (IsRunning)
        {
            IsRunning = false;
            Phase = DuplicateScanPhase.Cancelled;
        }
        if (raise) Raise();
    }

    /// <summary>"Try again"'s neighbour: drop the error and go back to the start.</summary>
    public static void Clear()
    {
        Error = null;
        Phase = DuplicateScanPhase.Idle;
        Raise();
    }

    /// <summary>The loading state's detail line, with the elapsed time or the stall note (Mac: loadingDetail).</summary>
    public static string Detail(DateTime now)
    {
        int elapsed = (int)(now - StartedAt).TotalSeconds;
        bool stalled = (now - LastProgressAt).TotalSeconds >= 30;
        string suffix = stalled
            ? " No progress has been reported for 30 seconds; the disk may still be working. You can cancel safely."
            : $" Elapsed: {elapsed}s.";
        return Phase switch
        {
            DuplicateScanPhase.Preparing => "Preparing a local comparison." + suffix,
            DuplicateScanPhase.Collecting =>
                $"{Examined:N0} items checked · {CandidateCount:N0} local candidates. Cloud-only placeholders are skipped." + suffix,
            DuplicateScanPhase.Grouping => "Looking for files that share the same size — the inexpensive first filter." + suffix,
            DuplicateScanPhase.Hashing =>
                "Comparing candidate groups locally. Hard links and ReFS block clones avoid a full content hash when possible." + suffix,
            DuplicateScanPhase.Assembling => "Preparing the groups for review." + suffix,
            _ => "Working…",
        };
    }
}
