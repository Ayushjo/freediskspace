using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>
/// Leftover matching is whole-word and never takes a sibling app's folder
/// (AGENTS.md: fewer false positives). Until 2026-10-21 it was a substring
/// match: "Steam" claimed "SteamVR" and a publisher token claimed every
/// folder from that vendor.
/// </summary>
public class AppLeftoverMatchingTests
{
    private static InstalledApp App(string name, string? publisher = null) =>
        new(name, publisher, InstallLocation: null, RegistryKeyName: name);

    private static readonly InstalledApp Steam = App("Steam", "Valve Corporation");
    private static readonly InstalledApp SteamVR = App("SteamVR", "Valve Corporation");
    private static readonly InstalledApp Zoom = App("Zoom Workplace", "Zoom Communications, Inc.");
    private static readonly InstalledApp Chrome = App("Google Chrome", "Google LLC");
    private static readonly InstalledApp Drive = App("Google Drive", "Google LLC");
    private static readonly InstalledApp Spotify = App("Spotify", "Spotify AB");

    [Theory]
    [InlineData("SteamVR")]
    [InlineData("steamvr-logs")]
    [InlineData("Steamworks Shared")]
    public void SteamNeverTakesOtherFolders(string folder)
    {
        Assert.False(AppLeftoverFinder.MatchesApp(folder, Steam, [Steam, SteamVR]));
    }

    [Fact]
    public void AnAppKeepsItsOwnFolder()
    {
        Assert.True(AppLeftoverFinder.MatchesApp("Steam", Steam, [Steam, SteamVR]));
        Assert.True(AppLeftoverFinder.MatchesApp("SteamVR", SteamVR, [Steam, SteamVR]));
        Assert.True(AppLeftoverFinder.MatchesApp("Spotify", Spotify, [Spotify]));
        Assert.True(AppLeftoverFinder.MatchesApp("Spotify.log", Spotify, [Spotify]));
    }

    [Fact]
    public void AShortFolderNameMatchesALongerAppName()
    {
        Assert.True(AppLeftoverFinder.MatchesApp("Zoom", Zoom, [Zoom]));
        Assert.True(AppLeftoverFinder.MatchesApp("Zoom Workplace", Zoom, [Zoom]));
    }

    [Fact]
    public void ASharedVendorFolderBelongsToNoSingleApp()
    {
        Assert.False(AppLeftoverFinder.MatchesApp("Google", Chrome, [Chrome, Drive]));
        Assert.False(AppLeftoverFinder.MatchesApp("Google", Chrome, [Chrome]));   // known shared vendor folder
        Assert.True(AppLeftoverFinder.MatchesApp("Google Chrome", Chrome, [Chrome, Drive]));
    }

    [Fact]
    public void PublisherFolderOnlyWhenNoOtherAppSharesThePublisher()
    {
        var tidal = App("TIDAL", "Aspiro AB");
        Assert.True(AppLeftoverFinder.MatchesApp("Aspiro", tidal, [tidal]));
        var other = App("Aspiro Tool", "Aspiro AB");
        Assert.False(AppLeftoverFinder.MatchesApp("Aspiro", tidal, [tidal, other]));
        Assert.False(AppLeftoverFinder.MatchesApp("Valve", Steam, [Steam, SteamVR]));
    }

    [Fact]
    public void UserDocumentsNeedTheExactName()
    {
        Assert.False(AppLeftoverFinder.MatchesApp("Zoom", Zoom, [Zoom], exactOnly: true));
        Assert.True(AppLeftoverFinder.MatchesApp("Zoom Workplace", Zoom, [Zoom], exactOnly: true));
    }

    [Theory]
    [InlineData("Git")]
    [InlineData("VLC")]
    public void VeryShortNamesNeverMatch(string name)
    {
        var app = App(name);
        Assert.False(AppLeftoverFinder.MatchesApp(name, app, [app]));
    }
}
