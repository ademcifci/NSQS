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
            const int maxAttempts = 8;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                releaseLocks?.Invoke();

                try
                {
                    DeleteDatabaseFiles(livePath);

                    if (File.Exists(buildingPath))
                        File.Move(buildingPath, livePath);

                    return;
                }
                catch (IOException ex) when (attempt < maxAttempts)
                {
                    Diagnostics.Log($"Index swap attempt {attempt} failed: {ex.Message}");
                    Thread.Sleep(250);
                }
            }
        }
    }
}
