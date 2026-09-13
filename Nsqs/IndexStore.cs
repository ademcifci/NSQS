using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Nsqs
{
    public sealed class FolderEntry
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public required string RootShare { get; init; }
    }

    public sealed class IndexStore : IDisposable
    {
        private readonly object _lock = new();
        private SqliteConnection? _connection;

        public bool IsOpen => _connection != null;

        public void OpenForSearch(string dbPath)
        {
            lock (_lock)
            {
                CloseInternal();
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Default
                };
                _connection = new SqliteConnection(builder.ConnectionString);
                _connection.Open();
            }
        }

        public void OpenForWrite(string dbPath)
        {
            lock (_lock)
            {
                CloseInternal();
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Default
                };
                _connection = new SqliteConnection(builder.ConnectionString);
                _connection.Open();
            }
        }

        [Obsolete("Use OpenForSearch or OpenForWrite.")]
        public void Open(string dbPath) => OpenForWrite(dbPath);

        public void Reopen()
        {
            OpenForSearch(AppPaths.IndexFile);
        }

        public void Close()
        {
            lock (_lock)
            {
                CloseInternal();
            }
        }

        private void CloseInternal()
        {
            if (_connection == null)
                return;

            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Read-only or non-WAL databases may reject this.
            }

            _connection.Dispose();
            _connection = null;
        }

        public static void InitializeDatabase(string dbPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                // DELETE avoids -wal/-shm sidecars that block file replace on Windows.
                pragma.CommandText = "PRAGMA journal_mode=DELETE;";
                pragma.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS meta (
                        key TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );

                    CREATE VIRTUAL TABLE IF NOT EXISTS folders_fts USING fts5(
                        name,
                        path UNINDEXED,
                        root_share UNINDEXED,
                        tokenize='unicode61 remove_diacritics 2'
                    );
                    """;
                cmd.ExecuteNonQuery();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                EnsureOpen();
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "DELETE FROM folders_fts;";
                cmd.ExecuteNonQuery();
            }
        }

        public void InsertBatch(IReadOnlyList<FolderEntry> batch)
        {
            if (batch.Count == 0) return;

            lock (_lock)
            {
                EnsureOpen();
                using var tx = _connection!.BeginTransaction();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO folders_fts (name, path, root_share)
                    VALUES ($name, $path, $root);
                    """;
                var nameParam = cmd.CreateParameter();
                nameParam.ParameterName = "$name";
                cmd.Parameters.Add(nameParam);
                var pathParam = cmd.CreateParameter();
                pathParam.ParameterName = "$path";
                cmd.Parameters.Add(pathParam);
                var rootParam = cmd.CreateParameter();
                rootParam.ParameterName = "$root";
                cmd.Parameters.Add(rootParam);

                foreach (var entry in batch)
                {
                    nameParam.Value = entry.Name;
                    pathParam.Value = entry.Path;
                    rootParam.Value = entry.RootShare;
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
        }

        public void SetMeta(string key, string value)
        {
            lock (_lock)
            {
                EnsureOpen();
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO meta (key, value) VALUES ($key, $value)
                    ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                    """;
                cmd.Parameters.AddWithValue("$key", key);
                cmd.Parameters.AddWithValue("$value", value);
                cmd.ExecuteNonQuery();
            }
        }

        public int GetEntryCount()
        {
            lock (_lock)
            {
                EnsureOpen();
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM folders_fts;";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public static int ExportToCsv(string dbPath, string csvPath)
        {
            if (!File.Exists(dbPath))
                return 0;

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Default
            };

            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT name, path, root_share
                FROM folders_fts
                ORDER BY root_share, path;
                """;

            var count = 0;
            using var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
            writer.WriteLine("Name,Path,Root Share");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                writer.WriteLine(string.Join(',',
                    EscapeCsv(reader.GetString(0)),
                    EscapeCsv(reader.GetString(1)),
                    EscapeCsv(reader.GetString(2))));
                count++;
            }

            return count;
        }

        public static int ExportEntriesToCsv(IEnumerable<FolderEntry> entries, string csvPath)
        {
            var count = 0;
            using var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
            writer.WriteLine("Name,Path,Root Share");

            foreach (var entry in entries)
            {
                writer.WriteLine(string.Join(',',
                    EscapeCsv(entry.Name),
                    EscapeCsv(entry.Path),
                    EscapeCsv(entry.RootShare)));
                count++;
            }

            return count;
        }

        internal static string EscapeCsv(string value)
        {
            if (value.Contains('"'))
                value = value.Replace("\"", "\"\"");

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return $"\"{value}\"";

            return value;
        }

        public IReadOnlyList<FolderEntry> Search(string query, int maxResults, IReadOnlyList<string>? rootShares = null)
        {
            var ftsQuery = BuildFtsQuery(query);
            if (string.IsNullOrEmpty(ftsQuery))
                return Array.Empty<FolderEntry>();

            var normalizedRoots = NormalizeRootShares(rootShares);

            lock (_lock)
            {
                EnsureOpen();
                using var cmd = _connection!.CreateCommand();

                if (normalizedRoots.Count == 0)
                {
                    cmd.CommandText = """
                        SELECT name, path, root_share
                        FROM folders_fts
                        WHERE folders_fts MATCH $query
                        ORDER BY rank
                        LIMIT $limit;
                        """;
                }
                else if (normalizedRoots.Count == 1)
                {
                    cmd.CommandText = """
                        SELECT name, path, root_share
                        FROM folders_fts
                        WHERE folders_fts MATCH $query AND root_share = $root0
                        ORDER BY rank
                        LIMIT $limit;
                        """;
                    cmd.Parameters.AddWithValue("$root0", normalizedRoots[0]);
                }
                else
                {
                    var placeholders = string.Join(", ", normalizedRoots.Select((_, i) => $"$root{i}"));
                    cmd.CommandText = $"""
                        SELECT name, path, root_share
                        FROM folders_fts
                        WHERE folders_fts MATCH $query AND root_share IN ({placeholders})
                        ORDER BY rank
                        LIMIT $limit;
                        """;
                    for (int i = 0; i < normalizedRoots.Count; i++)
                        cmd.Parameters.AddWithValue($"$root{i}", normalizedRoots[i]);
                }

                cmd.Parameters.AddWithValue("$query", ftsQuery);
                cmd.Parameters.AddWithValue("$limit", maxResults);

                var results = new List<FolderEntry>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add(new FolderEntry
                    {
                        Name = reader.GetString(0),
                        Path = reader.GetString(1),
                        RootShare = reader.GetString(2)
                    });
                }

                return results;
            }
        }

        private static List<string> NormalizeRootShares(IReadOnlyList<string>? rootShares)
        {
            if (rootShares == null || rootShares.Count == 0)
                return new List<string>();

            var normalized = new List<string>(rootShares.Count);
            foreach (var root in rootShares)
            {
                var path = ShareIndexer.NormalizeUncRoot(root);
                if (path != null && !normalized.Contains(path, StringComparer.OrdinalIgnoreCase))
                    normalized.Add(path);
            }

            return normalized;
        }

        internal static string BuildFtsQuery(string userInput)
        {
            var tokens = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
                return string.Empty;

            var parts = new List<string>(tokens.Length);
            foreach (var token in tokens)
            {
                var cleaned = token.Replace("\"", string.Empty);
                if (string.IsNullOrEmpty(cleaned))
                    continue;

                parts.Add($"\"{cleaned}\"*");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" AND ", parts);
        }

        private void EnsureOpen()
        {
            if (_connection == null)
                throw new InvalidOperationException("Index database is not open.");
        }

        public void Dispose()
        {
            Close();
        }
    }
}
