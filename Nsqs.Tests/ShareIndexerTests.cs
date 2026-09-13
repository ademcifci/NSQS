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
}
