namespace Nsqs.Tests;

public class IndexFileHelperTests
{
    [Fact]
    public void SwapDatabaseFiles_ReplacesLiveDatabase()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var livePath = Path.Combine(tempDir, "index.db");
        var buildingPath = Path.Combine(tempDir, "index.building.db");
        var root = "\\\\server\\share\\";

        try
        {
            IndexStore.InitializeDatabase(livePath);
            IndexStore.ApplyIncrementalChanges(livePath,
            [
                new FolderEntry { Name = "Old", Path = "\\\\server\\share\\Old", RootShare = root }
            ], []);

            IndexStore.InitializeDatabase(buildingPath);
            IndexStore.ApplyIncrementalChanges(buildingPath,
            [
                new FolderEntry { Name = "New", Path = "\\\\server\\share\\New", RootShare = root }
            ], []);

            TestFileHelper.ClearSqlitePools();
            IndexFileHelper.SwapDatabaseFiles(buildingPath, livePath, TestFileHelper.ClearSqlitePools);

            Assert.False(File.Exists(buildingPath));
            Assert.True(File.Exists(livePath));

            var results = IndexStore.SearchSnapshot(livePath, "New", 10, null);
            Assert.Single(results);
            Assert.Equal("New", results[0].Name);
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void SwapDatabaseFiles_ThrowsWhenBuildingFileMissing()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var livePath = Path.Combine(tempDir, "index.db");
        var buildingPath = Path.Combine(tempDir, "index.building.db");

        try
        {
            IndexStore.InitializeDatabase(livePath);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                IndexFileHelper.SwapDatabaseFiles(buildingPath, livePath, null));

            Assert.Contains("Building index not found", ex.Message);
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void IsValidDatabase_RejectsCorruptFile()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var dbPath = Path.Combine(tempDir, "index.db");

        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(dbPath, "not a sqlite database");

            Assert.False(IndexFileHelper.IsValidDatabase(dbPath));
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void IsValidDatabase_AcceptsInitializedDatabase()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var dbPath = Path.Combine(tempDir, "index.db");

        try
        {
            IndexStore.InitializeDatabase(dbPath);

            Assert.True(IndexFileHelper.IsValidDatabase(dbPath));
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }
}
