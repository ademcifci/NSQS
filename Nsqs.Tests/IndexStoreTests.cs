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

    [Fact]
    public void ReadEntriesForRoots_ReturnsOnlyMatchingShares()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var dbPath = Path.Combine(tempDir, "index.db");

        try
        {
            IndexStore.InitializeDatabase(dbPath);
            var shareA = "\\\\server\\share-a\\";
            var shareB = "\\\\server\\share-b\\";

            IndexStore.ApplyIncrementalChanges(dbPath,
            [
                new FolderEntry { Name = "A1", Path = "\\\\server\\share-a\\A1", RootShare = shareA },
                new FolderEntry { Name = "B1", Path = "\\\\server\\share-b\\B1", RootShare = shareB }
            ],
            []);

            var entries = IndexStore.ReadEntriesForRoots(dbPath, [shareA]);

            Assert.Single(entries);
            Assert.Equal("A1", entries[0].Name);
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void GetIndexedPathsForRoot_ReturnsStoredPaths()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var dbPath = Path.Combine(tempDir, "index.db");

        try
        {
            IndexStore.InitializeDatabase(dbPath);
            var root = "\\\\server\\share\\";

            IndexStore.ApplyIncrementalChanges(dbPath,
            [
                new FolderEntry { Name = "Movies", Path = "\\\\server\\share\\Movies", RootShare = root }
            ],
            []);

            var paths = IndexStore.GetIndexedPathsForRoot(dbPath, root);

            Assert.Single(paths);
            Assert.Equal("\\\\server\\share\\Movies", paths[0]);
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData("folder%name", "folder\\%name")]
    [InlineData("a_b", "a\\_b")]
    public void EscapeLikePattern_EscapesMetacharacters(string input, string expected)
    {
        Assert.Equal(expected, IndexStore.EscapeLikePattern(input));
    }

    [Fact]
    public void PurgeShareRoots_RemovesAllFoldersUnderRoot()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var dbPath = Path.Combine(tempDir, "index.db");

        try
        {
            IndexStore.InitializeDatabase(dbPath);
            var keepRoot = "\\\\server\\keep\\";
            var removeRoot = "\\\\server\\remove\\";

            IndexStore.ApplyIncrementalChanges(dbPath,
            [
                new FolderEntry { Name = "Keep", Path = "\\\\server\\keep\\Docs", RootShare = keepRoot },
                new FolderEntry { Name = "Remove", Path = "\\\\server\\remove\\Old", RootShare = removeRoot }
            ],
            []);

            var result = IndexStore.PurgeShareRoots(dbPath, [removeRoot]);

            Assert.True(result.Removed >= 1);
            Assert.Equal(1, result.TotalCount);

            var remaining = IndexStore.SearchSnapshot(dbPath, "Keep", 10, null);
            Assert.Single(remaining);
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void InsertBatch_InsertsLargeBatchInChunks()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var dbPath = Path.Combine(tempDir, "index.db");

        try
        {
            IndexStore.InitializeDatabase(dbPath);
            using var store = new IndexStore();
            store.OpenForWrite(dbPath);

            var root = "\\\\server\\share\\";
            var entries = new List<FolderEntry>(250);
            for (int i = 0; i < 250; i++)
            {
                entries.Add(new FolderEntry
                {
                    Name = $"Folder{i}",
                    Path = $"\\\\server\\share\\Folder{i}",
                    RootShare = root
                });
            }

            store.InsertBatch(entries);

            Assert.Equal(250, store.GetEntryCount());

            var results = IndexStore.SearchSnapshot(dbPath, "Folder249", 10, null);
            Assert.Single(results);
            Assert.Equal("Folder249", results[0].Name);
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }
}
