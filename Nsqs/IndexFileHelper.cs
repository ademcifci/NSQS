using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Nsqs
{
    internal static class IndexFileHelper
    {
        public static void DeleteDatabaseFiles(string basePath)
        {
            foreach (var path in new[] { basePath, basePath + "-wal", basePath + "-shm" })
            {
                if (!File.Exists(path))
                    continue;

                File.Delete(path);
            }
        }

        public static void SwapDatabaseFiles(string buildingPath, string livePath, Action? releaseLocks)
        {
            if (!File.Exists(buildingPath))
                throw new InvalidOperationException($"Building index not found: {buildingPath}");

            const int maxAttempts = 8;
            var backupPath = livePath + ".bak";
            var tempPath = livePath + ".new";
            Exception? lastError = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                releaseLocks?.Invoke();

                try
                {
                    IndexStore.CheckpointLiveDatabase(livePath);
                    SqliteConnection.ClearAllPools();

                    if (File.Exists(livePath))
                    {
                        DeleteDatabaseFiles(backupPath);
                        File.Copy(livePath, backupPath, overwrite: true);
                    }

                    DeleteDatabaseFiles(tempPath);
                    if (!File.Exists(buildingPath))
                        throw new FileNotFoundException($"Building index not found: {buildingPath}");

                    File.Move(buildingPath, tempPath);
                    DeleteDatabaseFiles(livePath);
                    File.Move(tempPath, livePath);

                    IndexStore.ConfigureLiveDatabase(livePath);

                    try { DeleteDatabaseFiles(backupPath); } catch { /* best effort */ }
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts && ex is IOException or UnauthorizedAccessException or SqliteException)
                {
                    lastError = ex;
                    Diagnostics.Log($"Index swap attempt {attempt} failed: {ex.Message}");
                    RestoreLiveFromBackup(livePath, backupPath);

                    if (File.Exists(tempPath) && !File.Exists(buildingPath))
                    {
                        try { File.Move(tempPath, buildingPath); } catch { /* best effort */ }
                    }

                    Thread.Sleep(250);
                }
            }

            RestoreLiveFromBackup(livePath, backupPath);
            throw new IOException($"Failed to swap index database after {maxAttempts} attempts.", lastError);
        }

        private static void RestoreLiveFromBackup(string livePath, string backupPath)
        {
            if (!File.Exists(backupPath))
                return;

            if (File.Exists(livePath) && IsValidDatabase(livePath))
                return;

            try
            {
                DeleteDatabaseFiles(livePath);
                File.Copy(backupPath, livePath, overwrite: true);

                if (!IsValidDatabase(livePath))
                    throw new InvalidDataException("Restored index backup failed integrity check.");

                IndexStore.ConfigureLiveDatabase(livePath);
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Index backup restore failed: {ex.Message}");
            }
        }

        internal static bool IsValidDatabase(string dbPath)
        {
            if (!File.Exists(dbPath))
                return false;

            try
            {
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Default
                };

                using var connection = new SqliteConnection(builder.ConnectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check;";
                return string.Equals(cmd.ExecuteScalar()?.ToString(), "ok", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static void CleanupStaleIndexFiles(string livePath, string buildingPath)
        {
            if (File.Exists(buildingPath))
            {
                try
                {
                    DeleteDatabaseFiles(buildingPath);
                    Diagnostics.Log("Removed stale building index from prior session.");
                }
                catch (Exception ex)
                {
                    Diagnostics.Log($"Failed to remove building index: {ex.Message}");
                }
            }

            var backupPath = livePath + ".bak";
            if (!File.Exists(backupPath))
                return;

            if (File.Exists(livePath) && IsValidDatabase(livePath))
            {
                try
                {
                    DeleteDatabaseFiles(backupPath);
                    Diagnostics.Log("Removed orphan index backup.");
                }
                catch (Exception ex)
                {
                    Diagnostics.Log($"Failed to remove orphan index backup: {ex.Message}");
                }
                return;
            }

            RestoreLiveFromBackup(livePath, backupPath);
        }
    }
}
