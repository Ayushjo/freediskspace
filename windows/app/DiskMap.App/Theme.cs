using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DiskMap.App;

/// <summary>
/// The app's palette — colors are swapped in place by <see cref="Apply"/>
/// and controls reference them via DynamicResource (or
/// <see cref="Ui.Brush"/> for code-built UI). Light values follow the
/// Calm design: one warm canvas, ink text, one violet accent and the
/// fixed data palette for charts.
/// </summary>
public static class Theme
{
    /// <summary>True when Windows is in dark mode — what Appearance "System" follows.</summary>
    public static bool IsDark =>
        (Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) as int?) == 0;

    /// <summary>The flag the palette was last applied with.</summary>
    public static bool AppliedDark { get; private set; }

    /// <summary>
    /// WIN-058: dark mode is live — the user's Appearance setting
    /// ("system" follows the OS) decides; an explicit override wins.
    /// </summary>
    public static void Apply() => Apply(AppSettings.Load().WantsDark);

    /// <summary>The palette for <paramref name="dark"/>; under high contrast every key takes its Windows colour.</summary>
    public static void Apply(bool dark)
    {
        AppliedDark = dark;
        var r = Application.Current.Resources;
        bool highContrast = SystemParameters.HighContrast;
        // Replace, don't mutate: BAML-loaded brushes arrive frozen, and
        // DynamicResource re-reads the dictionary entry on swap anyway.
        foreach (var (key, hex) in Palette(dark))
            r[key] = new SolidColorBrush(highContrast && HighContrastRole(key) is { } role
                ? SystemColor(role)
                : (Color)ColorConverter.ConvertFromString(hex));
    }

    /// <summary>
    /// Whether a Windows setting change re-themes the app: Windows switched light/dark while
    /// Appearance follows it, or the colours changed under high contrast (picking another
    /// contrast theme leaves HighContrast on, so nothing else says the colours moved).
    /// </summary>
    internal static bool FollowsWindowsChange(bool followsSystem, bool appliedDark, bool windowsDark,
        bool colorsChanged, bool highContrast) =>
        (followsSystem && appliedDark != windowsDark) || (colorsChanged && highContrast);

    /// <summary>
    /// The Calm palette (docs/DESIGN.md §3.1/§22): one canvas, one raised
    /// step, hairlines, ink text, a single violet accent. Safety colours
    /// are dot+word colours — never fills. Soft values are the spec's
    /// alpha mixes composited over the canvas.
    /// </summary>
    internal static Dictionary<string, string> Palette(bool dark)
    {
        string line = dark ? "#FF26262A" : "#FFE4E3DF";
        return new()
        {
            // Surfaces — one canvas everywhere; raised is for floats only.
            ["AppBackground"] = dark ? "#FF0B0B0C" : "#FFF8F7F4",
            ["AppSidebar"] = dark ? "#FF0B0B0C" : "#FFF8F7F4",
            ["AppCard"] = dark ? "#FF141416" : "#FFFFFFFF",
            ["AppCardBorder"] = line,
            ["AppChrome"] = dark ? "#FF0B0B0C" : "#FFF8F7F4",
            ["AppField"] = dark ? "#FF101012" : "#FFFCFBFA",   // raised @ 55%
            ["AppHover"] = dark ? "#FF161617" : "#FFEEEDEB",   // ink/white @ 4.5%

            // Text — ink, ink2, ink3.
            ["AppForeground"] = dark ? "#FFF2F2F3" : "#FF252B31",
            ["AppSubtle"] = dark ? "#FFA3A3A8" : "#FF66717E",
            ["AppFaint"] = dark ? "#FF76767C" : "#FF949AA2",

            // Accent + selection — one violet.
            ["AppAccent"] = dark ? "#FF9A8BF0" : "#FF7966DA",
            ["AppAccentSoft"] = dark ? "#FF1B1925" : "#FFEAE7F1",   // accent @ 11%
            ["AppNavSelected"] = dark ? "#FF1B1925" : "#FFEAE7F1",
            ["AppRowSelected"] = dark ? "#FF1B1925" : "#FFEAE7F1",

            // Safety — dot+word colours; Bg keys stay as muted tints for
            // the pages that still badge.
            ["AppDanger"] = dark ? "#FFEE7A70" : "#FFC2453D",
            ["AppDangerBg"] = dark ? "#FF241717" : "#FFF2E3E0",
            ["AppSuccess"] = dark ? "#FF6CC495" : "#FF3E8E63",
            ["AppSuccessBg"] = dark ? "#FF151F1B" : "#FFE4EBE4",
            ["AppWarning"] = dark ? "#FFE0A548" : "#FFB7791F",
            ["AppWarningBg"] = dark ? "#FF221C13" : "#FFF1E9DD",
            ["AppInfoBg"] = dark ? "#FF1B1925" : "#FFEAE7F1",
            ["AppInkButton"] = dark ? "#FFF2F2F3" : "#FF252B31",
            ["AppInkButtonFg"] = dark ? "#FF0B0B0C" : "#FFFFFFFF",
            ["AppBarTrack"] = dark ? "#FF26262A" : "#FFE4E3DF",

            // WIN-102: the Mac v0.3.0 redesign palette (Sources/DiskMapApp/Kit/Redesign.swift),
            // same values. Pages move to these as they're rebuilt to the Mac design.
            ["RdPage"] = dark ? "#FF0E0E10" : "#FFFFFFFF",          // behind cards
            ["RdSidebar"] = dark ? "#FF0B0B0C" : "#FFFCFCFB",       // sidebar, receipt column, inspector rail
            ["RdCard"] = dark ? "#FF141417" : "#FFFFFFFF",          // a card on the page
            ["RdSoft"] = dark ? "#FF1E1E22" : "#FFF4F4F1",          // selected nav row, wells
            ["RdSegmentOn"] = dark ? "#FF34343A" : "#FFFFFFFF",     // selected segment
            // The "You can free" panel. The Mac forces the dark appearance on it (colorScheme .dark),
            // so its adaptive night colour resolves to the dark value in both themes.
            ["RdNight"] = "#FF1C1C21",
            ["RdOnNight"] = "#FFFFFFFF",                            // text on the night panel (faded per use)
            ["RdAccentBright"] = dark ? "#FFAA9CF5" : "#FF9B8AF0",  // violet on night, dark figures
            ["RdOnAccent"] = "#FFFFFFFF",
            // Fixed in both appearances, like the Mac's: the receipt is white paper with black
            // print, the boards' hard shadows and scrims are black, a switch's knob is white.
            ["RdPaper"] = "#FFFFFFFF",
            ["RdOnPaper"] = "#FF000000",
            ["RdShadow"] = "#FF000000",
            ["RdKnob"] = "#FFFFFFFF",
            ["RdAccentDeep"] = dark ? "#FFB4A8F8" : "#FF5B48B8",    // "rebuilds" text on hatching
            ["RdAccentWash"] = dark ? "#FF24203A" : "#FFF3F0FD",    // behind hatching, selected rows
            ["RdHover"] = dark ? "#FF19191B" : "#FFF5F5F6",         // ink/white @ 4.5% over the page
            ["RdAccentSoft"] = dark ? "#FF1D1C29" : "#FFF0EEFB",    // accent @ 11% over the page
            ["RdLine"] = line,
            ["RdInk"] = dark ? "#FFF2F2F3" : "#FF252B31",
            ["RdInk2"] = dark ? "#FFA3A3A8" : "#FF66717E",
            ["RdInk3"] = dark ? "#FF76767C" : "#FF949AA2",
            ["RdOnInk"] = dark ? "#FF0B0B0C" : "#FFFFFFFF",

            // Kept for existing references (pages rewritten to the new names).
            ["AppPanel"] = dark ? "#FF0B0B0C" : "#FFF8F7F4",
            ["AppBorder"] = line,
        };
    }

    /// <summary>The Windows colours a high-contrast palette is made of.</summary>
    internal enum ContrastRole { Window, WindowText, GrayText, Highlight, HighlightText }

    /// <summary>
    /// What each palette key becomes under high contrast: surfaces and washes the window colour,
    /// all text, lines, shadows and knobs the window text colour, the accent the highlight. The
    /// night panel and the receipt paper are surfaces like any other there. Null: not a palette key.
    /// </summary>
    internal static ContrastRole? HighContrastRole(string key) => key switch
    {
        "AppBackground" or "AppSidebar" or "AppCard" or "AppChrome" or "AppField" or "AppHover"
            or "AppAccentSoft" or "AppNavSelected" or "AppRowSelected" or "AppDangerBg" or "AppSuccessBg"
            or "AppWarningBg" or "AppInfoBg" or "AppInkButtonFg" or "AppPanel"
            or "RdPage" or "RdSidebar" or "RdCard" or "RdSoft" or "RdSegmentOn" or "RdAccentWash"
            or "RdHover" or "RdAccentSoft" or "RdOnInk" or "RdNight" or "RdPaper" => ContrastRole.Window,
        "AppForeground" or "AppSubtle" or "AppFaint" or "AppDanger" or "AppSuccess" or "AppWarning"
            or "AppInkButton" or "AppCardBorder" or "AppBorder"
            or "RdInk" or "RdInk2" or "RdInk3" or "RdLine"
            or "RdOnNight" or "RdOnPaper" or "RdShadow" or "RdKnob" => ContrastRole.WindowText,
        "AppBarTrack" => ContrastRole.GrayText,
        "AppAccent" or "RdAccentDeep" or "RdAccentBright" => ContrastRole.Highlight,
        "RdOnAccent" => ContrastRole.HighlightText,
        _ => null,
    };

    private static Color SystemColor(ContrastRole role) => role switch
    {
        ContrastRole.Window => SystemColors.WindowColor,
        ContrastRole.WindowText => SystemColors.WindowTextColor,
        ContrastRole.GrayText => SystemColors.GrayTextColor,
        ContrastRole.Highlight => SystemColors.HighlightColor,
        _ => SystemColors.HighlightTextColor,
    };
}
