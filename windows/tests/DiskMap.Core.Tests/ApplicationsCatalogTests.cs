using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// The Applications page's logic, ported from the Mac (ApplicationsCatalog.swift, AppsView.swift):
/// "not opened in 6 months", "Last opened" wording, the three sorts, the search, what is ticked
/// for you and what uninstalling frees — plus the Windows parts: leftover kinds by place, the
/// program's own folder never offered as a leftover, and launch dates from UserAssist.
/// </summary>
public class ApplicationsCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static ApplicationEntry Entry(string name, long app = 0, long data = 0, int? daysAgo = null,
        string? publisher = null) => new()
    {
        Id = "reg:" + name,
        Name = name,
        Publisher = publisher,
        AppBytes = app,
        Related = data > 0 ? [new ApplicationRelatedItem(ApplicationRelatedKind.Caches, "/x/" + name, data)] : [],
        LastUsed = daysAgo is { } d ? Now.AddDays(-d) : null,
    };

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(6, "6 days ago")]
    [InlineData(7, "Last week")]
    [InlineData(13, "Last week")]
    [InlineData(14, "2 weeks ago")]
    [InlineData(59, "8 weeks ago")]
    [InlineData(60, "2 months ago")]
    [InlineData(364, "12 months ago")]
    [InlineData(365, "1 year ago")]
    [InlineData(800, "2 years ago")]
    public void LastOpenedReadsLikeTheMac(int? daysAgo, string expected)
    {
        DateTimeOffset? date = daysAgo is { } d ? Now.AddDays(-d) : null;
        Assert.Equal(expected, ApplicationsCatalog.LastOpenedText(date, Now));
    }

    [Fact]
    public void NotOpenedInSixMonthsIs180WholeDaysAndUnknownIsNever()
    {
        Assert.False(ApplicationsCatalog.IsNotRecentlyUsed(null, Now));
        Assert.False(ApplicationsCatalog.IsNotRecentlyUsed(Now.AddDays(-179), Now));
        Assert.False(ApplicationsCatalog.IsNotRecentlyUsed(Now.AddDays(-180).AddMinutes(1), Now));
        Assert.True(ApplicationsCatalog.IsNotRecentlyUsed(Now.AddDays(-180), Now));
        Assert.True(Entry("Old", daysAgo: 400).IsNotRecentlyUsed(Now));
    }

    [Fact]
    public void TotalsIncludeTheDataTheAppKeeps()
    {
        var app = Entry("Spotify", app: 300, data: 700);
        Assert.Equal(700, app.RelatedBytes);
        Assert.Equal(1000, app.TotalBytes);
        Assert.True(app.CanStageForCleanup);
        Assert.False(Entry("Bare", app: 5).CanStageForCleanup);
    }

    [Fact]
    public void SortsBySizeLastOpenedAndName()
    {
        var a = Entry("App 10", app: 10, daysAgo: 3);
        var b = Entry("app 9", app: 30, daysAgo: 300);
        var c = Entry("Beta", app: 20);
        var d = Entry("Alpha", app: 5, data: 40, daysAgo: 30);
        var all = new[] { a, b, c, d };
        Assert.Equal(["Alpha", "app 9", "Beta", "App 10"],
            ApplicationsCatalog.Sorted(all, ApplicationSort.SizeDesc).Select(x => x.Name));
        Assert.Equal(["App 10", "Beta", "app 9", "Alpha"],
            ApplicationsCatalog.Sorted(all, ApplicationSort.SizeAsc).Select(x => x.Name));
        // Longest unused first; never-recorded last.
        Assert.Equal(["app 9", "Alpha", "App 10", "Beta"],
            ApplicationsCatalog.Sorted(all, ApplicationSort.LastUsedAsc).Select(x => x.Name));
        Assert.Equal(["App 10", "Alpha", "app 9", "Beta"],
            ApplicationsCatalog.Sorted(all, ApplicationSort.LastUsedDesc).Select(x => x.Name));
        // Finder order: case-insensitive, digits by value.
        Assert.Equal(["Alpha", "app 9", "App 10", "Beta"],
            ApplicationsCatalog.Sorted(all, ApplicationSort.NameAsc).Select(x => x.Name));
    }

    [Fact]
    public void VisibleFiltersStaleAndSearchesNameAndPublisher()
    {
        var all = new[]
        {
            Entry("Steam", app: 1, daysAgo: 400, publisher: "Valve Corporation"),
            Entry("Zoom Workplace", app: 2, daysAgo: 3, publisher: "Zoom Communications, Inc."),
            Entry("Notepad++", app: 3, publisher: "Notepad++ Team"),
        };
        Assert.Equal(["Steam"], ApplicationsCatalog.Visible(all, true, "", ApplicationSort.SizeDesc, Now).Select(x => x.Name));
        Assert.Equal(["Steam"], ApplicationsCatalog.Visible(all, false, "  VALVE ", ApplicationSort.SizeDesc, Now).Select(x => x.Name));
        Assert.Equal(["Zoom Workplace"], ApplicationsCatalog.Visible(all, false, "zoom", ApplicationSort.NameAsc, Now).Select(x => x.Name));
        Assert.Empty(ApplicationsCatalog.Visible(all, true, "zoom", ApplicationSort.NameAsc, Now));
    }

    [Fact]
    public void OnlyWhatRebuildsIsTickedAndUninstallFreesTheAppPlusTicked()
    {
        Assert.True(ApplicationsCatalog.TickedByDefault(ApplicationRelatedKind.Caches));
        Assert.True(ApplicationsCatalog.TickedByDefault(ApplicationRelatedKind.Logs));
        foreach (var kind in new[] { ApplicationRelatedKind.RoamingData, ApplicationRelatedKind.LocalData,
                     ApplicationRelatedKind.ProgramData, ApplicationRelatedKind.Documents,
                     ApplicationRelatedKind.Programs, ApplicationRelatedKind.Other })
            Assert.False(ApplicationsCatalog.TickedByDefault(kind));

        var app = Entry("Spotify", app: 100) with
        {
            Related =
            [
                new ApplicationRelatedItem(ApplicationRelatedKind.Caches, "/c", 40),
                new ApplicationRelatedItem(ApplicationRelatedKind.RoamingData, "/r", 500),
            ],
        };
        Assert.Equal(140, ApplicationsCatalog.UninstallBytes(app, r => ApplicationsCatalog.TickedByDefault(r.Kind)));
        Assert.Equal(640, ApplicationsCatalog.UninstallBytes(app, _ => true));
    }

    private static readonly ApplicationsCatalog.Places Places = new(
        RoamingAppData: "C:/Users/me/AppData/Roaming",
        LocalAppData: "C:/Users/me/AppData/Local",
        ProgramData: "C:/ProgramData",
        Documents: "C:/Users/me/Documents",
        Programs: "C:/Users/me/AppData/Local/Programs");

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Roaming\Spotify", ApplicationRelatedKind.RoamingData)]
    [InlineData(@"C:\Users\me\AppData\Local\Spotify", ApplicationRelatedKind.LocalData)]
    [InlineData(@"C:\Users\me\AppData\Local\Spotify Cache", ApplicationRelatedKind.Caches)]
    [InlineData(@"C:\Users\me\AppData\Roaming\Spotify.log", ApplicationRelatedKind.Logs)]
    [InlineData(@"C:\ProgramData\Spotify", ApplicationRelatedKind.ProgramData)]
    [InlineData(@"C:\Users\me\Documents\Spotify", ApplicationRelatedKind.Documents)]
    [InlineData(@"C:\Users\me\Documents\Spotify Logs", ApplicationRelatedKind.Documents)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Spotify", ApplicationRelatedKind.Programs)]
    [InlineData(@"D:\Elsewhere\Spotify", ApplicationRelatedKind.Other)]
    public void LeftoversAreNamedByWhereTheySit(string path, ApplicationRelatedKind expected)
    {
        Assert.Equal(expected, ApplicationsCatalog.RelatedKind(path, Places));
    }

    [Fact]
    public void TheProgramsOwnFolderIsNeverALeftover()
    {
        var items = ApplicationsCatalog.RelatedItems(
        [
            (@"C:\Users\me\AppData\Local\Discord", 900),                 // holds the install (Squirrel)
            (@"C:\Users\me\AppData\Local\Discord\app-1.0\cache", 50),    // inside the install
            (@"C:\Users\me\AppData\Roaming\Discord", 300),
            (@"C:\Users\me\AppData\Roaming\discord.log", 2),
        ], @"C:/Users/me/AppData/Local/Discord/app-1.0", Places);
        Assert.Equal([@"C:\Users\me\AppData\Roaming\Discord", @"C:\Users\me\AppData\Roaming\discord.log"],
            items.Select(i => i.Path));
        Assert.Equal([ApplicationRelatedKind.RoamingData, ApplicationRelatedKind.Logs], items.Select(i => i.Kind));

        // Without an install folder every match stays a leftover.
        Assert.Equal(4, ApplicationsCatalog.RelatedItems([("/a", 1), ("/b", 2), ("/c", 3), ("/d", 4)], "", Places).Count);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(@"C:\", false)]
    [InlineData("C:", false)]
    [InlineData(@"C:\Program Files", false)]
    [InlineData(@"C:\Program Files\", false)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\Program Files\Vendor\App", true)]
    [InlineData(@"""C:\Program Files\Vendor\App""", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\App", true)]
    public void BroadInstallFoldersAreNotUsed(string? path, bool usable)
    {
        string[] broad = [@"C:\Program Files", @"C:\Users\me\AppData\Local", @"C:/Users/me/AppData/Local/Programs"];
        Assert.Equal(usable, ApplicationsCatalog.IsUsableInstallPath(path, broad));
    }

    [Fact]
    public void AppBytesMeasureTheInstallFolderElseTheRegistryEstimate()
    {
        string root = Path.Combine(Path.GetTempPath(), "dm-apps-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "Vendor", "App");
        Directory.CreateDirectory(install);
        try
        {
            File.WriteAllBytes(Path.Combine(install, "app.exe"), new byte[5000]);
            var measured = new InstalledApp("App", null, install, "App", EstimatedBytes: 1);
            Assert.Equal(8192, ApplicationsCatalog.AppBytes(measured, []));   // two 4K clusters

            var broad = measured with { InstallLocation = root };
            Assert.Equal(1, ApplicationsCatalog.AppBytes(broad, [install]));   // folder holds a broad one: estimate
            Assert.Equal(0, ApplicationsCatalog.AppBytes(measured with { InstallLocation = null, EstimatedBytes = null }, []));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void InstallDateComesFromTheRegistry()
    {
        var date = ApplicationsCatalog.ParseInstallDate("20250914");
        Assert.NotNull(date);
        Assert.Equal((2025, 9, 14), (date!.Value.Year, date.Value.Month, date.Value.Day));
        Assert.Null(ApplicationsCatalog.ParseInstallDate("14/09/2025"));
        Assert.Null(ApplicationsCatalog.ParseInstallDate(null));
        Assert.Null(ApplicationsCatalog.ParseInstallDate("19000101"));
    }

    [Fact]
    public void StubsKeepRegistryFactsAndWaitForSizes()
    {
        var app = new InstalledApp("Spotify", "Spotify AB", @"C:\Program Files", "Spotify",
            UninstallString: "\"C:\\x\\uninstall.exe\"", Version: "1.2.3", InstallDate: "20260102");
        var stub = ApplicationsCatalog.Stub(app, Now, [@"C:\Program Files"]);
        Assert.Equal("reg:Spotify", stub.Id);
        Assert.Equal("", stub.InstallPath);   // too broad to be the program's folder
        Assert.True(stub.SizePending);
        Assert.Equal("1.2.3", stub.Version);
        Assert.Equal(ApplicationSource.Other, stub.Source);
        Assert.Equal("Other", ApplicationsCatalog.SourceTitle(stub.Source));
        Assert.Equal("\"C:\\x\\uninstall.exe\"", stub.UninstallCommand);
        var store = ApplicationsCatalog.Stub(new InstalledApp("Pkg", null, null, "Pkg_1.0.0.0_x64__abc", IsStorePackage: true), null, []);
        Assert.Equal("pkg:Pkg_1.0.0.0_x64__abc", store.Id);
        Assert.Equal("Microsoft Store", ApplicationsCatalog.SourceTitle(store.Source));
    }

    // ---- Last used: UserAssist ----

    [Fact]
    public void Rot13DecodesUserAssistNames()
    {
        Assert.Equal(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Google\Chrome\Application\chrome.exe",
            ProgramUsage.Rot13(@"{6Q809377-6NS0-444O-8957-N3773S02200R}\Tbbtyr\Puebzr\Nccyvpngvba\puebzr.rkr"));
        Assert.Equal("abc", ProgramUsage.Rot13(ProgramUsage.Rot13("abc")));
    }

    [Fact]
    public void LastRunReadsTheFileTimeAtItsOffset()
    {
        var when = new DateTimeOffset(2026, 5, 1, 8, 30, 0, TimeSpan.Zero);
        var data = new byte[72];
        BitConverter.GetBytes(when.UtcDateTime.ToFileTimeUtc()).CopyTo(data, 60);
        Assert.Equal(when, ProgramUsage.LastRun(data));
        var xp = new byte[16];
        BitConverter.GetBytes(when.UtcDateTime.ToFileTimeUtc()).CopyTo(xp, 8);
        Assert.Equal(when, ProgramUsage.LastRun(xp));
        Assert.Null(ProgramUsage.LastRun(new byte[72]));   // never run: zero
        Assert.Null(ProgramUsage.LastRun(new byte[20]));
        Assert.Null(ProgramUsage.LastRun(null));
    }

    [Fact]
    public void KnownFolderGuidsResolve()
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = @"C:\Program Files",
        };
        Assert.Equal(@"C:\Program Files\Vendor\app.exe",
            ProgramUsage.Resolve(@"{6d809377-6af0-444b-8957-a3773f02200e}\Vendor\app.exe", folders));
        Assert.Equal(@"{0000}\x.exe", ProgramUsage.Resolve(@"{0000}\x.exe", folders));
        Assert.Equal(@"C:\Tools\x.exe", ProgramUsage.Resolve(@"C:\Tools\x.exe", folders));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\",0", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe,-101", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.ico", null)]
    [InlineData(null, null)]
    public void DisplayIconNamesTheExe(string? icon, string? expected)
    {
        Assert.Equal(expected, ProgramUsage.ExecutablePath(icon));
    }

    [Fact]
    public void PackageFamilyComesFromTheFullName()
    {
        Assert.Equal("SpotifyAB.SpotifyMusic_zpdnekdrzrea0",
            ProgramUsage.PackageFamily("SpotifyAB.SpotifyMusic_1.250.0.0_x64__zpdnekdrzrea0"));
        Assert.Null(ProgramUsage.PackageFamily("NotAFullName"));
    }

    [Fact]
    public void LaunchesMatchOnlyTheProgramsOwnExeShortcutOrPackage()
    {
        string[] broad = [@"C:\Program Files"];
        var day = (int d) => Now.AddDays(-d);
        var launches = new List<ProgramLaunch>
        {
            new(@"C:\Program Files\Steam\steam.exe", day(40)),
            new(@"C:\Program Files\Steam\uninstall.exe", day(1)),          // the uninstaller isn't opening it
            new(@"C:\Program Files\SteamVR\vrmonitor.exe", day(2)),        // a sibling, not inside Steam
            new(@"C:\Program Files\Other\other.exe", day(3)),
            new("Steam.lnk", day(10)),
            new(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Zoom Workplace.lnk", day(5)),
            new("Zoom.lnk", day(1)),                                       // not spelled like "Zoom Workplace"
            new("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", day(7)),
            new(@"C:\Tools\notepad++.exe", day(4)),
        };
        var steam = new InstalledApp("Steam", "Valve", @"C:\Program Files\Steam", "Steam");
        Assert.Equal(day(10), ProgramUsage.LastUsed(steam, launches, broad));

        var zoom = new InstalledApp("Zoom Workplace", null, null, "Zoom");
        Assert.Equal(day(5), ProgramUsage.LastUsed(zoom, launches, broad));

        var spotify = new InstalledApp("SpotifyAB.SpotifyMusic", null, null,
            "SpotifyAB.SpotifyMusic_1.250.0.0_x64__zpdnekdrzrea0", IsStorePackage: true);
        Assert.Equal(day(7), ProgramUsage.LastUsed(spotify, launches, broad));

        // No usable folder: only its DisplayIcon .exe counts.
        var npp = new InstalledApp("Notepad++", null, @"C:\Program Files", "npp", DisplayIcon: @"C:\Tools\notepad++.exe");
        Assert.Equal(day(4), ProgramUsage.LastUsed(npp, launches, broad));

        // A broad install folder never claims every program in it.
        var broken = new InstalledApp("Broken Thing", null, @"C:\Program Files", "broken");
        Assert.Null(ProgramUsage.LastUsed(broken, launches, broad));
    }
}
