using DiskMap.App.Pages;
using DiskMap.Core;

namespace DiskMap.App.Tests;

/// <summary>Applications' words (Mac: AppsView): the sort segments, the subtitle, the pill, the inspector.</summary>
public class AppsBoardTests
{
    private static ApplicationEntry App(string name, long app, long data, bool pending = false) => new()
    {
        Id = "reg:" + name,
        Name = name,
        AppBytes = app,
        Related = data > 0 ? [new ApplicationRelatedItem(ApplicationRelatedKind.RoamingData, @"C:\d\" + name, data)] : [],
        SizePending = pending,
    };

    [Fact]
    public void SortSegmentsMatchTheMac()
    {
        Assert.Equal(["Size", "Last opened", "Name"], Enum.GetValues<AppsBoardPage.SortKey>().Select(AppsBoardPage.SortTitle));
        Assert.Equal(ApplicationSort.SizeDesc, AppsBoardPage.CatalogSort(AppsBoardPage.SortKey.Size));
        Assert.Equal(ApplicationSort.LastUsedAsc, AppsBoardPage.CatalogSort(AppsBoardPage.SortKey.LastOpened));
        Assert.Equal(ApplicationSort.NameAsc, AppsBoardPage.CatalogSort(AppsBoardPage.SortKey.Name));
    }

    [Fact]
    public void SubtitleCountsAppsAndTheirData()
    {
        Assert.Equal("Finding your apps…", AppsBoardPage.Subtitle(true, []));
        var apps = new[] { App("A", 2_000_000_000, 500_000_000), App("B", 890_000_000, 1_260_000_000) };
        Assert.Equal("2 apps use 2.89 GB, and the data they keep uses another 1.76 GB.", AppsBoardPage.Subtitle(false, apps));
        Assert.Equal("2 apps use 2.89 GB, and the data they keep uses another 1.76 GB.", AppsBoardPage.Subtitle(true, apps));
    }

    [Fact]
    public void StalePillSaysNoneOrCountAndSize()
    {
        Assert.Equal("none", AppsBoardPage.StaleText([]));
        Assert.Equal("2 · 4.20 GB", AppsBoardPage.StaleText([App("A", 1_000_000_000, 200_000_000), App("B", 3_000_000_000, 0)]));
    }

    [Fact]
    public void InspectorWords()
    {
        Assert.Equal("LEFTOVERS · 1 PLACE", AppsBoardPage.PlacesLabel(1));
        Assert.Equal("LEFTOVERS · 3 PLACES", AppsBoardPage.PlacesLabel(3));
        Assert.Equal("Valve Corporation · version 2.10", AppsBoardPage.PublisherLine("Valve Corporation", "2.10"));
        Assert.Equal("version 2.10", AppsBoardPage.PublisherLine(null, "2.10"));
        Assert.Equal("", AppsBoardPage.PublisherLine(null, null));
        Assert.Equal("In the basket", AppsBoardPage.PrimaryTitle(true, 2, 10));
        Assert.Equal("Tick the leftovers to clean up", AppsBoardPage.PrimaryTitle(false, 0, 0));
        Assert.Equal("Clean up · add 1.37 GB to basket", AppsBoardPage.PrimaryTitle(false, 2, 1_370_000_000));
        Assert.Contains("Settings › Apps", AppsBoardPage.Guidance(App("Steam", 1, 0)));
        Assert.Contains("Microsoft Store", AppsBoardPage.Guidance(App("Pkg", 1, 0) with { Source = ApplicationSource.Store }));
    }
}
