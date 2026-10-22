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
    /// <summary>True when the OS is in dark mode — kept for when the app ships its dark theme.</summary>
    public static bool IsDark =>
        (Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) as int?) == 0;

    /// <summary>
    /// WIN-058: dark mode is live — the user's Appearance setting
    /// ("system" follows the OS) decides; an explicit override wins.
    /// </summary>
    public static void Apply() => Apply(AppSettings.Load().WantsDark);

    /// <summary>
    /// The Calm palette (docs/DESIGN.md §3.1/§22): one canvas, one raised
    /// step, hairlines, ink text, a single violet accent. Safety colours
    /// are dot+word colours — never fills. Soft values are the spec's
    /// alpha mixes composited over the canvas.
    /// </summary>
    public static void Apply(bool dark)
    {
        var r = Application.Current.Resources;
        bool highContrast = SystemParameters.HighContrast;
        string line = highContrast
            ? (dark ? "#FF4A4A50" : "#FFB9B8B3")
            : (dark ? "#FF26262A" : "#FFE4E3DF");
        string ink2 = highContrast
            ? (dark ? "#FFCDCDD2" : "#FF404852")
            : (dark ? "#FFA3A3A8" : "#FF66717E");
        string ink3 = highContrast
            ? (dark ? "#FFAAAAB0" : "#FF626870")
            : (dark ? "#FF76767C" : "#FF949AA2");

        // Surfaces — one canvas everywhere; raised is for floats only.
        Set(r, "AppBackground", dark ? "#FF0B0B0C" : "#FFF8F7F4");
        Set(r, "AppSidebar", dark ? "#FF0B0B0C" : "#FFF8F7F4");
        Set(r, "AppCard", dark ? "#FF141416" : "#FFFFFFFF");
        Set(r, "AppCardBorder", line);
        Set(r, "AppChrome", dark ? "#FF0B0B0C" : "#FFF8F7F4");
        Set(r, "AppField", dark ? "#FF101012" : "#FFFCFBFA");   // raised @ 55%
        Set(r, "AppHover", dark ? "#FF161617" : "#FFEEEDEB");   // ink/white @ 4.5%

        // Text — ink, ink2, ink3.
        Set(r, "AppForeground", dark ? "#FFF2F2F3" : "#FF252B31");
        Set(r, "AppSubtle", ink2);
        Set(r, "AppFaint", ink3);

        // Accent + selection — one violet.
        Set(r, "AppAccent", dark ? "#FF9A8BF0" : "#FF7966DA");
        Set(r, "AppAccentSoft", dark ? "#FF1B1925" : "#FFEAE7F1");   // accent @ 11%
        Set(r, "AppNavSelected", dark ? "#FF1B1925" : "#FFEAE7F1");
        Set(r, "AppRowSelected", dark ? "#FF1B1925" : "#FFEAE7F1");

        // Safety — dot+word colours; Bg keys stay as muted tints for
        // the pages that still badge.
        Set(r, "AppDanger", dark ? "#FFEE7A70" : "#FFC2453D");
        Set(r, "AppDangerBg", dark ? "#FF241717" : "#FFF2E3E0");
        Set(r, "AppSuccess", dark ? "#FF6CC495" : "#FF3E8E63");
        Set(r, "AppSuccessBg", dark ? "#FF151F1B" : "#FFE4EBE4");
        Set(r, "AppWarning", dark ? "#FFE0A548" : "#FFB7791F");
        Set(r, "AppWarningBg", dark ? "#FF221C13" : "#FFF1E9DD");
        Set(r, "AppInfoBg", dark ? "#FF1B1925" : "#FFEAE7F1");
        Set(r, "AppInkButton", dark ? "#FFF2F2F3" : "#FF252B31");
        Set(r, "AppInkButtonFg", dark ? "#FF0B0B0C" : "#FFFFFFFF");
        Set(r, "AppBarTrack", dark ? "#FF26262A" : "#FFE4E3DF");

        // WIN-102: the Mac v0.3.0 redesign palette (Sources/DiskMapApp/Kit/Redesign.swift),
        // same values. Pages move to these as they're rebuilt to the Mac design.
        Set(r, "RdPage", dark ? "#FF0E0E10" : "#FFFFFFFF");          // behind cards
        Set(r, "RdSidebar", dark ? "#FF0B0B0C" : "#FFFCFCFB");       // sidebar, receipt column, inspector rail
        Set(r, "RdCard", dark ? "#FF141417" : "#FFFFFFFF");          // a card on the page
        Set(r, "RdSoft", dark ? "#FF1E1E22" : "#FFF4F4F1");          // selected nav row, wells
        Set(r, "RdSegmentOn", dark ? "#FF34343A" : "#FFFFFFFF");     // selected segment
        Set(r, "RdNight", dark ? "#FF1C1C21" : "#FF101113");         // the dark "You can free" panel
        Set(r, "RdAccentBright", dark ? "#FFAA9CF5" : "#FF9B8AF0");  // violet on night, dark figures
        Set(r, "RdOnAccent", "#FFFFFFFF");
        Set(r, "RdAccentDeep", dark ? "#FFB4A8F8" : "#FF5B48B8");    // "rebuilds" text on hatching
        Set(r, "RdAccentWash", dark ? "#FF24203A" : "#FFF3F0FD");    // behind hatching, selected rows
        Set(r, "RdHover", dark ? "#FF19191B" : "#FFF5F5F6");         // ink/white @ 4.5% over the page
        Set(r, "RdAccentSoft", dark ? "#FF1D1C29" : "#FFF0EEFB");    // accent @ 11% over the page
        Set(r, "RdLine", line);
        Set(r, "RdInk", dark ? "#FFF2F2F3" : "#FF252B31");
        Set(r, "RdInk2", ink2);
        Set(r, "RdInk3", ink3);
        Set(r, "RdOnInk", dark ? "#FF0B0B0C" : "#FFFFFFFF");

        // Kept for existing references (pages rewritten to the new names).
        Set(r, "AppPanel", dark ? "#FF0B0B0C" : "#FFF8F7F4");
        Set(r, "AppBorder", line);
    }

    private static void Set(ResourceDictionary r, string key, string hex)
    {
        // Replace, don't mutate: BAML-loaded brushes arrive frozen, and
        // DynamicResource re-reads the dictionary entry on swap anyway.
        r[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
