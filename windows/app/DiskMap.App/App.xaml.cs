using System.IO;
using System.Windows;
using DiskMap.Core.Native;
using Microsoft.Win32;

namespace DiskMap.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The test and render-harness switches are for unelevated runs only. HKCU\Environment needs
        // no administrator, so a variable set there would otherwise move this elevated app's state
        // (the Put Back record too), rebase the profile the classifier protects, or write files.
        if (UnelevatedLaunch.IsElevated)
            foreach (string name in HarnessVariables(Environment.GetEnvironmentVariables().Keys.Cast<string>()))
                Environment.SetEnvironmentVariable(name, null);
        // WIN-055: `diskmap C:\some\folder` launches straight into a scan. A relative folder is
        // read against the folder the app started in, before the working folder moves.
        StartupPath = e.Args is [{ } arg, ..] && Directory.Exists(arg)
            ? Path.GetFullPath(arg) : null;
        // Nothing loads from, or resolves against, the folder the app was started from.
        Environment.CurrentDirectory = Environment.SystemDirectory;

        // WIN-058: the user's Appearance choice decides the Fluent theme
        // and our palette together.
        ApplyTheme(Harness.Dark ?? AppSettings.Load().WantsDark); // a render-harness run can pin the theme
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast)) Retheme();
        };
        SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;
        // A failing click reports instead of taking the app (and the scan) down.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "freedisk.space", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // SystemEvents is static: a handler left behind outlives the dispatcher it posts to.
        SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
        base.OnExit(e);
    }

    /// <summary>argv[0] when it's a real directory — scanned on load.</summary>
    public static string? StartupPath { get; private set; }

    /// <summary>The variables an elevated launch drops: every DISKMAP_ switch (names are case-blind on Windows).</summary>
    internal static List<string> HarnessVariables(IEnumerable<string> names) =>
        names.Where(n => n.StartsWith("DISKMAP_", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The Fluent theme and our palette brushes on top of it.</summary>
    internal static void ApplyTheme(bool dark)
    {
#pragma warning disable WPF0001 // ThemeMode is experimental but stable in practice on .NET 10
        Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        Theme.Apply(dark);
    }

    private static void Retheme()
    {
        ApplyTheme(Harness.Dark ?? AppSettings.Load().WantsDark);
        (Current.MainWindow as MainWindow)?.RebuildForTheme();
    }

    /// <summary>Windows switched light/dark, or its colours (SystemEvents may raise this off the UI thread).</summary>
    private void OnWindowsPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        // Background priority: behind WPF's own handling of the same broadcast (SystemColors).
        Dispatcher.InvokeAsync(() =>
        {
            bool followsSystem = Harness.Dark is null && AppSettings.Load().Appearance == "system";
            if (Theme.FollowsWindowsChange(followsSystem, Theme.AppliedDark, Theme.IsDark,
                    colorsChanged: e.Category == UserPreferenceCategory.Color, SystemParameters.HighContrast))
                Retheme();
        }, System.Windows.Threading.DispatcherPriority.Background);
}
