namespace DiskMap.App.Tests;

/// <summary>How paths are quoted for File Explorer and which files Open reveals instead of running.</summary>
public class ExplorerLaunchTests
{
    [Theory]
    [InlineData(@"C:\Users\me\My Files\a.txt", "\"C:\\Users\\me\\My Files\\a.txt\"")]
    [InlineData(@"C:\Users\me\Downloads\", "\"C:\\Users\\me\\Downloads\"")]
    // A root's "\" right before the closing quote would escape it: keep a "." after it.
    [InlineData(@"C:\", "\"C:\\.\"")]
    [InlineData("D:", "\"D:\\.\"")]
    [InlineData(@"\\nas\share\x", "\"\\\\nas\\share\\x\"")]
    public void PathsAreQuotedSoTheCommandLineCantBreak(string path, string expected) =>
        Assert.Equal(expected, Explorer.Quote(path));

    [Theory]
    [InlineData(".exe")]
    [InlineData(".HTA")]
    [InlineData(".reg")]
    [InlineData(".py")]
    [InlineData(".chm")]
    public void ProgramsAndScriptsAreRevealedNotRun(string extension) =>
        Assert.Contains(extension, Explorer.RunnableExtensions);

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".jpg")]
    [InlineData(".txt")]
    public void DocumentsOpen(string extension) =>
        Assert.DoesNotContain(extension, Explorer.RunnableExtensions);
}
