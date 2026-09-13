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

        public Task RebuildAsync(
            IReadOnlyList<string> shareRoots,
            AppSettings settings,
            Action? releaseLiveIndexLocks = null,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_runningTask is { IsCompleted: false })
                    throw new InvalidOperationException("An index rebuild is already running.");

                _runningTask = Task.Run(
                    async () => await RunRebuildAsync(shareRoots, settings, releaseLiveIndexLocks, cancellationToken),
                    cancellationToken);
                return _runningTask;
            }
        }

        private async Task RunRebuildAsync(
            IReadOnlyList<string> shareRoots,
            AppSettings settings,
            Action? releaseLiveIndexLocks,
            CancellationToken cancellationToken)
        {
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
                        continue;
                    }

                    store.InsertBatch(new[]
                    {
                        new FolderEntry
                        {
                            Name = Path.GetFileName(normalizedRoot.TrimEnd('\\')) is { Length: > 0 } n ? n : normalizedRoot,
                            Path = normalizedRoot,
                            RootShare = normalizedRoot
                        }
                    });
                    total++;

                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint
                    };

                    foreach (var dir in Directory.EnumerateDirectories(normalizedRoot, "*", options))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        batch.Add(new FolderEntry
                        {
                            Name = Path.GetFileName(dir),
                            Path = dir,
                            RootShare = normalizedRoot
                        });

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

                Report(new IndexProgress { FoldersIndexed = total, CurrentRoot = "Saving index…", ElapsedSeconds = sw.Elapsed.TotalSeconds });

                store.SetMeta("built_at", DateTime.Now.ToString("O"));
                store.SetMeta("entry_count", total.ToString());
                store.Close();

                releaseLiveIndexLocks?.Invoke();
                IndexFileHelper.SwapDatabaseFiles(buildingPath, livePath, releaseLiveIndexLocks);

                sw.Stop();
                settings.LastIndexedAt = DateTime.Now;
                settings.LastIndexEntryCount = total;
                settings.LastIndexDurationSeconds = sw.Elapsed.TotalSeconds;
                settings.LastIndexError = null;
                settings.Save();

                Diagnostics.Log($"Index rebuild complete: {total} folders in {sw.Elapsed.TotalSeconds:F1}s");
                Report(new IndexProgress { FoldersIndexed = total, IsComplete = true, ElapsedSeconds = sw.Elapsed.TotalSeconds });
            }
            catch (OperationCanceledException)
            {
                Diagnostics.Log("Index rebuild cancelled.");
                try { IndexFileHelper.DeleteDatabaseFiles(buildingPath); } catch { /* ignore */ }
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                settings.LastIndexError = ex.Message;
                settings.Save();
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
