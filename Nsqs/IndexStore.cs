using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
            if (_connection == null)
                return;

            var dbPath = _connection.DataSource;
            OpenForSearch(dbPath);
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

        public static void ConfigureLiveDatabase(string dbPath)
        {
            using var connection = OpenReadWriteConnection(dbPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        public static void CheckpointLiveDatabase(string dbPath)
        {
            if (!File.Exists(dbPath))
                return;

            try
            {
                using var connection = OpenReadWriteConnection(dbPath);
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Building databases and read-only opens may not support this.
            }
        }

        public static IReadOnlyList<FolderEntry> SearchSnapshot(
            string dbPath,
            string query,
            int maxResults,
            IReadOnlyList<string>? rootShares = null)
        {
            using var store = new IndexStore();
            store.OpenForSearch(dbPath);
            return store.Search(query, maxResults, rootShares);
        }

        public static IReadOnlyList<FolderEntry> ReadEntriesForRoots(string dbPath, IReadOnlyList<string> rootShares)
        {
            if (!File.Exists(dbPath) || rootShares.Count == 0)
                return Array.Empty<FolderEntry>();

            var normalizedRoots = NormalizeRootSharesStatic(rootShares);
            if (normalizedRoots.Count == 0)
                return Array.Empty<FolderEntry>();

            using var connection = OpenReadOnlyConnection(dbPath);
            using var cmd = connection.CreateCommand();

            if (normalizedRoots.Count == 1)
            {
                cmd.CommandText = """
                    SELECT name, path, root_share
                    FROM folders_fts
                    WHERE root_share = $root0
                    ORDER BY path;
                    """;
                cmd.Parameters.AddWithValue("$root0", normalizedRoots[0]);
            }
            else
            {
                var placeholders = string.Join(", ", normalizedRoots.Select((_, i) => $"$root{i}"));
                cmd.CommandText = $"""
                    SELECT name, path, root_share
                    FROM folders_fts
                    WHERE root_share IN ({placeholders})
                    ORDER BY path;
                    """;
                for (int i = 0; i < normalizedRoots.Count; i++)
                    cmd.Parameters.AddWithValue($"$root{i}", normalizedRoots[i]);
            }

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

        public static IReadOnlyList<string> GetIndexedPathsForRoot(string dbPath, string rootShare)
        {
            var normalizedRoot = ShareIndexer.NormalizeUncRoot(rootShare);
            if (normalizedRoot == null || !File.Exists(dbPath))
                return Array.Empty<string>();

            using var connection = OpenReadOnlyConnection(dbPath);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT path
                FROM folders_fts
                WHERE root_share = $root
                ORDER BY path;
                """;
            cmd.Parameters.AddWithValue("$root", normalizedRoot);

            var paths = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                paths.Add(reader.GetString(0));

            return paths;
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
                return QueryEntryCount(_connection!);
            }
        }

        public readonly record struct IncrementalApplyResult(int Added, int Removed, int TotalCount);

        public static IncrementalApplyResult ApplyIncrementalChanges(
            string dbPath,
            IReadOnlyList<FolderEntry> additions,
            IReadOnlyList<string> removedDirectoryPaths)
        {
            const int maxAttempts = 5;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return ApplyIncrementalChangesCore(dbPath, additions, removedDirectoryPaths);
                }
                catch (SqliteException ex) when (attempt < maxAttempts && ex.SqliteErrorCode == 5)
                {
                    Thread.Sleep(50 * attempt);
                }
            }

            return ApplyIncrementalChangesCore(dbPath, additions, removedDirectoryPaths);
        }

        private static IncrementalApplyResult ApplyIncrementalChangesCore(
            string dbPath,
            IReadOnlyList<FolderEntry> additions,
            IReadOnlyList<string> removedDirectoryPaths)
        {
            if (!File.Exists(dbPath))
                return new IncrementalApplyResult(0, 0, 0);

            if (additions.Count == 0 && removedDirectoryPaths.Count == 0)
            {
                using var countConnection = OpenReadWriteConnection(dbPath);
                return new IncrementalApplyResult(0, 0, QueryEntryCount(countConnection));
            }

            using var connection = OpenReadWriteConnection(dbPath);
            var added = 0;
            var removed = 0;

            using (var tx = connection.BeginTransaction())
            {
                using (var deleteCmd = connection.CreateCommand())
                {
                    deleteCmd.Transaction = tx;
                    deleteCmd.CommandText = """
                        DELETE FROM folders_fts
                        WHERE path = $path OR path LIKE $prefix ESCAPE '\';
                        """;
                    var pathParam = deleteCmd.CreateParameter();
                    pathParam.ParameterName = "$path";
                    deleteCmd.Parameters.Add(pathParam);
                    var prefixParam = deleteCmd.CreateParameter();
                    prefixParam.ParameterName = "$prefix";
                    deleteCmd.Parameters.Add(prefixParam);

                    foreach (var directoryPath in removedDirectoryPaths)
                    {
                        var normalized = NormalizeDirectoryPath(directoryPath);
                        if (normalized == null)
                            continue;

                        pathParam.Value = normalized;
                        prefixParam.Value = normalized.TrimEnd('\\') + "\\%";
                        removed += deleteCmd.ExecuteNonQuery();
                    }
                }

                using (var existsCmd = connection.CreateCommand())
                {
                    existsCmd.Transaction = tx;
                    existsCmd.CommandText = "SELECT 1 FROM folders_fts WHERE path = $path LIMIT 1;";
                    var existsParam = existsCmd.CreateParameter();
                    existsParam.ParameterName = "$path";
                    existsCmd.Parameters.Add(existsParam);

                    using var insertCmd = connection.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = """
                        INSERT INTO folders_fts (name, path, root_share)
                        VALUES ($name, $path, $root);
                        """;
                    var nameParam = insertCmd.CreateParameter();
                    nameParam.ParameterName = "$name";
                    insertCmd.Parameters.Add(nameParam);
                    var pathParam = insertCmd.CreateParameter();
                    pathParam.ParameterName = "$path";
                    insertCmd.Parameters.Add(pathParam);
                    var rootParam = insertCmd.CreateParameter();
                    rootParam.ParameterName = "$root";
                    insertCmd.Parameters.Add(rootParam);

                    foreach (var entry in additions)
                    {
                        var normalizedPath = NormalizeDirectoryPath(entry.Path);
                        var normalizedRoot = ShareIndexer.NormalizeUncRoot(entry.RootShare);
                        if (normalizedPath == null || normalizedRoot == null)
                            continue;

                        existsParam.Value = normalizedPath;
                        if (existsCmd.ExecuteScalar() != null)
                            continue;

                        nameParam.Value = entry.Name;
                        pathParam.Value = normalizedPath;
                        rootParam.Value = normalizedRoot;
                        insertCmd.ExecuteNonQuery();
                        added++;
                    }
                }

                var total = QueryEntryCount(connection, tx);
                using (var metaCmd = connection.CreateCommand())
                {
                    metaCmd.Transaction = tx;
                    metaCmd.CommandText = """
                        INSERT INTO meta (key, value) VALUES ('entry_count', $value)
                        ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                        """;
                    metaCmd.Parameters.AddWithValue("$value", total.ToString());
                    metaCmd.ExecuteNonQuery();
                }

                tx.Commit();
                return new IncrementalApplyResult(added, removed, total);
            }
        }

        internal static string? NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var trimmed = path.Trim();
            if (!trimmed.StartsWith(@"\\", StringComparison.Ordinal))
                return null;

            return trimmed.TrimEnd('\\');
        }

        private static SqliteConnection OpenReadWriteConnection(string dbPath) =>
            OpenConnection(dbPath, SqliteOpenMode.ReadWrite);

        private static SqliteConnection OpenReadOnlyConnection(string dbPath) =>
            OpenConnection(dbPath, SqliteOpenMode.ReadOnly);

        private static SqliteConnection OpenConnection(string dbPath, SqliteOpenMode mode)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = mode,
                Cache = SqliteCacheMode.Default
            };

            var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            return connection;
        }

        private static int QueryEntryCount(SqliteConnection connection, SqliteTransaction? tx = null)
        {
            using var cmd = connection.CreateCommand();
            if (tx != null)
                cmd.Transaction = tx;

            cmd.CommandText = "SELECT COUNT(*) FROM folders_fts;";
            return Convert.ToInt32(cmd.ExecuteScalar());
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
            if (value.Length > 0 && "=+-@".Contains(value[0]))
                value = "'" + value;

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

        private static List<string> NormalizeRootShares(IReadOnlyList<string>? rootShares) =>
            NormalizeRootSharesStatic(rootShares);

        private static List<string> NormalizeRootSharesStatic(IReadOnlyList<string>? rootShares)
        {
            if (rootShares == null || rootShares.Count == 0)
                return new List<string>();

            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in rootShares)
            {
                var path = ShareIndexer.NormalizeUncRoot(root);
                if (path != null)
                    normalized.Add(path);
            }

            return normalized.ToList();
        }

        internal static string BuildFtsQuery(string userInput)
        {
            var tokens = userInput.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
                return string.Empty;

            var parts = new List<string>(tokens.Length);
            foreach (var token in tokens)
            {
                var cleaned = SanitizeFtsToken(token);
                if (string.IsNullOrEmpty(cleaned))
                    continue;

                parts.Add($"\"{cleaned}\"*");
            }

            return parts.Count == 0 ? string.Empty : string.Join(" AND ", parts);
        }

        internal static string SanitizeFtsToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            var chars = token.Where(c =>
                c != '"' &&
                c != '*' &&
                c != '(' &&
                c != ')' &&
                c != ':' &&
                c != '-' &&
                c != '^' &&
                c != '+').ToArray();

            return new string(chars).Trim();
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
