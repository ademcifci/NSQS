namespace Nsqs.Tests;

public class IndexStoreTests
{
    [Theory]
    [InlineData("movies", "\"movies\"*")]
    [InlineData("star wars", "\"star\"* AND \"wars\"*")]
    [InlineData("foo*bar", "\"foobar\"*")]
    public void BuildFtsQuery_SanitizesTokens(string input, string expected)
    {
        Assert.Equal(expected, IndexStore.BuildFtsQuery(input));
    }

    [Fact]
    public void ApplyIncrementalChanges_AddsAndRemovesFolders()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var dbPath = Path.Combine(tempDir, "index.db");

        try
        {
            IndexStore.InitializeDatabase(dbPath);
            IndexStore.ConfigureLiveDatabase(dbPath);

            var root = "\\\\server\\share\\";
            var addResult = IndexStore.ApplyIncrementalChanges(dbPath,
            [
                new FolderEntry { Name = "Movies", Path = "\\\\server\\share\\Movies", RootShare = root },
                new FolderEntry { Name = "TV", Path = "\\\\server\\share\\TV", RootShare = root }
            ],
            []);

            Assert.Equal(2, addResult.Added);
            Assert.Equal(2, addResult.TotalCount);

            var removeResult = IndexStore.ApplyIncrementalChanges(dbPath, [],
                ["\\\\server\\share\\Movies"]);

            Assert.True(removeResult.Removed >= 1);
            Assert.Equal(1, removeResult.TotalCount);

            var results = IndexStore.SearchSnapshot(dbPath, "TV", 10, null);
            Assert.Single(results);
            Assert.Equal("TV", results[0].Name);
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }
}
