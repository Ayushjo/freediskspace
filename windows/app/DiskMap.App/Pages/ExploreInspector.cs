using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.Core;

// FreeableIndex and the inspector's wording helpers are internal; the app tests check them.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DiskMap.App.Tests")]

namespace DiskMap.App.Pages;

/// <summary>
/// Explore's inspector column (Mac: Kit/KitRows.swift FileInspector and FolderInspector, as
/// VisualizeView.inspector uses them). A folder: "Selected folder", its size as the hero,
/// what can be freed, "What is this?", the biggest things inside, the largest files, the
/// safety line and the actions. A file: header, facts, why it's large, safety, actions.
/// Same metrics as the Mac views; Quick Look becomes "Open" on Windows.
/// </summary>
internal static class ExploreInspector
{
    private static ScanModel Model => ScanModel.Shared;

    /// <summary>DiskMapMetric.inspectorPadding.</summary>
    private const double Padding = 20;

    /// <summary>InspectorColumn's VStack spacing.</summary>
    private const double Spacing = 18;

    // ---- Layout (SharedChrome.swift DiskMapLayout / AdaptiveInspectorSplit) ----

    /// <summary>At 1200 pt and wider the inspector is a column beside the page; narrower, a drawer.</summary>
    internal static bool ShowsSideInspector(double windowWidth) => windowWidth >= 1200;

    /// <summary>
    /// The inspector's width: 320 at 1450 and wider, else 290 beside the page; as a drawer on a
    /// narrow window, 34% of the window clamped to 280…320.
    /// </summary>
    internal static double ColumnWidth(double windowWidth) =>
        ShowsSideInspector(windowWidth)
            ? (windowWidth >= 1450 ? 320 : 290)
            : Math.Min(320, Math.Max(280, windowWidth * 0.34));

    // ---- Entry ----

    /// <summary>
    /// The inspector for <paramref name="nodeId"/> in the current scan: the folder or the file
    /// inspector, or "Select an item" when the id is not in the tree. <paramref name="widthClass"/>
    /// is accepted for the page's layout pass; the Mac inspector has one size at every width.
    /// </summary>
    public static FrameworkElement Build(int nodeId, Rd.WidthClass widthClass)
    {
        _ = widthClass;
        if (Model.Tree is not { } tree || Model.RootPath is not { } root
            || nodeId < 0 || nodeId >= tree.Count || Model.Totals.Length != tree.Count)
        {
            return EmptyState(Rd.IconForSymbol("square.grid.2x2"), "Select an item", "Click a tile to inspect it.", "RdPage");
        }

        // Mac: staging re-renders the inspector ("In basket"); staging raises StagedChanged
        // here, not StateChanged, so the host follows it while it is on screen.
        var host = new Border { Background = Ui.Brush("RdSidebar") };
        bool isDirectory = tree.IsDirectory[nodeId];
        void render()
        {
            if (!ReferenceEquals(Model.Tree, tree)) return;
            if (isDirectory) RenderFolder(host, tree, root, nodeId);
            else host.Child = FileInspector(tree, root, nodeId);
        }
        EventHandler onStaged = (_, _) => host.Dispatcher.InvokeAsync(render);
        host.Loaded += (_, _) => Model.StagedChanged += onStaged;
        host.Unloaded += (_, _) => Model.StagedChanged -= onStaged;
        render();
        return host;
    }

    // ---- Folder inspector ----

    /// <summary>The last insight built, so a page rebuild doesn't flash "Reading folder" (Mac: @State insight).</summary>
    private static (int Id, FileTree Tree, long[] Totals, FolderInsight? Insight)? _built;

    /// <summary>
    /// Composition and largest files walk the whole subtree, which is slow for a home folder
    /// or a drive root, so the insight is built off the UI thread (Mac: .task(id: buildKey)).
    /// </summary>
    private static void RenderFolder(Border host, FileTree tree, string root, int id)
    {
        var totals = Model.Totals;
        if (_built is { } done && done.Id == id && ReferenceEquals(done.Tree, tree) && ReferenceEquals(done.Totals, totals))
        {
            host.Child = done.Insight is { } ready
                ? FolderInspector(tree, ready)
                : EmptyState(Icons.Folder, "Select a folder", "Its details and actions appear here.", "RdSidebar");
            return;
        }
        // Keep what's on screen while a staging refresh rebuilds the same folder.
        if (host.Child is null) host.Child = LoadingState("Reading folder", tree.NameOf(id));
        var (files, folders) = Model.Counts;
        Task.Run(() => FolderInsight.Build(id, tree, root, totals, files, folders))
            .ContinueWith(task =>
            {
                var insight = task.IsCompletedSuccessfully ? task.Result : null;
                host.Dispatcher.InvokeAsync(() =>
                {
                    // A newer scan or a size-basis change landed while this one ran.
                    if (!ReferenceEquals(Model.Tree, tree) || !ReferenceEquals(Model.Totals, totals)) return;
                    _built = (id, tree, totals, insight);
                    RenderFolder(host, tree, root, id);
                });
            }, TaskScheduler.Default);
    }

    private static FrameworkElement FolderInspector(FileTree tree, FolderInsight insight)
    {
        int id = insight.NodeID;
        var totals = Model.Totals;
        string path = insight.AbsolutePath;
        string name = DisplayName(tree, id);
        bool staged = Model.IsStaged(path);
        var index = new FreeableIndex(Model.Reviewables ?? [], totals);
        long freeBytes = index.Bytes(id, tree, totals);
        bool insideTarget = index.Contains(id, tree);
        var verdict = StorageClassifier.Classify(path, isDirectory: true);
        var safety = SafetyClassifier.Assess(path, name, isDirectory: true);
        var kids = tree.ChildrenOf(id, totals).Where(k => k.Size > 0).OrderByDescending(k => k.Size).Take(4).ToList();
        long biggestKid = Math.Max(1, kids.Count > 0 ? kids[0].Size : 1);

        var column = new List<FrameworkElement>();

        // Header: "Selected folder", the name, the path (VStack spacing 4).
        var header = new StackPanel();
        header.Children.Add(Rd.Text("Selected folder", 12, null, "RdInk3"));
        var title = Rd.Text(name, 22, FontWeights.SemiBold, "RdInk", wrap: true);
        title.Margin = new Thickness(0, 4, 0, 0);
        header.Children.Add(Fitted(title, 2, middle: false));
        var place = Rd.Figure(CanonicalPath.DisplayPath(path), 11, null, "RdInk3");
        place.TextWrapping = TextWrapping.Wrap;
        place.Margin = new Thickness(0, 4, 0, 0);
        place.ToolTip = path;
        header.Children.Add(Fitted(place, 2, middle: true));
        column.Add(header);

        // The hero: tightText(hero(bytes), kerning: -1.6), 40 semibold, shrinks to 60%.
        var hero = Rd.TightText(ShellFormat.HeroBytes(insight.Bytes), 40, -1.6);
        column.Add(Shrinkable(hero, 0.6));

        // Facts (VStack spacing 7).
        var facts = new StackPanel();
        facts.Children.Add(KeyValue("You can free", freeBytes > 0 ? ShellFormat.Bytes(freeBytes) : "Nothing here", accent: freeBytes > 0));
        var changed = KeyValue("Last changed", LastUsed(DayOf(tree, id), AgeMap.Today()));
        changed.Margin = new Thickness(0, 7, 0, 0);
        facts.Children.Add(changed);
        var count = KeyValue("Files", insight.FileCount.ToString("N0", CultureInfo.CurrentCulture));
        count.Margin = new Thickness(0, 7, 0, 0);
        facts.Children.Add(count);
        column.Add(facts);

        column.Add(WhatIsThis(WhatIsThisText(insideTarget, verdict.Class, insight.WhyLarge), verdict.Note));

        if (kids.Count > 0) column.Add(BiggestInside(tree, kids, biggestKid, index));
        if (insight.LargestFiles.Count > 0) column.Add(LargestFiles(insight.LargestFiles));
        column.Add(SafetyLine(safety));
        var actions = FolderActions(tree, insight, name, staged, freeBytes, insideTarget, safety);
        actions.Margin = new Thickness(0, 4, 0, 0);
        column.Add(actions);
        return InspectorColumn(column, $"Folder {name}");
    }

    /// <summary>
    /// "What is this?": inside a Clean up target it rebuilds by itself; otherwise the storage
    /// class's blurb, or the composition sentence for Other.
    /// </summary>
    internal static string WhatIsThisText(bool insideTarget, StorageClass storageClass, string whyLarge) =>
        insideTarget
            ? "This rebuilds by itself. " + (storageClass.Blurb.Length == 0 ? whyLarge : storageClass.Blurb)
            : storageClass.Id == "other" ? whyLarge : storageClass.Blurb;

    /// <summary>The note card: card fill, 1 px line, radius 7, a 3 pt accent rule on the leading edge.</summary>
    private static FrameworkElement WhatIsThis(string text, string? note)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        stack.Children.Add(Rd.Text("What is this?", 13, FontWeights.Medium));
        var body = Rd.Text(text, 12, null, "RdInk2", wrap: true);
        body.LineHeight = Ui.Scaled(12) * 1.33 + 2;   // .lineSpacing(2)
        body.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(body);
        if (note is { Length: > 0 })
        {
            var extra = Rd.Text(note, 12, null, "RdInk2", wrap: true);
            extra.Margin = new Thickness(0, 3 + 2, 0, 0);
            stack.Children.Add(extra);
        }
        var layers = new Grid();
        layers.Children.Add(stack);
        layers.Children.Add(new Border
        {
            Width = 3, HorizontalAlignment = HorizontalAlignment.Left,
            Background = Ui.Brush("AppAccent"), CornerRadius = new CornerRadius(7, 0, 0, 7),
        });
        var card = Rd.Surface(layers, "RdCard", 7);
        System.Windows.Automation.AutomationProperties.SetName(card, "What is this? " + text + (note is { Length: > 0 } ? " " + note : ""));
        return card;
    }

    /// <summary>"Biggest inside": four rows on a card, each with a soft bar sized to the biggest one.</summary>
    private static FrameworkElement BiggestInside(FileTree tree, List<(int Id, long Size)> kids, long biggestKid, FreeableIndex index)
    {
        var stack = new StackPanel();
        stack.Children.Add(Rd.Text("Biggest inside", 12, null, "RdInk3"));
        var rows = new StackPanel();
        foreach (var (kidId, size) in kids)
        {
            bool rebuilds = index.Contains(kidId, tree);
            rows.Children.Add(KidRow(kidId, DisplayName(tree, kidId), size, (double)size / biggestKid, rebuilds));
        }
        var card = Rd.Surface(rows, "RdCard", 9);
        card.Margin = new Thickness(0, 7, 0, 0);
        stack.Children.Add(card);
        return stack;
    }

    private static FrameworkElement KidRow(int kidId, string name, long size, double fraction, bool rebuilds)
    {
        var line = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 0, 10, 0) };
        var sizeText = Rd.Figure(ShellFormat.Bytes(size), 11);
        sizeText.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(sizeText, Dock.Right);
        line.Children.Add(sizeText);
        var title = Rd.Text(name, 13);
        DockPanel.SetDock(title, Dock.Left);
        line.Children.Add(title);
        TextBlock? tag = null;
        if (rebuilds)
        {
            tag = Rd.Text("rebuilds", 12, null, "RdAccentDeep");
            tag.Margin = new Thickness(7, 0, 0, 0);
            tag.HorizontalAlignment = HorizontalAlignment.Left;
            line.Children.Add(tag);
        }
        else
        {
            line.Children.Add(new Border());   // the Spacer
        }
        // The name truncates before "rebuilds" does (it keeps its width beside the size).
        line.SizeChanged += (_, e) =>
        {
            double reserved = sizeText.ActualWidth + 4;
            if (tag is not null)
            {
                tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                reserved += tag.DesiredSize.Width + 7;
            }
            title.MaxWidth = Math.Max(0, e.NewSize.Width - reserved);
        };

        var bar = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = Ui.Brush("RdSoft"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(5, 5, 0, 5),
        };
        var layers = new Grid { Height = 34, Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true };
        layers.Children.Add(bar);
        layers.Children.Add(line);
        layers.SizeChanged += (_, e) =>
            bar.Width = Math.Max(6, (e.NewSize.Width - 10) * 0.86 * Math.Clamp(fraction, 0, 1));
        layers.MouseLeftButtonUp += (_, _) => Model.Select(kidId);
        layers.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { Model.Select(kidId); e.Handled = true; }
        };
        layers.ToolTip = $"Inspect {name}";
        System.Windows.Automation.AutomationProperties.SetName(layers,
            $"{name}, {ShellFormat.Bytes(size)}{(rebuilds ? ", rebuilds by itself" : "")}");
        return new Border
        {
            BorderBrush = Rd.WithOpacity(Ui.Brush("RdLine"), 0.6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = layers,
        };
    }

    /// <summary>"Largest files": label and "View all", then up to four name / size lines (spacing 6).</summary>
    private static FrameworkElement LargestFiles(IReadOnlyList<FolderLargestFile> files)
    {
        var stack = new StackPanel();
        var head = new DockPanel();
        // Mac: Biggest Files filtered to this folder. Windows' Biggest Files has no folder filter
        // yet, so it opens the whole list.
        var all = Rd.Link("View all", () => Model.ShowPage("Biggest Files"), 13);
        all.ToolTip = "Open Biggest Files";
        DockPanel.SetDock(all, Dock.Right);
        head.Children.Add(all);
        head.Children.Add(Rd.Text("Largest files", 12, null, "RdInk3"));
        stack.Children.Add(head);
        foreach (var file in files.Take(4))
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var size = Rd.Figure(ShellFormat.Bytes(file.Bytes), 11, null, "RdInk2");
            size.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            row.Children.Add(Fitted(Rd.Text(file.Name, 12), 1, middle: true));
            System.Windows.Automation.AutomationProperties.SetName(row, $"{file.Name}, {ShellFormat.Bytes(file.Bytes)}");
            stack.Children.Add(row);
        }
        return stack;
    }

    private static FrameworkElement FolderActions(FileTree tree, FolderInsight insight, string name, bool staged,
        long freeBytes, bool insideTarget, SafetyAssessment safety)
    {
        int id = insight.NodeID;
        string path = insight.AbsolutePath;
        var stack = new StackPanel();

        // Reveal | Open (Quick Look), side by side, full width (HStack spacing 8).
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var reveal = Rd.SecondaryButton("Reveal", () => Explorer.Reveal(path), fullWidth: true);
        reveal.ToolTip = "Show in File Explorer";
        var open = Rd.SecondaryButton("Open", () => OpenItem(path, isDirectory: true), fullWidth: true);
        open.ToolTip = "Open in File Explorer";
        Grid.SetColumn(open, 2);
        pair.Children.Add(reveal);
        pair.Children.Add(open);
        stack.Children.Add(pair);

        Button add;
        if (freeBytes > 0 && !insideTarget)
        {
            add = Rd.AccentButton($"Add {ShellFormat.Bytes(freeBytes)} to basket",
                () => StageFreeable(tree, id, name), large: true, fullWidth: true, hard: true);
            add.ToolTip = "Adds only what rebuilds by itself, as in Clean up";
        }
        else
        {
            add = Rd.AccentButton(staged ? "In basket" : $"Add {ShellFormat.Bytes(insight.Bytes)} to basket",
                () =>
                {
                    if (staged) Model.OpenBasket();
                    else Model.Stage(id, "Explore: " + name);
                },
                large: true, fullWidth: true, hard: !staged);
            add.IsEnabled = safety.Level != SafetyLevel.Protected;
        }
        add.Margin = new Thickness(0, 8, 3, 0);
        stack.Children.Add(add);

        var more = MoreMenu(
        [
            ("Open in File Browser", () => { Model.DrillTo(id); Model.Select(id); Model.ShowPage("File Browser"); }),
            ("Show in Explore", () => { Model.DrillTo(id); Model.Select(id); Model.ShowPage("Visualize"); }),
            ("Biggest Files in This Folder", () => Model.ShowPage("Biggest Files")),
            ("Copy Path", () => CopyPath(path)),
        ]);
        more.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(more);
        return stack;
    }

    /// <summary>
    /// The safe Clean up items inside this folder, staged as one bulk add after one confirmation
    /// (Mac: stageFreeable → confirmStageMany). Each keeps its own reason ("Explore: npm cache").
    /// </summary>
    private static void StageFreeable(FileTree tree, int id, string name)
    {
        var requests = FreeableRequests(Model.Reviewables ?? [], tree, id);
        if (requests.Count == 0) return;
        var totals = Model.Totals;
        var safe = requests.Where(r => Model.VerdictOf(r.NodeId).Advice is not (CleanupAdvice.Warn or CleanupAdvice.Never)).ToList();
        var risky = requests.Except(safe).Select(r => tree.NameOf(r.NodeId)).ToList();
        if (safe.Count > 0)
        {
            var items = safe.Select(r => new Dialogs.ConfirmItem(tree.NameOf(r.NodeId), Model.PathOf(r.NodeId),
                r.NodeId < totals.Length ? totals[r.NodeId] : 0)).ToList();
            string? footnote = risky.Count > 0
                ? $"{risky.Count} risky item{(risky.Count == 1 ? "" : "s")} (WSL/Docker disks, apps, browser data…) won't be added — add those one at a time to see the warning."
                : null;
            bool ok = Model.ConfirmBulk?.Invoke($"Things that rebuild by themselves in {name}",
                "These will be added to Cleanup. Nothing is deleted yet — you review the list on the Cleanup page and confirm again before anything moves to the Recycle Bin.",
                items, footnote) == true;
            if (!ok) return;
        }
        int added = 0, already = 0;
        foreach (var (nodeId, reason) in safe)
        {
            if (Model.Stage(nodeId, reason, notify: false)) added++;
            else already++;
        }
        // The same toasts as ScanModel.StageMany.
        if (risky.Count > 0)
        {
            string addedText = added == 1 ? "Added 1 item to Cleanup" : $"Added {added:N0} items to Cleanup";
            string names = string.Join(", ", risky.Take(2)) + (risky.Count > 2 ? $" +{risky.Count - 2}" : "");
            Model.Toast($"{(added > 0 ? addedText + " · " : "")}skipped {risky.Count} risky item{(risky.Count == 1 ? "" : "s")} ({names}) — add those one at a time to see the warning");
        }
        else if (added > 0) Model.ToastAdded(added);
        else if (already > 0) Model.Toast("Already in Cleanup");
    }

    /// <summary>
    /// The nodes "Add X to basket" stages: every generally safe target's nodes that are the
    /// folder or inside it, once each, with "Explore: " and the target's name as the reason.
    /// </summary>
    internal static List<(int NodeId, string Reason)> FreeableRequests(IReadOnlyList<ReviewableTarget> reviewables,
        FileTree tree, int folder)
    {
        var output = new List<(int, string)>();
        var seen = new HashSet<int>();
        foreach (var target in reviewables)
        {
            if (!target.IsGenerallySafe) continue;
            foreach (int node in target.NodeIds)
            {
                if (node < 0 || node >= tree.Count) continue;
                if ((node == folder || FreeableIndex.IsAncestor(folder, node, tree)) && seen.Add(node))
                    output.Add((node, "Explore: " + target.DisplayName));
            }
        }
        return output;
    }

    // ---- File inspector ----

    private static FrameworkElement FileInspector(FileTree tree, string root, int id)
    {
        var totals = Model.Totals;
        long size = id < totals.Length ? totals[id] : 0;
        string name = tree.NameOf(id);
        string path = tree.PathOf(id, root);
        var kind = FileKinds.Classify(name, path);
        var safety = SafetyClassifier.Assess(path, name, isDirectory: false);
        var volume = Model.Snapshot?.Volume ?? Model.Volume;
        long used = volume is { } v ? Math.Max(1, v.UsedBytes)
            : Math.Max(1, Model.Snapshot?.ScannedBytes ?? (totals.Length > 0 ? totals[0] : 0));
        double share = (double)size / used;
        bool allowTrash = safety.Level != SafetyLevel.Protected && kind != FileKind.VirtualDisk;
        bool staged = Model.IsStaged(path);

        var column = new List<FrameworkElement>
        {
            InspectorHeader(name, ShellFormat.Bytes(size), $"{kind.Title()} · {Percent(share)} of used space", FileIdentityIcon(kind, 40)),
            Hairline(),
            FactRow("Location", ParentDisplay(path)),
        };
        column.Add(FactRow("Modified", RelativeAgeLong(DayOf(tree, id), AgeMap.Today())));
        // Mac: "APFS clone". The Windows twin is a ReFS block clone (WIN-066).
        if (tree.SharingInfoOf(id) is { } shared)
            column.Add(FactRow("Block clone",
                $"Shares {ShellFormat.Bytes(shared.SharedBytes)} with {shared.OtherCopies} other cop{(shared.OtherCopies == 1 ? "y" : "ies")}"));
        column.Add(Hairline());
        column.Add(NoteBlock("Why it's large", WhyLarge(kind)));
        column.Add(SafetyLine(safety));
        if (kind == FileKind.VirtualDisk || name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
            column.Add(Rd.Text("Don't delete this file directly — manage its storage in the app that owns it.", 12, null, "AppDanger", wrap: true));

        int parent = tree.Parent[id] >= 0 ? tree.Parent[id] : 0;
        var actions = InspectorActions(
            staged ? "In the basket" : "Add to basket", staged, allowTrash,
            () =>
            {
                if (staged) Model.OpenBasket();
                else Model.Stage(id, "Explore: " + name);
            },
            path,
            [
                ("Show in Visualize", () => { Model.DrillTo(parent); Model.Select(id); Model.ShowPage("Visualize"); }),
                ("Show in File Browser", () => { Model.DrillTo(parent); Model.Select(id); Model.ShowPage("File Browser"); }),
            ]);
        actions.Margin = new Thickness(0, 4, 0, 0);
        column.Add(actions);
        return InspectorColumn(column, $"File {name}");
    }

    /// <summary>InspectorHeader: icon, name (heading, 3 lines), the size as the display figure, one mono line (spacing 10).</summary>
    private static FrameworkElement InspectorHeader(string name, string size, string detail, FrameworkElement icon)
    {
        var stack = new StackPanel();
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(icon);
        var title = Rd.Text(name, 15, FontWeights.SemiBold, "RdInk", wrap: true);
        title.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(Fitted(title, 3, middle: false));
        var figure = Rd.Text(size, 28, FontWeights.SemiBold);
        System.Windows.Documents.Typography.SetNumeralAlignment(figure, FontNumeralAlignment.Tabular);
        var shrink = Shrinkable(figure, 0.6);
        shrink.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(shrink);
        var line = Rd.Figure(detail, 11, null, "RdInk2");
        line.TextWrapping = TextWrapping.Wrap;
        line.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(Fitted(line, 2, middle: false));
        return stack;
    }

    /// <summary>
    /// FileIdentityIcon's kind glyph: a rounded square tinted with the kind colour at 16%
    /// (hover for Other), the glyph at 44% of the size. The Mac's video thumbnail and app icon
    /// have no Windows source here, so those kinds draw the glyph too.
    /// </summary>
    private static FrameworkElement FileIdentityIcon(FileKind kind, double size)
    {
        Brush tint = KindColor(kind);
        bool other = kind == FileKind.Other;
        var glyph = Ui.Glyph(KindIcon(kind), size * 0.44, other ? Ui.Brush("RdInk2") : tint);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var tile = new Border
        {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(Math.Max(5, size * 0.24)),
            Background = other ? Ui.Brush("RdHover") : Rd.WithOpacity(tint, 0.16),
            Child = glyph,
        };
        return tile;
    }

    /// <summary>DiskMapTheme.kindColor.</summary>
    private static Brush KindColor(FileKind kind) => kind switch
    {
        FileKind.Video => Ui.Hex("#C78797"),
        FileKind.DiskImage => Ui.Hex("#849BB8"),
        FileKind.Archive => Ui.Hex("#B9A071"),
        FileKind.Application => Ui.Hex("#8FACC0"),
        FileKind.Document => Ui.Hex("#7BA89C"),
        FileKind.VirtualDisk => Ui.Hex("#A795C7"),
        FileKind.DeviceBackup => Ui.Hex("#9FB5A9"),
        FileKind.Database => Ui.Hex("#A2A4AC"),
        _ => Ui.Brush("RdInk3"),
    };

    /// <summary>FileKind.symbolName as the bundled Bootstrap icons.</summary>
    private static string KindIcon(FileKind kind) => kind switch
    {
        FileKind.Video => Icons.Video,
        FileKind.DiskImage => Icons.DiskImage,
        FileKind.Archive => Icons.Archive,
        FileKind.Application => Icons.Application,
        FileKind.Document => "BsFileText",
        FileKind.VirtualDisk => Icons.Drive,
        FileKind.Database => Icons.Drive,
        FileKind.DeviceBackup => Icons.Drive,
        _ => Icons.File,
    };

    /// <summary>InspectorActions: one primary button, then Reveal / Open / Copy Path and the ⋯ menu.</summary>
    private static FrameworkElement InspectorActions(string primaryTitle, bool primaryDone, bool primaryEnabled,
        Action primary, string path, IReadOnlyList<(string Title, Action Action)> more)
    {
        var stack = new StackPanel();
        var button = primaryDone
            ? Rd.SecondaryButton(primaryTitle, primary, fullWidth: true)
            : Rd.AccentButton(primaryTitle, primary, fullWidth: true);
        Brush ink = primaryDone ? Ui.Brush("RdInk") : Ui.Brush("RdOnAccent");
        button.Content = IconLabel(primaryDone ? "BsCheckLg" : Icons.Add, primaryTitle, ink);
        button.IsEnabled = primaryEnabled;
        stack.Children.Add(button);

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        var buttons = new List<FrameworkElement>
        {
            Rd.IconButton(Icons.Open, "Show in File Explorer", () => Explorer.Reveal(path)),
            Rd.IconButton(Icons.Visibility, "Open", () => OpenItem(path, isDirectory: false)),
            Rd.IconButton(Icons.Copy, "Copy Path", () => CopyPath(path)),
        };
        for (int i = 0; i < buttons.Count; i++)
        {
            if (i > 0) buttons[i].Margin = new Thickness(2, 0, 0, 0);
            DockPanel.SetDock(buttons[i], Dock.Left);
            row.Children.Add(buttons[i]);
        }
        Border? ellipsis = null;
        ellipsis = Rd.IconButton(Icons.More, "More actions", () =>
        {
            if (ellipsis is not null) OpenMenu(ellipsis, more);
        });
        ellipsis.ToolTip = "More";
        DockPanel.SetDock(ellipsis, Dock.Right);
        row.Children.Add(ellipsis);
        stack.Children.Add(row);
        return stack;
    }

    /// <summary>A button label with its glyph, coloured on the elements themselves (see Rd.Label).</summary>
    private static StackPanel IconLabel(string icon, string text, Brush ink)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var glyph = Ui.Glyph(icon, 12, ink);
        glyph.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(glyph);
        row.Children.Add(new TextBlock
        {
            Text = text, Foreground = ink, FontSize = Ui.Scaled(13), FontWeight = FontWeights.Medium,
            FontFamily = Rd.FontFor(13), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return row;
    }

    // ---- Shared parts (Kit/Kit.swift) ----

    /// <summary>InspectorColumn: a scroll view, 20 pt padding, 18 pt between groups, on the sidebar colour.</summary>
    private static FrameworkElement InspectorColumn(List<FrameworkElement> parts, string name)
    {
        var stack = new StackPanel { Margin = new Thickness(Padding) };
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var m = part.Margin;
            part.Margin = new Thickness(m.Left, m.Top + (i > 0 ? Spacing : 0), m.Right, m.Bottom);
            stack.Children.Add(part);
        }
        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Background = Ui.Brush("RdSidebar"),
        };
        System.Windows.Automation.AutomationProperties.SetName(scroll, "Inspector: " + name);
        return scroll;
    }

    private static Border Hairline() => new() { Height = 1, Background = Ui.Brush("RdLine") };

    /// <summary>FactRow: a mono label over a mono value that wraps (spacing 3).</summary>
    private static FrameworkElement FactRow(string label, string value)
    {
        var stack = new StackPanel();
        stack.Children.Add(Rd.MonoLabel(label));
        var text = Rd.Figure(value, 12);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(text);
        System.Windows.Automation.AutomationProperties.SetName(stack, $"{label}: {value}");
        return stack;
    }

    /// <summary>Note: a mono label over one secondary sentence, no box (spacing 4).</summary>
    private static FrameworkElement NoteBlock(string label, string text)
    {
        var stack = new StackPanel();
        stack.Children.Add(Rd.MonoLabel(label));
        var body = Rd.Text(text, 12, null, "RdInk2", wrap: true);
        body.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(body);
        System.Windows.Automation.AutomationProperties.SetName(stack, $"{label}: {text}");
        return stack;
    }

    /// <summary>SafetyLine: the dot and its word (SafetyLabel), then why and what removing it means (spacing 4).</summary>
    private static FrameworkElement SafetyLine(SafetyAssessment assessment)
    {
        var stack = new StackPanel();
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6, Fill = SafetyTint(assessment.Level), VerticalAlignment = VerticalAlignment.Center,
        });
        var word = Rd.Text(SafetyTitle(assessment.Level), 12, null, "RdInk2");
        word.Margin = new Thickness(5, 0, 0, 0);
        label.Children.Add(word);
        stack.Children.Add(label);
        string sentence = SafetySentence(assessment);
        var body = Rd.Text(sentence, 12, null, "RdInk2", wrap: true);
        body.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(body);
        System.Windows.Automation.AutomationProperties.SetName(stack, $"{SafetyTitle(assessment.Level)}. {sentence}");
        return stack;
    }

    /// <summary>SafetyLabel's word: "Safe", "Review first", "Protected".</summary>
    internal static string SafetyTitle(SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => "Safe",
        SafetyLevel.Review => "Review first",
        _ => "Protected",
    };

    internal static string SafetySentence(SafetyAssessment a) =>
        a.Consequences.Length == 0 ? a.Reason : $"{a.Reason} {a.Consequences}";

    private static Brush SafetyTint(SafetyLevel level) => level switch
    {
        SafetyLevel.Safe => Ui.Brush("AppSuccess"),
        SafetyLevel.Review => Ui.Brush("AppWarning"),
        _ => Ui.Brush("AppDanger"),
    };

    /// <summary>The folder's kv rows: body ink2 label, mono value (accent for what can be freed).</summary>
    private static FrameworkElement KeyValue(string label, string value, bool accent = false)
    {
        var row = new DockPanel();
        var figure = Rd.Figure(value, 12, null, accent ? "AppAccent" : "RdInk");
        figure.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(figure, Dock.Right);
        row.Children.Add(figure);
        row.Children.Add(Rd.Text(label, 13, null, "RdInk2"));
        System.Windows.Automation.AutomationProperties.SetName(row, $"{label}: {value}");
        return row;
    }

    /// <summary>A borderless "More ⌄" menu, trailing (Mac: Menu("More").menuStyle(.borderlessButton)).</summary>
    private static FrameworkElement MoreMenu(IReadOnlyList<(string Title, Action Action)> items)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(Rd.Text("More", 13));
        var chevron = Ui.Glyph(Icons.ChevronDown, 9, Ui.Brush("RdInk2"));
        chevron.Margin = new Thickness(4, 1, 0, 0);
        label.Children.Add(chevron);
        var button = new Border
        {
            Child = label, Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true,
            HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(4, 2, 0, 2),
        };
        button.MouseLeftButtonUp += (_, e) => { OpenMenu(button, items); e.Handled = true; };
        button.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space or Key.Down) { OpenMenu(button, items); e.Handled = true; }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "More");
        return button;
    }

    private static void OpenMenu(FrameworkElement anchor, IReadOnlyList<(string Title, Action Action)> items)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (title, action) in items)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            System.Windows.Automation.AutomationProperties.SetName(item, title);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    /// <summary>DiskMapEmptyState: glyph (20, ink3), title, message, centered with 32 pt padding (spacing 12).</summary>
    private static FrameworkElement EmptyState(string icon, string title, string message, string background)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = Ui.Glyph(icon, 20, Ui.Brush("RdInk3"));
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
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
        System.Windows.Automation.AutomationProperties.SetName(stack, $"{title}. {message}");
        return new Border { Background = Ui.Brush(background), Padding = new Thickness(32), Child = stack };
    }

    /// <summary>DiskMapLoadingState: an indeterminate bar, the title, the detail (spacing 16).</summary>
    private static FrameworkElement LoadingState(string title, string detail)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 120, Height = 4 });
        var head = Rd.Text(title, 13, FontWeights.Medium);
        head.HorizontalAlignment = HorizontalAlignment.Center;
        head.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(head);
        var body = Rd.Text(detail, 12, null, "RdInk2", wrap: true);
        body.TextAlignment = TextAlignment.Center;
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.MaxWidth = 420;
        body.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(body);
        System.Windows.Automation.AutomationProperties.SetName(stack, title);
        return new Border { Background = Ui.Brush("RdSidebar"), Padding = new Thickness(32), Child = stack };
    }

    // ---- Actions ----

    /// <summary>Extensions "Open" won't launch — it reveals them instead (Quick Look never runs anything).</summary>
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".scr", ".pif", ".lnk", ".url", ".reg",
        ".cpl", ".hta", ".jar", ".application", ".appref-ms", ".gadget", ".inf", ".sys", ".dll",
    };

    /// <summary>
    /// Quick Look's Windows stand-in: a folder opens in File Explorer, a document in its app.
    /// Programs and scripts are revealed rather than run.
    /// </summary>
    internal static void OpenItem(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory || System.IO.Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
            else if (RunnableExtensions.Contains(System.IO.Path.GetExtension(path)))
                Explorer.Reveal(path);
            else if (System.IO.File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                Explorer.Reveal(path);
        }
        catch { /* no handler for the type is not fatal */ }
    }

    private static void CopyPath(string path)
    {
        try { Clipboard.SetText(path); }
        catch { /* the clipboard can be locked by another app */ }
    }

    // ---- Wording ----

    /// <summary>The node's name; a drive or folder root shown by its last segment ("C:", "Demo PC").</summary>
    private static string DisplayName(FileTree tree, int id)
    {
        string name = tree.NameOf(id);
        if (name.IndexOfAny(['\\', '/']) < 0) return name;
        string trimmed = name.TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed.Length > 0 ? trimmed : name;
    }

    private static int DayOf(FileTree tree, int id) =>
        id >= 0 && id < tree.ModifiedDay.Count ? tree.ModifiedDay[id] : 0;

    /// <summary>FileInspector.percent: one decimal, "&lt;0.1%" for a sliver, capped at 100.</summary>
    internal static string Percent(double fraction)
    {
        double pct = fraction * 100;
        if (pct > 0 && pct < 0.1) return "<0.1%";
        return ShellFormat.Fixed(Math.Min(100, pct), 1) + "%";
    }

    /// <summary>VisualizeView.lastUsed: "Today", "Yesterday", "3 days ago", "2 weeks ago", "5 months ago", "2 years ago", "—".</summary>
    internal static string LastUsed(int day, int today)
    {
        if (day <= 0) return "—";
        int age = today - day;
        if (age <= 0) return "Today";
        if (age == 1) return "Yesterday";
        if (age < 7) return $"{age} days ago";
        if (age < 60) return $"{age / 7} week{(age / 7 == 1 ? "" : "s")} ago";
        if (age < 730) return $"{age / 30} months ago";
        return $"{age / 365} years ago";
    }

    /// <summary>RelativeAge.long: "Today", "Yesterday", "3 days ago", "1 month ago", "2 years ago", "Unknown".</summary>
    internal static string RelativeAgeLong(int day, int today)
    {
        if (day <= 0) return "Unknown";
        int days = today - day;
        if (days <= 0) return "Today";
        if (days == 1) return "Yesterday";
        if (days < 31) return $"{days} days ago";
        if (days < 365) { int m = days / 30; return m == 1 ? "1 month ago" : $"{m} months ago"; }
        int y = days / 365;
        return y == 1 ? "1 year ago" : $"{y} years ago";
    }

    /// <summary>
    /// CanonicalPath.parentDisplay: the parent folder with the profile as "~" and a trailing
    /// separator, kept to 48 characters as the first 18, "…", and the last 29.
    /// </summary>
    internal static string ParentDisplay(string absolutePath, string? home = null)
    {
        string trimmed = absolutePath.TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(['\\', '/']);
        string parent = cut > 0 ? trimmed[..cut] : cut == 0 ? trimmed[..1] : trimmed;
        char separator = parent.Contains('\\') || !parent.Contains('/') ? '\\' : '/';
        string display = CanonicalPath.DisplayPath(parent, home);
        if (!display.EndsWith('\\') && !display.EndsWith('/')) display += separator;
        const int maxChars = 48, head = 18;
        if (display.Length <= maxChars) return display;
        int tail = Math.Max(12, maxChars - head - 1);
        return display[..head] + "…" + display[^tail..];
    }

    /// <summary>
    /// FileKind.whyLarge, with Windows' places: File Explorer for Finder, Settings › Apps for
    /// Launchpad, "your PC" for "your Mac".
    /// </summary>
    internal static string WhyLarge(FileKind kind) => kind switch
    {
        FileKind.Video => "This is a large video file. High-resolution movies and screen recordings often take many gigabytes.",
        FileKind.DiskImage => "Disk images and installers store a full copy of software or media. Safe to remove after installation if you still have the original download source.",
        FileKind.VirtualDisk => "Virtual machine or container disks grow with images, layers, and volumes. Prefer the app’s own cleanup (e.g. Docker Desktop) over deleting this file directly.",
        FileKind.Archive => "Compressed archives can hold projects, backups, or downloads. Confirm you have extracted anything you need before removing.",
        FileKind.Database => "Application databases hold structured data. Deleting them can corrupt the app’s state.",
        FileKind.DeviceBackup => "Device backups are large by design. Manage them in the Apple Devices app or the backup app rather than deleting raw files.",
        FileKind.Application => "Application files include binaries and resources. Uninstall via Settings › Apps or the vendor’s uninstaller when possible.",
        FileKind.Document => "A large document or export. Review whether you still need this copy.",
        _ => "This file is large relative to others on your PC. Inspect its location and owning app before removing it.",
    };

    // ---- Text fitting ----

    /// <summary>
    /// SwiftUI's minimumScaleFactor: the figure shrinks to fit, down to <paramref name="minimum"/>,
    /// and clips past that (OverviewPage.Shrinkable).
    /// </summary>
    private static FrameworkElement Shrinkable(FrameworkElement content, double minimum)
    {
        var box = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = content,
        };
        content.SizeChanged += (_, _) =>
        {
            if (box.ActualWidth > 0 && content.ActualWidth * minimum > box.ActualWidth)
                box.Stretch = Stretch.None;
        };
        return box;
    }

    /// <summary>
    /// lineLimit(n) with SwiftUI's truncation: the text keeps the most of itself that fits in
    /// <paramref name="lines"/> lines of the width it is given, cut in the middle
    /// (.truncationMode(.middle)) or at the end. Refits when the width changes.
    /// </summary>
    internal static TextBlock Fitted(TextBlock block, int lines, bool middle)
    {
        string full = block.Text;
        double fittedWidth = -1;
        block.HorizontalAlignment = HorizontalAlignment.Stretch;
        block.TextTrimming = TextTrimming.None;
        if (block.ToolTip is null) block.ToolTip = full;
        System.Windows.Automation.AutomationProperties.SetName(block, full);
        block.SizeChanged += (_, e) =>
        {
            double width = e.NewSize.Width;
            if (width <= 0 || Math.Abs(width - fittedWidth) < 0.5) return;
            fittedWidth = width;
            block.Text = FitText(block, full, width, lines, middle);
        };
        return block;
    }

    private static string FitText(TextBlock block, string full, double width, int lines, bool middle)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(block).PixelsPerDip;
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        FormattedText format(string text) => new(text, CultureInfo.CurrentUICulture, block.FlowDirection, typeface,
            block.FontSize, Brushes.Black, pixelsPerDip);
        double lineHeight = format("Xg").Height;
        bool fits(string text)
        {
            var f = format(text);
            if (lines <= 1) return f.WidthIncludingTrailingWhitespace <= width + 0.5;
            f.MaxTextWidth = width;
            return f.Height <= lineHeight * lines + 0.5;
        }
        if (fits(full)) return full;
        string candidate(int keep) => middle
            ? full[..((keep + 1) / 2)] + "…" + full[(full.Length - keep / 2)..]
            : full[..keep].TrimEnd() + "…";
        int lo = 0, hi = full.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (fits(candidate(mid))) lo = mid; else hi = mid - 1;
        }
        return candidate(lo);
    }
}

/// <summary>
/// Which nodes "can be freed": the generally safe Clean up items and everything inside them
/// (Mac: Charts/Freeable.swift). Shared by the map, its table and the inspector so all three
/// agree with Clean up.
/// </summary>
internal sealed class FreeableIndex
{
    /// <summary>Safe target node → its size.</summary>
    private readonly Dictionary<int, long> _targets = [];

    public FreeableIndex(IReadOnlyList<ReviewableTarget> reviewables, long[] totals)
    {
        foreach (var r in reviewables)
        {
            if (!r.IsGenerallySafe) continue;
            foreach (int id in r.NodeIds)
                if (id >= 0 && id < totals.Length) _targets[id] = totals[id];
        }
    }

    public bool IsEmpty => _targets.Count == 0;

    /// <summary>The node is a safe target or inside one.</summary>
    public bool Contains(int id, FileTree tree)
    {
        if (_targets.Count == 0 || id < 0 || id >= tree.Count) return false;
        int cursor = id;
        int hops = 0;
        while (cursor >= 0 && cursor < tree.Count && hops < 128)
        {
            if (_targets.ContainsKey(cursor)) return true;
            cursor = tree.Parent[cursor];
            hops++;
        }
        return false;
    }

    /// <summary>Bytes under <paramref name="id"/> that can be freed: all of it inside a target, else the targets beneath it.</summary>
    public long Bytes(int id, FileTree tree, long[] totals)
    {
        if (_targets.Count == 0 || id < 0 || id >= totals.Length || id >= tree.Count) return 0;
        if (Contains(id, tree)) return totals[id];
        long sum = 0;
        foreach (var (target, size) in _targets)
            if (IsAncestor(id, target, tree)) sum += size;
        return Math.Min(sum, totals[id]);
    }

    /// <summary>Striped on the map and rings: inside a target, or at least 60% freeable.</summary>
    public bool IsMostly(int id, FileTree tree, long[] totals)
    {
        if (id < 0 || id >= totals.Length || id >= tree.Count) return false;
        if (Contains(id, tree)) return true;
        long size = totals[id];
        if (size <= 0) return false;
        return (double)Bytes(id, tree, totals) / size >= 0.6;
    }

    /// <summary><paramref name="ancestor"/> is above <paramref name="id"/> (not equal to it), within 128 hops.</summary>
    public static bool IsAncestor(int ancestor, int id, FileTree tree)
    {
        if (id < 0 || id >= tree.Count) return false;
        int cursor = tree.Parent[id];
        int hops = 0;
        while (cursor >= 0 && cursor < tree.Count && hops < 128)
        {
            if (cursor == ancestor) return true;
            cursor = tree.Parent[cursor];
            hops++;
        }
        return false;
    }
}
