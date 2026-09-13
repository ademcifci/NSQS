using Microsoft.Data.Sqlite;

namespace Nsqs.Tests;

internal static class TestFileHelper
{
    public static string CreateTempDirectory() =>
        Path.Combine(Path.GetTempPath(), "NSQS-tests-" + Guid.NewGuid().ToString("N"));

    public static void ClearSqlitePools()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SqliteConnection.ClearAllPools();
    }

    public static void DeleteTempDirectory(string tempDir)
    {
        ClearSqlitePools();

        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, recursive: true);
    }
}
