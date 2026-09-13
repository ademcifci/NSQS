namespace Nsqs.Tests;

public class ShareWatchReconcilerTests
{
    [Fact]
    public void CollectChanges_AddsMissingFoldersAndRemovesStaleOnes()
    {
        var root = "\\\\server\\share\\";
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var additions = new List<FolderEntry>();
        var removals = new List<string>();

        var diskEntries = new[]
        {
            new FolderEntry { Name = "Movies", Path = "\\\\server\\share\\Movies", RootShare = root },
            new FolderEntry { Name = "TV", Path = "\\\\server\\share\\TV", RootShare = root }
        };

        var indexedPaths = new[]
        {
            "\\\\server\\share\\Movies",
            "\\\\server\\share\\Old"
        };

        ShareWatchReconciler.CollectChanges(diskEntries, indexedPaths, seenPaths, additions, removals);

        Assert.Equal(2, additions.Count);
        Assert.Contains(removals, path => path.Equals("\\\\server\\share\\Old", StringComparison.OrdinalIgnoreCase));
    }
}
