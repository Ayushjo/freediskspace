using System.IO;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>The app shell's decisions: startup hardening, scan and basket wording, theme, window sizes.</summary>
public class AppShellTests
{
    // ---- Startup ----

    [Fact]
    public void ElevatedRunDropsEveryHarnessSwitchAndNothingElse()
    {
        string[] names = ["DISKMAP_SHOT", "diskmap_home", "DiskMap_State_Dir", "DISKMAP_TRACE", "PATH", "TEMP",
            "XDISKMAP_SHOT", "DISKMAP", "DOTNET_STARTUP_HOOKS"];
        Assert.Equal(["DISKMAP_SHOT", "diskmap_home", "DiskMap_State_Dir", "DISKMAP_TRACE"], App.HarnessVariables(names));
    }

    // ---- Basket wording ----

    [Fact]
    public void BulkAddSaysWhatWentInAndWhatTheBasketRefused()
    {
        Assert.Equal("Added 3 items to the basket — Ctrl+Shift+Delete to review", ScanModel.BulkStageToast(3, 0, 0, []));
        Assert.Equal("Already in the basket", ScanModel.BulkStageToast(0, 2, 0, []));
        // A refusal is not "already in the basket".
        Assert.Equal("2 items can't go in the basket", ScanModel.BulkStageToast(0, 0, 2, []));
        Assert.Equal("1 item can't go in the basket", ScanModel.BulkStageToast(0, 4, 1, []));
        Assert.Equal("Added 2 items · 1 item can't go in the basket", ScanModel.BulkStageToast(2, 0, 1, []));
        Assert.Equal("Added 1 item · skipped 3 risky items (a, b +1)", ScanModel.BulkStageToast(1, 0, 0, ["a", "b", "c"]));
        Assert.Equal("1 risky item — add it one at a time to see why · 2 items can't go in the basket",
            ScanModel.BulkStageToast(0, 0, 2, ["ext4.vhdx"]));
        Assert.Null(ScanModel.BulkStageToast(0, 0, 0, []));
    }

    [Fact]
    public void FreeAfterWaitsForTheRecycleBin()
    {
        string text = MainWindow.FreeAfterText(100_000_000_000, 5_000_000_000);
        Assert.StartsWith(ByteFormat.Format(105_000_000_000), text);
        Assert.EndsWith("free after emptying the bin", text);
    }

    [Fact]
    public void PutBackToastGivesBothCountsAndTheFirstReason()
    {
        static CleanupRecord.Item Item(string name) => new(@"C:\Users\you\" + name, @"C:\$Recycle.Bin\S-1\$R" + name, 10);
        static PutBackReport Report(int restored, params string[] reasons) => new(
            Enumerable.Range(0, restored).Select(i => Item("r" + i)).ToList(),
            reasons.Select((r, i) => (Item("s" + i), r)).ToList());

        Assert.Equal("Put back 1 item", ScanModel.PutBackToast(Report(1)));
        Assert.Equal("Put back 3 items", ScanModel.PutBackToast(Report(3)));
        Assert.Equal("Nothing put back · 2 skipped (No longer in the Recycle Bin)",
            ScanModel.PutBackToast(Report(0, "No longer in the Recycle Bin", "No longer in the Recycle Bin")));
        Assert.Equal("Put back 2 items · 2 skipped (No longer in the Recycle Bin, …)",
            ScanModel.PutBackToast(Report(2, "No longer in the Recycle Bin", @"Something new is at C:\Users\you\s1")));
    }

    // ---- Saved scans ----

    [Fact]
    public void SavedScanRowMustStillMatchTheDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "freedisk-appshell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "movie.mp4");
            File.WriteAllBytes(file, new byte[1234]);
            int day = (int)(new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeSeconds() / 86400);

            Assert.True(ScanModel.MatchesDisk(file, false, 1234, day));
            Assert.True(ScanModel.MatchesDisk(file, false, 1234, 0));        // the scan had no date
            Assert.False(ScanModel.MatchesDisk(file, false, 999, day));      // a different length
            Assert.False(ScanModel.MatchesDisk(file, false, 1234, day - 40)); // rewritten since
            Assert.False(ScanModel.MatchesDisk(Path.Combine(dir, "gone.mp4"), false, 1234, day));
            Assert.True(ScanModel.MatchesDisk(dir, true, 0, 0));
            Assert.False(ScanModel.MatchesDisk(dir, false, 0, 0));           // a folder where the file was
            Assert.False(ScanModel.MatchesDisk(Path.Combine(dir, "gone"), true, 0, 0));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Page bar ----

    [Fact]
    public void RescanTurnsIntoCancelWhileItRuns()
    {
        Assert.Null(MainWindow.ScanButtonTitle(scanned: false, scanning: true));   // the first scan has its own board
        Assert.Equal("Cancel", MainWindow.ScanButtonTitle(scanned: true, scanning: true));
        Assert.Equal("Rescan", MainWindow.ScanButtonTitle(scanned: true, scanning: false));
    }

    // ---- Rebuilds ----

    [Fact]
    public void VersionMovesOnlyWhenTheModelAnnouncesAChange()
    {
        var model = ScanModel.Shared;
        int start = model.Version;
        _ = model.Tree;
        _ = model.HistoryComparison;
        model.SearchQuery = model.SearchQuery;   // the same value is no change
        Assert.Equal(start, model.Version);

        model.ToggleMulti(int.MaxValue);   // StateChanged
        model.ToggleMulti(int.MaxValue);
        Assert.True(model.Version >= start + 2);

        int beforeBasket = model.Version;
        model.RefreshStaged();   // StagedChanged
        Assert.True(model.Version > beforeBasket);

        int beforeProperty = model.Version;
        var basis = model.SizeBasis;
        model.SizeBasis = basis == SizeBasis.Logical ? SizeBasis.Allocated : SizeBasis.Logical;   // PropertyChanged only
        Assert.True(model.Version > beforeProperty);
        model.SizeBasis = basis;
    }

    // ---- Theme ----

    [Fact]
    public void SystemAppearanceFollowsWindowsOnlyWhenLightOrDarkFlipped()
    {
        Assert.True(Theme.FollowsWindowsChange(followsSystem: true, appliedDark: false, windowsDark: true, colorsChanged: false, highContrast: false));
        Assert.False(Theme.FollowsWindowsChange(followsSystem: true, appliedDark: true, windowsDark: true, colorsChanged: false, highContrast: false));
        Assert.False(Theme.FollowsWindowsChange(followsSystem: false, appliedDark: false, windowsDark: true, colorsChanged: false, highContrast: false));
        // Another contrast theme: HighContrast stays on, only the colours say it changed.
        Assert.True(Theme.FollowsWindowsChange(followsSystem: false, appliedDark: false, windowsDark: false, colorsChanged: true, highContrast: true));
        Assert.False(Theme.FollowsWindowsChange(followsSystem: false, appliedDark: false, windowsDark: false, colorsChanged: true, highContrast: false));
    }

    [Fact]
    public void HighContrastMapsEveryPaletteKeyToAWindowsColour()
    {
        Assert.Equal(Theme.Palette(dark: false).Keys.Order(), Theme.Palette(dark: true).Keys.Order());
        // Every key, the night panel and the receipt paper too: pages no longer draw fixed white or black.
        foreach (string key in Theme.Palette(dark: false).Keys) Assert.NotNull(Theme.HighContrastRole(key));
        foreach (string text in new[] { "RdInk", "RdInk2", "RdInk3", "AppForeground", "AppSubtle", "AppFaint", "RdLine",
                     "RdOnNight", "RdOnPaper", "RdShadow", "RdKnob" })
            Assert.Equal(Theme.ContrastRole.WindowText, Theme.HighContrastRole(text));
        foreach (string surface in new[] { "RdPage", "RdCard", "RdSidebar", "RdSoft", "RdAccentWash", "AppBackground", "RdNight", "RdPaper" })
            Assert.Equal(Theme.ContrastRole.Window, Theme.HighContrastRole(surface));
        Assert.Equal(Theme.ContrastRole.Highlight, Theme.HighContrastRole("AppAccent"));
        Assert.Equal(Theme.ContrastRole.Highlight, Theme.HighContrastRole("RdAccentBright"));
        Assert.Equal(Theme.ContrastRole.HighlightText, Theme.HighContrastRole("RdOnAccent"));
        // Ink buttons invert: their label is the window colour on window-text.
        Assert.Equal(Theme.ContrastRole.WindowText, Theme.HighContrastRole("AppInkButton"));
        Assert.Equal(Theme.ContrastRole.Window, Theme.HighContrastRole("AppInkButtonFg"));
    }

    // ---- Keyboard and screen reader ----

    [Fact]
    public void StretchedPressableIsFocusableNamedAndInvokable()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int clicks = 0;
                // The nav rows' and Settings' buttons (they were PlainButton): laid out like a Border.
                var button = Rd.Pressable(new TextBlock { Text = "Overview" }, "Overview", () => clicks++, 7, stretch: true);
                Assert.True(button.Focusable);
                Assert.True(button.IsTabStop);
                Assert.Equal("Overview", AutomationProperties.GetName(button));
                Assert.Equal(System.Windows.HorizontalAlignment.Stretch, button.HorizontalAlignment);
                Assert.Equal(0, button.MinHeight);
                // The screen reader's "invoke"; Enter and Space end in the same Click.
                var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!;
                invoke.Invoke();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                Assert.Equal(1, clicks);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    // ---- Window sizes ----

    [Fact]
    public void WindowsFitA1366By768ScreenAt125Percent()
    {
        // 1366×768 at 125 %, taskbar 48 px: about 1093 × 576 DIPs to work with.
        const double width = 1366 / 1.25, height = (768 - 48) / 1.25;
        Assert.Equal((576, 576), WindowFit.Clamp(820, 600, height));     // main window
        Assert.Equal((width, 880), WindowFit.Clamp(1280, 880, width));
        Assert.Equal((576, 576), WindowFit.Clamp(780, 640, height));     // Settings
        Assert.Equal((1080, 1000), WindowFit.Clamp(1080, 1000, width));
        Assert.Equal((820, 600), WindowFit.Clamp(820, 600, 1400));       // a big screen keeps the design
    }
}
