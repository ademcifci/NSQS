namespace Nsqs.Tests;

public class SharePathHelperTests
{
    [Theory]
    [InlineData("\\\\server\\share\\Movies", "\\\\server\\share\\", true)]
    [InlineData("\\\\server\\share\\", "\\\\server\\share\\", true)]
    [InlineData("\\\\server\\sharepoint\\data", "\\\\server\\share\\", false)]
    public void IsUnderRoot_UsesBoundarySafeMatching(string path, string root, bool expected)
    {
        Assert.Equal(expected, SharePathHelper.IsUnderRoot(path, root));
    }

    [Fact]
    public void FindRootShare_PrefersLongestMatchingRoot()
    {
        var roots = new[] { "\\\\server\\share\\", "\\\\server\\share\\Movies\\" };
        var path = "\\\\server\\share\\Movies\\Action";

        Assert.Equal("\\\\server\\share\\Movies\\", SharePathHelper.FindRootShare(path, roots));
    }

    [Fact]
    public void IsSafeIndexedPath_RejectsPathsOutsideConfiguredRoots()
    {
        var entry = new FolderEntry
        {
            Name = "Evil",
            Path = "\\\\other\\share\\secret",
            RootShare = "\\\\other\\share\\"
        };

        Assert.False(SharePathHelper.IsSafeIndexedPath(entry, ["\\\\server\\share\\"]));
    }
}
