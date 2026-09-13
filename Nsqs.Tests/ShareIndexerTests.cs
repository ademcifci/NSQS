namespace Nsqs.Tests;

public class ShareIndexerTests
{
    [Theory]
    [InlineData("\\\\server\\share", "\\\\server\\share\\")]
    [InlineData("\\\\server\\share\\", "\\\\server\\share\\")]
    [InlineData("s:\\mapped", null)]
    public void NormalizeUncRoot_AcceptsUncOnly(string input, string? expected)
    {
        Assert.Equal(expected, ShareIndexer.NormalizeUncRoot(input));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    public void ShouldAbortRebuild_WhenNoReachableRoots(int reachableRootCount, bool expected)
    {
        Assert.Equal(expected, ShareIndexer.ShouldAbortRebuild(reachableRootCount));
    }

    [Fact]
    public void BuildAllRootsUnreachableMessage_IsStable()
    {
        Assert.Contains("unreachable", ShareIndexer.BuildAllRootsUnreachableMessage(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSkippedRootsWarning_ListsSkippedRoots()
    {
        var warning = ShareIndexer.BuildSkippedRootsWarning(["\\\\server\\offline\\", "\\\\server\\away\\"]);

        Assert.Contains("\\\\server\\offline\\", warning);
        Assert.Contains("\\\\server\\away\\", warning);
    }
}
