using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DiskMap.Core;

namespace DiskMap.App;

/// <summary>
/// First launch (and Settings → Your setup): "what do you use this PC
/// for", then "what do you use". The answers tailor Tools &amp; Projects —
/// profiles.json maps each tool to the ecosystems that page shows. Tools
/// found on this PC (a path exists — nothing is read) start ticked. Skip
/// keeps everything visible.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private readonly HashSet<string> _roles;
    private readonly HashSet<string> _tools;
    private readonly HashSet<string> _detected = UserProfiles.Detected();
    private readonly bool _firstTime;
    /// <summary>Roles whose found-on-this-PC tools were already ticked once — unticking sticks.</summary>
    private readonly HashSet<string> _pretickedRoles;

    /// <summary>True when the person finished (the profile changed); false for Skip / close.</summary>
    public bool Saved { get; private set; }

    public OnboardingWindow()
    {
        var settings = AppSettings.Load();
        _roles = settings.Roles.ToHashSet(StringComparer.Ordinal);
        _tools = settings.Tools.ToHashSet(StringComparer.Ordinal);
        _firstTime = !settings.Onboarded;
        _pretickedRoles = settings.Roles.ToHashSet(StringComparer.Ordinal);
        // First launch: closing the window counts as Skip — ask once, not every start.
        Closed += (_, _) => { if (_firstTime && !Saved) MarkOnboarded(); };
        Title = "Welcome to freedisk.space";
        Width = 780;
        Height = 640;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Skip(); e.Handled = true; } };
        ShowRoles();
    }

    // ---- Step 1: roles ----

    private void ShowRoles()
    {
        var body = Frame("1 OF 2", "What do you use this PC for?",
            "Pick everything that fits. freedisk.space looks for what your tools leave behind — and stays away from what they need. You can change this any time in Settings.");
        var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var role in UserProfiles.Roles)
        {
            bool on = _roles.Contains(role.Id);
            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.T(role.Title, 13, FontWeights.SemiBold));
            var blurb = Ui.Faint(role.Blurb, 11.5);
            blurb.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(blurb);
            var row = new DockPanel();
            var icon = Ui.IconTile(Icons.ForRole(role.Id), 34,
                on ? Ui.Brush("AppAccent") : Ui.Brush("AppHover"),
                on ? Ui.Brush("AppInkButtonFg") : Ui.Brush("AppSubtle"), 8);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);
            grid.Children.Add(Toggle(row, on, () =>
            {
                if (!_roles.Remove(role.Id)) _roles.Add(role.Id);
                ShowRoles();
            }, $"{role.Title} — {role.Blurb}"));
        }
        body.Content.Children.Add(grid);
        body.Back.Content = Ui.Button("Skip for now", null, Ui.ButtonStyle.Ghost, Skip);
        var next = Ui.Button(_roles.SetEquals(["everyday"]) ? "Done" : "Next", Icons.Forward, Ui.ButtonStyle.Primary,
            () => { if (_roles.SetEquals(["everyday"])) Finish(); else ShowTools(); });
        next.IsEnabled = _roles.Count > 0;
        body.Next.Content = next;
    }

    // ---- Step 2: tools ----

    private void ShowTools()
    {
        var offered = UserProfiles.ToolsFor(_roles);
        // Found on this PC starts ticked for each newly chosen role — once,
        // so unticking sticks.
        foreach (var role in _roles.Where(r => _pretickedRoles.Add(r)).ToList())
            foreach (var tool in offered.Where(t => t.Roles.Contains(role) && _detected.Contains(t.Id)))
                _tools.Add(tool.Id);

        int found = offered.Count(t => _detected.Contains(t.Id));
        var body = Frame("2 OF 2", "What do you use?",
            found > 0
                ? $"We ticked the {found} we found on this PC. Untick anything you don't use, tick what we missed."
                : "Tick the tools you use — Tools & Projects will show what they keep on disk.");
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in UserProfiles.Roles.Where(r => _roles.Contains(r.Id)))
        {
            // A tool shared by two roles is listed once, under the first.
            var tools = offered.Where(t => t.Roles.Contains(role.Id) && shown.Add(t.Id)).ToList();
            if (tools.Count == 0) continue;
            var label = Ui.MonoLabel(role.Title.ToUpperInvariant(), Ui.Brush("AppSubtle"));
            label.Margin = new Thickness(0, 14, 0, 8);
            body.Content.Children.Add(label);
            var wrap = new WrapPanel();
            foreach (var tool in tools)
                wrap.Children.Add(ToolChip(tool));
            body.Content.Children.Add(wrap);
        }
        if (_roles.Contains("everyday") && _roles.Count == 1 || shown.Count == 0)
        {
            var none = Ui.Subtle("Nothing to pick — freedisk.space will focus on Downloads, photos and videos, caches and duplicates.", 12.5);
            none.TextWrapping = TextWrapping.Wrap;
            body.Content.Children.Add(none);
        }
        body.Back.Content = Ui.Button("Back", Icons.Back, Ui.ButtonStyle.Ghost, ShowRoles);
        body.Next.Content = Ui.Button("Done", null, Ui.ButtonStyle.Primary, Finish);
    }

    private FrameworkElement ToolChip(ProfileTool tool)
    {
        bool on = _tools.Contains(tool.Id);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (on)
        {
            var check = Ui.Glyph(Icons.Check, 12, Ui.Brush("AppAccent"));
            check.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(check);
        }
        row.Children.Add(Ui.T(tool.Title, 12.5, on ? FontWeights.SemiBold : FontWeights.Normal));
        if (_detected.Contains(tool.Id))
        {
            var dot = Ui.Faint("  · on this PC", 11);
            dot.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(dot);
        }
        var chip = Toggle(row, on, () =>
        {
            if (!_tools.Remove(tool.Id)) _tools.Add(tool.Id);
            ShowTools();
        }, tool.Title);
        chip.Padding = new Thickness(10, 6, 10, 6);
        chip.Margin = new Thickness(0, 0, 8, 8);
        chip.MinHeight = 0;
        return chip;
    }

    // ---- Shared chrome ----

    /// <summary>A bordered, clickable toggle card; Space/Enter toggle it too.</summary>
    private static Border Toggle(UIElement content, bool on, Action toggle, string name)
    {
        var card = new Border
        {
            Child = content,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 10, 10),
            MinHeight = 64,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(on ? 1.5 : 1),
            BorderBrush = on ? Ui.Brush("AppAccent") : Ui.Brush("AppCardBorder"),
            Background = on ? Ui.Brush("AppAccentSoft") : Ui.Brush("AppCard"),
            Cursor = Cursors.Hand,
            Focusable = true,
        };
        System.Windows.Automation.AutomationProperties.SetName(card, (on ? "Selected: " : "") + name);
        card.MouseLeftButtonUp += (_, _) => toggle();
        card.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter) { toggle(); e.Handled = true; }
        };
        card.MouseEnter += (_, _) => { if (!on) card.Background = Ui.Brush("AppHover"); };
        card.MouseLeave += (_, _) => { if (!on) card.Background = Ui.Brush("AppCard"); };
        return card;
    }

    private sealed record Chrome(StackPanel Content, ContentControl Back, ContentControl Next);

    /// <summary>Eyebrow, title, subtitle, a scrolling body, and the footer's two slots.</summary>
    private Chrome Frame(string step, string title, string subtitle)
    {
        var head = new StackPanel { Margin = new Thickness(32, 28, 32, 12) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        brand.Children.Add(Ui.BrandIcon(28));
        var name = Ui.T("freedisk.space", 13, FontWeights.SemiBold);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(8, 0, 0, 0);
        brand.Children.Add(name);
        head.Children.Add(brand);
        var eyebrow = Ui.MonoLabel($"YOUR SETUP · {step}", Ui.Brush("AppSubtle"));
        eyebrow.Margin = new Thickness(0, 0, 0, 6);
        head.Children.Add(eyebrow);
        head.Children.Add(Ui.PageTitle(title));
        var sub = Ui.PageSubtitle(subtitle);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 4, 0, 0);
        head.Children.Add(sub);

        var content = new StackPanel { Margin = new Thickness(32, 4, 22, 16) };
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var back = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var next = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
        var footer = new DockPanel { Margin = new Thickness(32, 12, 32, 20) };
        DockPanel.SetDock(back, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        footer.Children.Add(back);
        footer.Children.Add(next);
        var privacy = Ui.Faint("Stays on this PC — freedisk.space never sends anything anywhere.", 11);
        privacy.HorizontalAlignment = HorizontalAlignment.Center;
        privacy.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(privacy);

        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        var footerBorder = new Border
        {
            BorderBrush = Ui.Brush("AppBorder"), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footer,
        };
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        dock.Children.Add(footerBorder);
        dock.Children.Add(scroll);
        Content = dock;
        return new Chrome(content, back, next);
    }

    // ---- Outcomes ----

    private void Finish()
    {
        var settings = AppSettings.Load();
        // Catalog order, and only tools that belong to a chosen role.
        settings.Roles = UserProfiles.Roles.Select(r => r.Id).Where(_roles.Contains).ToList();
        var offered = UserProfiles.ToolsFor(settings.Roles).Select(t => t.Id).ToHashSet();
        settings.Tools = UserProfiles.Tools.Select(t => t.Id)
            .Where(id => _tools.Contains(id) && offered.Contains(id)).ToList();
        settings.Onboarded = true;
        settings.Save();
        Saved = true;
        Close();
    }

    private void Skip() => Close();   // Closed marks a first launch onboarded

    private static void MarkOnboarded()
    {
        var settings = AppSettings.Load();
        settings.Onboarded = true;   // asked once; Settings → Your setup reopens it
        settings.Save();
    }
}
