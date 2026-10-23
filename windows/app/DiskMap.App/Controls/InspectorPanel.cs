using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App.Controls;

/// <summary>
/// The right-hand inspector — the decision panel from the reference
/// design: identity, what's inside, why it's large, can it be removed,
/// and the action stack. Shows the selected node, or the zoomed folder
/// when nothing is selected.
/// </summary>
public sealed class InspectorPanel : UserControl
{
    private readonly StackPanel _root = new() { Margin = new Thickness(16) };
    private string _tab = "Overview";

    public InspectorPanel()
    {
        Content = new ScrollViewer
        {
            Content = _root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        ModelEvents.WhileLoaded(this, Refresh);
    }

    private ScanModel Model => ScanModel.Shared;

    private void Refresh()
    {
        _root.Children.Clear();
        if (Model.Tree is not { } tree || Model.Totals.Length != tree.Count)
        {
            _root.Children.Add(Ui.EmptyState(Icons.Folder, "Nothing to inspect",
                "Scan a folder, then select items to see details, safety and actions here.",
                allowDusty: false));
            return;
        }
        int id = Model.InspectedNode;
        var totals = Model.Totals;
        bool isDir = tree.IsDirectory[id];
        string name = tree.NameOf(id);
        if (id == 0 && Model.RootPath is { } rp)
        {
            string trimmed = rp.TrimEnd('\\', '/');
            name = trimmed.Contains('\\')
                ? trimmed[(trimmed.LastIndexOf('\\') + 1)..]
                : trimmed;
        }
        string kind = FileTypes.KindOf(tree, id);
        long size = totals[id];

        // Header: icon tile, name, size, kind line.
        var kindBrush = Ui.KindColor(kind);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var tile = Ui.IconTile(Icons.ForKind(kind), 40,
            isDir ? Ui.Brush("AppHover") : Ui.Tint(((SolidColorBrush)kindBrush).Color, 41),
            isDir ? Ui.Brush("AppSubtle") : kindBrush, 9);
        DockPanel.SetDock(tile, Dock.Left);
        var headText = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        headText.Children.Add(Ui.T(name, 15, FontWeights.SemiBold, wrap: true));
        headText.Children.Add(Ui.Mono(ByteFormat.Format(size), 28, FontWeights.SemiBold));
        string kindLine = isDir ? "Folder" : FileTypes.LabelOf(kind);
        if (!isDir && tree.NameOf(id).LastIndexOf('.') is int dot && dot >= 0 && dot < tree.NameOf(id).Length - 1)
            kindLine += $" · {tree.NameOf(id)[(dot + 1)..].ToUpperInvariant()}";
        headText.Children.Add(Ui.Subtle(kindLine, 11.5));
        header.Children.Add(tile);
        header.Children.Add(headText);
        _root.Children.Add(header);

        // Tabs — text tabs with an accent underline on the selected one.
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var label in new[] { "Overview", "Contents" })
        {
            bool selected = _tab == label;
            var tabContent = new StackPanel();
            tabContent.Children.Add(Ui.T(label, 13, selected ? FontWeights.Medium : FontWeights.Normal,
                selected ? Ui.Brush("AppForeground") : Ui.Brush("AppSubtle")));
            tabContent.Children.Add(new Border
            {
                Height = 1.5, Margin = new Thickness(0, 4, 0, 0),
                Background = selected ? Ui.Brush("AppForeground") : Brushes.Transparent,
            });
            var tab = new Border
            {
                Child = tabContent,
                Margin = new Thickness(0, 0, 18, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
            };
            string captured = label;
            tab.MouseLeftButtonDown += (_, _) => { _tab = captured; Refresh(); };
            tabs.Children.Add(tab);
        }
        _root.Children.Add(tabs);

        if (_tab == "Contents") ShowContents(tree, totals, id);
        else ShowOverview(tree, totals, id);
    }

    // ---- Overview tab: metadata + what's inside + actions ----

    private void ShowOverview(FileTree tree, long[] totals, int id)
    {
        bool isDir = tree.IsDirectory[id];
        string path = Model.PathOf(id);

        var location = Ui.T(Model.DisplayPath(id), 12, FontWeights.Medium);
        location.TextTrimming = TextTrimming.CharacterEllipsis;
        location.MaxWidth = 200;
        location.ToolTip = path;
        _root.Children.Add(Ui.MetaRow("Location", location));

        // Size row: value + % of used + tiny bar.
        var sizePanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        sizePanel.Children.Add(Ui.T(ByteFormat.Format(totals[id]), 12, FontWeights.Medium));
        if (Model.Volume is { } vol && vol.UsedBytes > 0)
        {
            double pct = 100.0 * totals[id] / vol.UsedBytes;
            var sub = Ui.Faint($"{pct:0.0}% of used storage");
            sub.HorizontalAlignment = HorizontalAlignment.Right;
            sizePanel.Children.Add(sub);
            var bar = Ui.Bar(pct / 100.0, Ui.Brush("AppAccent"), 4, 90);
            bar.Margin = new Thickness(0, 3, 0, 0);
            sizePanel.Children.Add(bar);
        }
        _root.Children.Add(Ui.MetaRow("Size", sizePanel));

        // WIN-047: logical vs on-disk — compression/sparse savings show
        // as "Compressed by X" (NTFS compression, OneDrive evictions).
        if (Model.DualTotals(id) is { } dual && dual.Logical != dual.Allocated)
        {
            long compressedBy = dual.Logical - dual.Allocated;
            var diskPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            diskPanel.Children.Add(Ui.T(ByteFormat.Format(dual.Allocated), 12, FontWeights.Medium));
            diskPanel.Children.Add(Ui.Faint(compressedBy > 0
                ? $"compressed by {ByteFormat.Format(compressedBy)}"
                : $"metadata overhead {ByteFormat.Format(-compressedBy)}"));
            _root.Children.Add(Ui.MetaRow("On disk", diskPanel));
        }

        // WIN-066: a file in a clone family — what it shares and with
        // how many copies (the macOS inspector's "N other copies" row).
        if (!isDir && tree.SharingInfoOf(id) is { } sharing)
        {
            var clonePanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            clonePanel.Children.Add(Ui.T(ByteFormat.Format(sharing.SharedBytes), 12, FontWeights.Medium));
            clonePanel.Children.Add(Ui.Faint(sharing.OtherCopies > 0
                ? $"shared with {sharing.OtherCopies} other {(sharing.OtherCopies == 1 ? "copy" : "copies")}"
                : "shared with copies outside this scan"));
            _root.Children.Add(Ui.MetaRow("Block clone", clonePanel));
        }

        if (id < Model.Counts.Files.Length)
        {
            int files = Model.Counts.Files[id];
            int folders = Model.Counts.Folders[id];
            string items = isDir ? $"{folders:N0} folders, {files:N0} files" : "1 file";
            _root.Children.Add(Ui.MetaRow("Items", items));
        }

        var times = Ui.FileTimes(path);
        var modified = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        // The scan keeps no date for some folders (the root) — the disk does.
        modified.Children.Add(Ui.T(tree.ModifiedDay[id] <= 0 && times?.Modified is { } m0
            ? RelativeDate(m0) : Ui.RelativeDay(tree.ModifiedDay[id]), 12, FontWeights.Medium));
        modified.Children.Add(Ui.Faint(times?.Modified is { } m ? m.ToString("ddd, d MMM yyyy") : Ui.AbsoluteDay(tree.ModifiedDay[id])));
        _root.Children.Add(Ui.MetaRow("Modified", modified));
        if (times?.Created is { } created)
        {
            var createdPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            createdPanel.Children.Add(Ui.T(RelativeDate(created), 12, FontWeights.Medium));
            createdPanel.Children.Add(Ui.Faint(created.ToString("ddd, d MMM yyyy")));
            _root.Children.Add(Ui.MetaRow("Created", createdPanel));
        }

        if (isDir) AddWhatsInside(tree, totals, id);
        AddExplanation(tree, totals, id);
        AddActions(tree, totals, id);
    }

    private static string RelativeDate(DateTime d)
    {
        var days = (DateTime.Now.Date - d.Date).Days;
        return days switch
        {
            <= 0 => "today", 1 => "1 day ago",
            < 7 => $"{days} days ago", < 14 => "1 week ago",
            < 30 => $"{days / 7} weeks ago", < 60 => "1 month ago",
            < 365 => $"{days / 30} months ago", < 730 => "1 year ago",
            _ => $"{days / 365} years ago",
        };
    }

    /// <summary>"What's inside?" — dominant type shares for a folder.</summary>
    private void AddWhatsInside(FileTree tree, long[] totals, int id)
    {
        var breakdown = FileTypes.TypeBreakdown(tree, totals, id)
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ToList();
        if (breakdown.Count == 0) return;
        long total = breakdown.Sum(kv => kv.Value);
        var label = Ui.SectionLabel("What's inside?");
        label.Margin = new Thickness(0, 14, 0, 8);
        _root.Children.Add(label);
        var parts = breakdown
            .Select(kv => (FileTypes.LabelOf(kv.Key), kv.Value, Ui.Hex(FileTypes.TileColorOf(kv.Key))))
            .ToList();
        _root.Children.Add(Ui.TypeBarWithLegend(parts, total));
    }

    /// <summary>"Why is it large?" + safety + consequences — the explain layer.</summary>
    private void AddExplanation(FileTree tree, long[] totals, int id)
    {
        bool isDir = tree.IsDirectory[id];
        string name = isDir ? tree.NameOf(id) : tree.NameOf(id);
        if (id == 0 && Model.RootPath is { } rp) name = rp;

        // Why is it large? — deterministic: dominant contributor.
        string why;
        if (isDir)
        {
            var children = tree.ChildrenOf(id, totals).OrderByDescending(c => c.Size).ToList();
            if (children.Count > 0 && totals[id] > 0)
            {
                var top = children[0];
                double share = 100.0 * top.Size / totals[id];
                why = share >= 50
                    ? $"Most of the space is {tree.NameOf(top.Id)} — {share:0}% of this folder."
                    : $"Largest inside: {tree.NameOf(top.Id)} ({ByteFormat.Format(top.Size)}), plus {children.Count - 1:N0} more items.";
            }
            else
            {
                why = "This folder is empty or its contents couldn't be read.";
            }
        }
        else
        {
            string kind = FileTypes.KindOfFile(tree.NameOf(id));
            why = kind switch
            {
                "video" => "This is a large video file. High-resolution movies and recordings take significant space.",
                "diskImage" => "This is a disk image. Virtual disks and installers often run to several gigabytes.",
                "archive" => "This is a large archive. Compressed backups and downloads often sit forgotten.",
                "audio" => "This is a large audio file.",
                "image" => "This is a large image file — likely a raw photo or layered design file.",
                _ => totals[id] > 1_000_000_000
                    ? "This file is over a gigabyte — one of the larger items in the scan."
                    : "This file contributes to the folder's total.",
            };
        }
        var note = new StackPanel { Margin = new Thickness(0, 8, 0, 12) };
        note.Children.Add(Ui.MonoLabel("Why it's large", Ui.Brush("AppSubtle")));
        var noteText = Ui.Subtle(why, 12);
        noteText.TextWrapping = TextWrapping.Wrap;
        noteText.Margin = new Thickness(0, 4, 0, 0);
        note.Children.Add(noteText);
        _root.Children.Add(note);

        // Can I remove it? + What happens?
        string path = Model.PathOf(id);
        bool regenerable = Model.QuickWins?.Any(h => h.Id == id) == true;
        var (verdict, detail, safe) = EvaluateRemovability(id, path, regenerable);
        _root.Children.Add(SafetyBlock("Can I remove it?", verdict, detail, safe));
    }

    /// <summary>The same verdict the lists, menus and Cleanup use — never a friendlier one.</summary>
    private (string Verdict, string Detail, bool Safe) EvaluateRemovability(int id, string path, bool regenerable)
    {
        var verdict = Model.VerdictOf(id);
        if (id == 0)
            return ("Review first", "This is the scanned folder. Remove items inside it rather than the folder itself.", false);
        if (verdict.Advice == CleanupAdvice.Never || CleanupQueue.IsExcludedPath(path))
            return ("Protected — can't be removed here",
                verdict.Instead ?? verdict.Note ?? "freedisk.space never offers this location for cleanup.", false);
        if (verdict.Advice == CleanupAdvice.Warn)
            return ("Risky — removing it breaks something",
                string.Join(" ", new[] { verdict.Note, verdict.Instead }.Where(t => t is not null)), false);
        if (regenerable)
            return ("Yes — safe to review",
                "Regenerable data (build output or a cache) your tools recreate when needed — you may need to reinstall dependencies or rebuild.",
                true);
        return ("Review first",
            verdict.Note ?? "Your data, not a known cache. Check what's inside first — items go to the Recycle Bin and stay recoverable until you empty it.",
            false);
    }

    private static Border SafetyBlock(string title, string verdict, string detail, bool safe)
    {
        var text = new StackPanel();
        text.Children.Add(Ui.T(title, 12, FontWeights.Medium));
        var safety = Ui.SafetyLabel(
            safe ? Ui.Brush("AppSuccess") : Ui.Brush("AppWarning"), verdict, 11.5);
        safety.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(safety);
        text.Children.Add(new TextBlock
        {
            Text = detail, FontSize = 11.5, Foreground = Ui.Brush("AppSubtle"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        return new Border { Margin = new Thickness(0, 0, 0, 10), Child = text };
    }

    // ---- Actions ----

    private void AddActions(FileTree tree, long[] totals, int id)
    {
        bool isDir = tree.IsDirectory[id];
        string path = Model.PathOf(id);

        var label = Ui.SectionLabel("Actions");
        label.Margin = new Thickness(0, 8, 0, 8);
        _root.Children.Add(label);

        var stack = new StackPanel();
        bool inCleanup = Model.StagedItems.Any(item =>
            string.Equals(item.Path, path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        var verdict = Model.VerdictOf(id);
        bool canStage = id != 0 && !CleanupQueue.IsExcludedPath(path) && verdict.Advice != CleanupAdvice.Never;
        var cleanup = inCleanup
            ? Ui.Button("In Cleanup ✓", Icons.Cleanup, Ui.ButtonStyle.Outline,
                () => Model.OpenBasket())
            : Ui.Button(verdict.Advice == CleanupAdvice.Warn ? "Add to Cleanup…" : "Add to Cleanup",
                Icons.Cleanup, Ui.ButtonStyle.Primary, () => Model.Stage(id, "from inspector"));
        cleanup.IsEnabled = inCleanup || canStage;
        if (!canStage && !inCleanup)
        {
            cleanup.ToolTip = verdict.Instead ?? verdict.Note ?? "Protected — can't be added to Cleanup.";
            ToolTipService.SetShowOnDisabled(cleanup, true);
        }
        stack.Children.Add(cleanup);
        stack.Children.Add(new Border { Height = 8 });
        stack.Children.Add(Ui.Button("Reveal in Explorer", Icons.Open, Ui.ButtonStyle.Outline,
            () => Explorer.Reveal(path)));
        stack.Children.Add(new Border { Height = 8 });
        stack.Children.Add(Ui.Button("Copy Path", Icons.Copy, Ui.ButtonStyle.Outline,
            () => Clipboard.SetText(path)));
        if (isDir)
        {
            stack.Children.Add(new Border { Height = 8 });
            stack.Children.Add(Ui.Button("Show in Visualize", Icons.Visualize, Ui.ButtonStyle.Outline,
                () => Model.Visualize(id)));
        }
        _root.Children.Add(stack);
    }

    // ---- Contents tab ----

    private void ShowContents(FileTree tree, long[] totals, int id)
    {
        if (!tree.IsDirectory[id])
        {
            _root.Children.Add(Ui.Subtle("Files have no contents."));
            return;
        }
        var children = tree.ChildrenOf(id, totals).OrderByDescending(c => c.Size).Take(100).ToList();
        if (children.Count == 0)
        {
            _root.Children.Add(Ui.Subtle("This folder is empty or unreadable."));
            return;
        }
        foreach (var (childId, size) in children)
        {
            string kind = FileTypes.KindOf(tree, childId);
            var cat = FileTypes.Categories.FirstOrDefault(c => c.Id == kind);
            bool isDir = tree.IsDirectory[childId];
            var row = Ui.NameCell(
                Icons.ForKind(kind),
                isDir ? Ui.Brush("AppAccentSoft") : Ui.Hex(cat?.BadgeBackground ?? "#F1F5F9"),
                isDir ? Ui.Brush("AppAccent") : Ui.Hex(cat?.BadgeForeground ?? "#475569"),
                tree.NameOf(childId), ByteFormat.Format(size), 26);
            row.Margin = new Thickness(0, 4, 0, 4);
            row.Cursor = System.Windows.Input.Cursors.Hand;
            int captured = childId;
            row.MouseLeftButtonDown += (_, _) => Model.Select(captured);
            _root.Children.Add(row);
        }
    }
}
