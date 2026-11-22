using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DiskMap.App.Controls;
using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>The audit's app follow-ups: tick boxes and chart menus the keyboard can use, pages that re-read what's on disk.</summary>
public class AuditFollowUpTests
{
    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    /// <summary>Runs what this thread's dispatcher has queued at Background priority and above.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void TickBoxIsAToggleTheKeyboardAndScreenReadersCanUse() => OnStaThread(() =>
    {
        int flips = 0;
        var host = CleanUpPage.TickToggle(new Border(), CleanUpPage.TickState.On, () => flips++);
        AutomationProperties.SetName(host, "Not opened in 6 months");   // callers name the Border they get back
        var toggle = Assert.IsType<ToggleButton>(host.Child);
        Assert.True(toggle.Focusable);   // Tab reaches it; Space clicks a focused ToggleButton
        Assert.True(toggle.IsTabStop);
        Assert.Equal("Not opened in 6 months", AutomationProperties.GetName(toggle));

        static IToggleProvider Peer(ToggleButton b) =>
            (IToggleProvider)new ToggleButtonAutomationPeer(b).GetPattern(PatternInterface.Toggle)!;
        Assert.Equal(ToggleState.On, Peer(toggle).ToggleState);
        Peer(toggle).Toggle();   // the screen reader's toggle; a click and Space end in the same flip
        Assert.Equal(1, flips);

        var off = (ToggleButton)CleanUpPage.TickToggle(new Border(), CleanUpPage.TickState.Off, () => flips++).Child;
        Assert.Equal(ToggleState.Off, Peer(off).ToggleState);
        var mixed = (ToggleButton)CleanUpPage.TickToggle(new Border(), CleanUpPage.TickState.Mixed, () => flips++).Child;
        Assert.Equal(ToggleState.Indeterminate, Peer(mixed).ToggleState);
        Peer(mixed).Toggle();
        Assert.Equal(2, flips);
    });

    /// <summary>History and What changed show the Recycle Bin and saved scans, which the model doesn't hold.</summary>
    [Fact]
    public void APageShowingOutsideStateReloadsEachTimeItIsShownOnce() => OnStaThread(() =>
    {
        int loads = 0;
        var page = new Border();
        ModelEvents.WhileLoaded(page, () => loads++, everyShow: true);
        void Show()
        {
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Pump();
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        }
        Show();
        Assert.Equal(1, loads);   // one reload a show, not one per hook
        Show();   // the model didn't move: reloaded anyway
        Assert.True(loads >= 2);   // (another test moving the shared model meanwhile may add one)
    });

    [Fact]
    public void ChartMenusOpenForTheSelectedSliceFromTheKeyboard()
    {
        Assert.Equal(7, ChartNavigation.MenuTarget(fromKeyboard: true, underPointer: 3, selected: 7, selectedShown: true));
        Assert.Null(ChartNavigation.MenuTarget(true, 3, 7, selectedShown: false));   // selected elsewhere: no menu
        Assert.Equal(3, ChartNavigation.MenuTarget(false, 3, 7, true));               // a right-click: the slice under the pointer
        Assert.Null(ChartNavigation.MenuTarget(false, null, 7, true));
    }

    [Fact]
    public void OpenDownloadsOpensTheFolderWindowsNames()
    {
        Assert.Equal(@"D:\Downloads", CleanDownloadsPage.DownloadsFolder([("desktop", @"C:\Users\you\Desktop"), ("downloads", @"D:\Downloads")]));
        string fallback = System.IO.Path.Combine(UserFolders.Home, "Downloads");
        Assert.Equal(fallback, CleanDownloadsPage.DownloadsFolder([("downloads", "")]));   // the shell named none
        Assert.Equal(fallback, CleanDownloadsPage.DownloadsFolder([]));                     // the demo PC
    }
}
