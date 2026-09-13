using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nsqs
{
    public sealed class IndexProgress
    {
        public int FoldersIndexed { get; init; }
        public string? CurrentRoot { get; init; }
        public bool IsComplete { get; init; }
        public bool IsFailed { get; init; }
        public string? ErrorMessage { get; init; }
        public double ElapsedSeconds { get; init; }
    }

    public sealed class ShareIndexer
    {
        private const int BatchSize = 500;
        private const int ProgressInterval = 100;
        private readonly object _gate = new();
        private Task? _runningTask;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return _runningTask is { IsCompleted: false };
            }
        }

        public event Action<IndexProgress>? ProgressChanged;

        public bool TryRebuildAsync(
            IReadOnlyList<string> shareRoots,
            Action<IndexMetadata>? persistIndexMetadata = null,
            Action? releaseLiveIndexLocks = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_runningTask is { IsCompleted: false })
                    return false;

                _runningTask = Task.Run(
                    async () => await RunRebuildAsync(shareRoots, persistIndexMetadata, releaseLiveIndexLocks, cancellationToken),
                    cancellationToken);
                return true;
            }
        }

        public Task WaitForCurrentRebuildAsync()
        {
            lock (_gate)
            {
                return _runningTask ?? Task.CompletedTask;
            }
        }

        [Obsolete("Use TryRebuildAsync.")]
        public Task RebuildAsync(
            IReadOnlyList<string> shareRoots,
            AppSettings settings,
            Action? releaseLiveIndexLocks = null,
            CancellationToken cancellationToken = default)
        {
            if (!TryRebuildAsync(shareRoots, null, releaseLiveIndexLocks, cancellationToken))
                throw new InvalidOperationException("An index rebuild is already running.");

            return _runningTask!;
        }

        public static IEnumerable<FolderEntry> EnumerateDirectoryEntries(string directoryPath, string normalizedRoot)
        {
            var normalizedDirectory = IndexStore.NormalizeDirectoryPath(directoryPath);
            var root = NormalizeUncRoot(normalizedRoot);
            if (normalizedDirectory == null || root == null)
                yield break;

            if (!Directory.Exists(normalizedDirectory))
                yield break;

            yield return CreateFolderEntry(normalizedDirectory, root);

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var dir in Directory.EnumerateDirectories(normalizedDirectory, "*", options))
                yield return CreateFolderEntry(dir, root);
        }

        internal static FolderEntry CreateFolderEntry(string path, string normalizedRoot)
        {
            var normalizedPath = IndexStore.NormalizeDirectoryPath(path);
            var normalizedRootPath = IndexStore.NormalizeDirectoryPath(normalizedRoot);
            if (normalizedPath == null || normalizedRootPath == null)
            {
                return new FolderEntry
                {
                    Name = normalizedRoot.TrimEnd('\\'),
                    Path = normalizedRoot,
                    RootShare = normalizedRoot
                };
            }

            var storedPath = string.Equals(normalizedPath, normalizedRootPath, StringComparison.OrdinalIgnoreCase)
                ? normalizedRoot
                : normalizedPath;

            var name = Path.GetFileName(normalizedPath);
            if (string.IsNullOrEmpty(name))
                name = Path.GetFileName(normalizedRoot.TrimEnd('\\'));

            return new FolderEntry
            {
                Name = name,
                Path = storedPath,
                RootShare = normalizedRoot
            };
        }

        internal static bool ShouldAbortRebuild(int reachableRootCount) => reachableRootCount == 0;

        internal static string BuildAllRootsUnreachableMessage() =>
            "All share roots were unreachable; existing index kept.";

        internal static string BuildSkippedRootsWarning(IReadOnlyList<string> skippedRoots) =>
            skippedRoots.Count == 0
                ? string.Empty
                : $"Skipped unreachable shares: {string.Join(", ", skippedRoots)}";

        private async Task RunRebuildAsync(
            IReadOnlyList<string> shareRoots,
            Action<IndexMetadata>? persistIndexMetadata,
            Action? releaseLiveIndexLocks,
            CancellationToken cancellationToken)
        {
            void SaveMetadata(IndexMetadata metadata)
            {
                persistIndexMetadata?.Invoke(metadata);
            }
            // Never block the UI thread — NAS enumeration can run for a long time.
            await Task.Yield();

            var sw = Stopwatch.StartNew();
            var buildingPath = AppPaths.IndexBuildingFile;
            var livePath = AppPaths.IndexFile;

            try
            {
                Report(new IndexProgress { CurrentRoot = "Preparing index…", ElapsedSeconds = 0 });

                if (File.Exists(buildingPath))
                    IndexFileHelper.DeleteDatabaseFiles(buildingPath);

                IndexStore.InitializeDatabase(buildingPath);

                using var store = new IndexStore();
                store.OpenForWrite(buildingPath);

                int total = 0;
                int reachableRootCount = 0;
                var skippedRoots = new List<string>();
                var batch = new List<FolderEntry>(BatchSize);

                foreach (var root in shareRoots)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var normalizedRoot = NormalizeUncRoot(root);
                    if (normalizedRoot == null)
                    {
                        Diagnostics.Log($"Skipping invalid share root: {root}");
                        continue;
                    }

                    Report(new IndexProgress { FoldersIndexed = total, CurrentRoot = normalizedRoot });

                    if (!Directory.Exists(normalizedRoot))
                    {
                        Diagnostics.Log($"Share root not reachable: {normalizedRoot}");
                        skippedRoots.Add(normalizedRoot);
                        continue;
                    }

                    reachableRootCount++;

                    store.InsertBatch(new[] { CreateFolderEntry(normalizedRoot, normalizedRoot) });
                    total++;

                    foreach (var entry in EnumerateDirectoryEntries(normalizedRoot, normalizedRoot))
                    {
                        if (string.Equals(entry.Path, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(IndexStore.NormalizeDirectoryPath(entry.Path),
                                IndexStore.NormalizeDirectoryPath(normalizedRoot),
                                StringComparison.OrdinalIgnoreCase))
                            continue;

                        cancellationToken.ThrowIfCancellationRequested();

                        batch.Add(entry);

                        var pending = total + batch.Count;
                        if (batch.Count >= BatchSize)
                        {
                            store.InsertBatch(batch);
                            total += batch.Count;
                            batch.Clear();
                            Report(new IndexProgress { FoldersIndexed = total, CurrentRoot = normalizedRoot, ElapsedSeconds = sw.Elapsed.TotalSeconds });
                            await Task.Yield();
                        }
                        else if (pending % ProgressInterval == 0)
                        {
                            Report(new IndexProgress { FoldersIndexed = pending, CurrentRoot = normalizedRoot, ElapsedSeconds = sw.Elapsed.TotalSeconds });
                            await Task.Yield();
                        }
                    }
                }

                if (batch.Count > 0)
                {
                    store.InsertBatch(batch);
                    total += batch.Count;
                    batch.Clear();
                }

                if (ShouldAbortRebuild(reachableRootCount))
                {
                    store.Close();
                    IndexFileHelper.DeleteDatabaseFiles(buildingPath);

                    var message = BuildAllRootsUnreachableMessage();
                    SaveMetadata(new IndexMetadata { LastIndexError = message });
                    Diagnostics.Log(message);
                    Report(new IndexProgress
                    {
                        IsFailed = true,
                        ErrorMessage = message,
                        IsComplete = true,
                        ElapsedSeconds = sw.Elapsed.TotalSeconds
                    });
                    return;
                }

                if (skippedRoots.Count > 0 && File.Exists(livePath))
                {
                    var preserved = IndexStore.ReadEntriesForRoots(livePath, skippedRoots);
                    if (preserved.Count > 0)
                    {
                        store.InsertBatch(preserved);
                        total += preserved.Count;
                        Diagnostics.Log($"Preserved {preserved.Count} folders from {skippedRoots.Count} unreachable share(s).");
                    }
                }

                Report(new IndexProgress { FoldersIndexed = total, CurrentRoot = "Saving index…", ElapsedSeconds = sw.Elapsed.TotalSeconds });

                store.SetMeta("built_at", DateTime.Now.ToString("O"));
                store.SetMeta("entry_count", total.ToString());
                store.Close();

                IndexFileHelper.SwapDatabaseFiles(buildingPath, livePath, releaseLiveIndexLocks);

                sw.Stop();
                SaveMetadata(new IndexMetadata
                {
                    LastIndexedAt = DateTime.Now,
                    LastIndexEntryCount = total,
                    LastIndexDurationSeconds = sw.Elapsed.TotalSeconds,
                    LastIndexError = skippedRoots.Count > 0 ? BuildSkippedRootsWarning(skippedRoots) : null,
                    ClearLastIndexError = skippedRoots.Count == 0
                });

                Diagnostics.Log($"Index rebuild complete: {total} folders in {sw.Elapsed.TotalSeconds:F1}s");
                Report(new IndexProgress { FoldersIndexed = total, IsComplete = true, ElapsedSeconds = sw.Elapsed.TotalSeconds });
            }
            catch (OperationCanceledException)
            {
                Diagnostics.Log("Index rebuild cancelled.");
                try { IndexFileHelper.DeleteDatabaseFiles(buildingPath); } catch { /* ignore */ }
                Report(new IndexProgress
                {
                    IsFailed = true,
                    ErrorMessage = "Index rebuild cancelled.",
                    IsComplete = true,
                    ElapsedSeconds = sw.Elapsed.TotalSeconds
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                SaveMetadata(new IndexMetadata { LastIndexError = ex.Message });
                Diagnostics.Log($"Index rebuild failed: {ex}");
                Report(new IndexProgress { IsFailed = true, ErrorMessage = ex.Message, IsComplete = true });
                try { IndexFileHelper.DeleteDatabaseFiles(buildingPath); } catch { /* ignore */ }
            }
        }

        internal static string? NormalizeUncRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return null;

            var trimmed = root.Trim();
            if (!trimmed.StartsWith(@"\\", StringComparison.Ordinal))
                return null;

            return trimmed.TrimEnd('\\') + "\\";
        }

        private void Report(IndexProgress progress) => ProgressChanged?.Invoke(progress);
    }
}
