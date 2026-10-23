using DiskMap.Core;

namespace DiskMap.Core.Tests;

/// <summary>The list search box (Mac: ListQuery): tokens understood, the rest searched as words.</summary>
public class ListQueryTests
{
    [Fact]
    public void TokensAndWords()
    {
        var q = ListQuery.Parse("Report size>1GB kind:video changed>1y in:Downloads name:final");
        Assert.Equal(1_000_000_000, q.MinSize);
        Assert.Equal(FileKind.Video, q.Kind);
        Assert.Equal(365, q.OlderThanDays);
        Assert.Equal("downloads", q.InFolder);
        Assert.Equal(["report", "final"], q.Words);
        Assert.Equal(["size>1GB", "kind:video", "changed>1y", "in:Downloads", "name:final"], q.Tokens);
    }

    [Theory]
    [InlineData("size<500mb", null, 500_000_000L)]
    [InlineData("size:2.5tb", 2_500_000_000_000L, null)]
    [InlineData("size:>100k", 100_000L, null)]
    [InlineData("size>12", 12L, null)]
    public void SizeForms(string text, long? min, long? max)
    {
        var q = ListQuery.Parse(text);
        Assert.Equal(min, q.MinSize);
        Assert.Equal(max, q.MaxSize);
    }

    [Theory]
    [InlineData("size>lots")]     // not a size: kept as a word
    [InlineData("kind:pottery")]  // unknown kind: kept as a word
    [InlineData("changed>soon")]
    [InlineData("in:")]
    public void UnknownTokensAreSearchedAsText(string text)
    {
        var q = ListQuery.Parse(text);
        Assert.Empty(q.Tokens);
        Assert.Equal([text], q.Words);
    }

    [Theory]
    [InlineData("30d", 30)]
    [InlineData("6w", 42)]
    [InlineData("3m", 90)]
    [InlineData("1y", 365)]
    [InlineData("1.5y", 547)]
    [InlineData("10x", null)]
    [InlineData("", null)]
    public void Days(string text, int? expected) => Assert.Equal(expected, ListQuery.Days(text));

    [Fact]
    public void MatchingFollowsTheMac()
    {
        var q = ListQuery.Parse("wedding size>1gb changed>30d in:videos");
        Assert.True(q.Matches("wedding edit 4k.mov\n~/videos/wedding edit 4k.mov", "videos", 26_100_000_000, FileKind.Video, 410));
        Assert.False(q.Matches("wedding edit 4k.mov", "videos", 1_000_000_000, FileKind.Video, 410));   // size must exceed
        Assert.False(q.Matches("wedding edit 4k.mov", "videos", 26_100_000_000, FileKind.Video, 10));    // too recent
        Assert.False(q.Matches("wedding edit 4k.mov", "videos", 26_100_000_000, FileKind.Video, null));  // unknown age
        Assert.False(q.Matches("wedding edit 4k.mov", "pictures", 26_100_000_000, FileKind.Video, 410));
        Assert.True(ListQuery.Parse("").Matches("anything", "x", 1, FileKind.Other, null));
        Assert.True(ListQuery.Parse("").IsEmpty);
    }

    [Fact]
    public void WindowsKindWords()
    {
        Assert.Equal(FileKind.DiskImage, ListQuery.Parse("kind:msi").Kind);
        Assert.Equal(FileKind.VirtualDisk, ListQuery.Parse("kind:vhdx").Kind);
    }
}
