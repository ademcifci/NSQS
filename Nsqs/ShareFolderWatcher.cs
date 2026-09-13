using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nsqs
{
    public sealed class ShareFolderWatcher : IDisposable
    {
        private const int FlushDelayMs = 2000;
        private const int FlushRetryDelayMs = 5000;
        private const int WatcherBufferBytes = 65536;
        private const int PeriodicReconcileHours = 6;

        private readonly object _lock = new();
        private readonly HashSet<string> _pendingAdds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingRemoves = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingFullReconcileRoots = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingLightweightReconcileRoots = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly List<string> _shareRoots = new();

        private Timer? _flushTimer;
        private Timer? _restartTimer;
        private Timer? _periodicReconcileTimer;
        private Action<int>? _onIndexChanged;
        private bool _disposed;
        private int _errorRestartCount;
        private int _operationGeneration;
        private int _periodicReconcileRootIndex;

        public void Start(IReadOnlyList<string> shareRoots, Action<int> onIndexChanged)
        {
            lock (_lock)
            {
                StopWatchersLocked(clearPending: false);
                _onIndexChanged = onIndexChanged;
                _errorRestartCount = 0;
                _periodicReconcileRootIndex = 0;
                AttachWatchers(shareRoots);
                SchedulePeriodicReconcileLocked();
            }
        }

        public void Stop()
        {
            lock (_lock)
                StopWatchersLocked(clearPending: true);
        }

        public void Restart(IReadOnlyList<string> shareRoots, Action<int> onIndexChanged)
        {
            Start(shareRoots, onIndexChanged);
        }

        private void AttachWatchers(IReadOnlyList<string> shareRoots)
        {
            _shareRoots.Clear();

            foreach (var root in shareRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalizedRoot = ShareIndexer.NormalizeUncRoot(root);
                if (normalizedRoot == null)
                    continue;

                if (!Directory.Exists(normalizedRoot))
                {
                    Diagnostics.Log($"Share watch skipped (not reachable): {normalizedRoot}");
                    continue;
                }

                try
                {
                    var watcher = CreateWatcher(normalizedRoot);
                    _watchers.Add(watcher);
                    _shareRoots.Add(normalizedRoot);
                    watcher.EnableRaisingEvents = true;
                    Diagnostics.Log($"Watching share for folder changes: {normalizedRoot}");
                }
                catch (Exception ex)
                {
                    Diagnostics.Log($"Share watch failed for {normalizedRoot}: {ex.Message}");
                }
            }
        }

        private FileSystemWatcher CreateWatcher(string normalizedRoot)
        {
            var watcher = new FileSystemWatcher(normalizedRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.DirectoryName,
                InternalBufferSize = WatcherBufferBytes
            };

            watcher.Created += OnWatcherEvent;
            watcher.Deleted += OnWatcherEvent;
            watcher.Renamed += OnWatcherRenamed;
            watcher.Error += OnWatcherError;

            return watcher;
        }

        private void OnWatcherEvent(object sender, FileSystemEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.FullPath))
                return;

            switch (e.ChangeType)
            {
                case WatcherChangeTypes.Created:
                    QueueAdd(e.FullPath);
                    break;
                case WatcherChangeTypes.Deleted:
                    QueueRemove(e.FullPath);
                    break;
            }
        }

        private void OnWatcherRenamed(object sender, RenamedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.OldFullPath))
                QueueRemove(e.OldFullPath);

            if (!string.IsNullOrWhiteSpace(e.FullPath))
                QueueAdd(e.FullPath);
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Diagnostics.Log($"Share folder watch error: {e.GetException().Message}");

            lock (_lock)
            {
                if (_disposed || _onIndexChanged == null)
                    return;

                foreach (var root in _shareRoots)
                    _pendingFullReconcileRoots.Add(root);

                _errorRestartCount++;
                ScheduleWatcherRestartLocked();
            }
        }

        private void ScheduleWatcherRestartLocked()
        {
            _restartTimer?.Dispose();
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(_errorRestartCount, 5))));
            _restartTimer = new Timer(_ => RestartWatchersPreservePending(), null, delay, Timeout.InfiniteTimeSpan);
        }

        private void SchedulePeriodicReconcileLocked()
        {
            _periodicReconcileTimer?.Dispose();
            if (_shareRoots.Count == 0)
                return;

            _periodicReconcileTimer = new Timer(
                _ => QueuePeriodicReconcile(),
                null,
                TimeSpan.FromHours(PeriodicReconcileHours),
                TimeSpan.FromHours(PeriodicReconcileHours));
        }

        private void QueuePeriodicReconcile()
        {
            lock (_lock)
            {
                if (_disposed || _onIndexChanged == null || _shareRoots.Count == 0)
                    return;

                var root = _shareRoots[_periodicReconcileRootIndex % _shareRoots.Count];
                _periodicReconcileRootIndex++;
                _pendingLightweightReconcileRoots.Add(root);
                ScheduleFlushLocked();
                Diagnostics.Log($"Scheduled lightweight index validate for {root}");
            }
        }

        private void RestartWatchersPreservePending()
        {
            lock (_lock)
            {
                if (_disposed || _onIndexChanged == null)
                    return;

                var roots = _shareRoots.Count > 0
                    ? _shareRoots.ToList()
                    : _pendingFullReconcileRoots.Concat(_pendingLightweightReconcileRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var callback = _onIndexChanged;

                StopWatchersLocked(clearPending: false);
                AttachWatchers(roots);
                _onIndexChanged = callback;
                SchedulePeriodicReconcileLocked();
                ScheduleFlushLocked();
            }
        }

        private void QueueAdd(string path)
        {
            lock (_lock)
            {
                if (_disposed || _onIndexChanged == null)
                    return;

                var normalized = IndexStore.NormalizeDirectoryPath(path);
                if (normalized == null)
                    return;

                _pendingRemoves.Remove(normalized);
                _pendingAdds.Add(normalized);
                ScheduleFlushLocked();
            }
        }

        private void QueueRemove(string path)
        {
            lock (_lock)
            {
                if (_disposed || _onIndexChanged == null)
                    return;

                var normalized = IndexStore.NormalizeDirectoryPath(path);
                if (normalized == null)
                    return;

                _pendingAdds.Remove(normalized);
                _pendingRemoves.Add(normalized);
                ScheduleFlushLocked();
            }
        }

        private void ScheduleFlushLocked()
        {
            _flushTimer ??= new Timer(_ => FlushPendingChanges(), null, Timeout.Infinite, Timeout.Infinite);
            _flushTimer.Change(FlushDelayMs, Timeout.Infinite);
        }

        private void ScheduleFlushRetryLocked()
        {
            _flushTimer ??= new Timer(_ => FlushPendingChanges(), null, Timeout.Infinite, Timeout.Infinite);
            _flushTimer.Change(FlushRetryDelayMs, Timeout.Infinite);
        }

        private void FlushPendingChanges()
        {
            List<string> adds;
            List<string> removes;
            List<string> fullReconcileRoots;
            List<string> lightweightReconcileRoots;
            IReadOnlyList<string> roots;
            Action<int>? callback;
            int generation;

            lock (_lock)
            {
                if (_disposed || _onIndexChanged == null)
                    return;

                if (_pendingAdds.Count == 0 &&
                    _pendingRemoves.Count == 0 &&
                    _pendingFullReconcileRoots.Count == 0 &&
                    _pendingLightweightReconcileRoots.Count == 0)
                    return;

                adds = _pendingAdds.ToList();
                removes = _pendingRemoves.ToList();
                fullReconcileRoots = _pendingFullReconcileRoots.ToList();
                lightweightReconcileRoots = _pendingLightweightReconcileRoots.ToList();
                roots = _shareRoots.ToList();
                callback = _onIndexChanged;
                generation = _operationGeneration;

                _pendingAdds.Clear();
                _pendingRemoves.Clear();
                _pendingFullReconcileRoots.Clear();
                _pendingLightweightReconcileRoots.Clear();
            }

            try
            {
                var additions = new List<FolderEntry>();
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var path in adds)
                {
                    var rootShare = SharePathHelper.FindRootShare(path, roots);
                    if (rootShare == null)
                        continue;

                    foreach (var entry in ShareIndexer.EnumerateDirectoryEntries(path, rootShare))
                    {
                        if (seenPaths.Add(entry.Path))
                            additions.Add(entry);
                    }
                }

                foreach (var root in lightweightReconcileRoots)
                {
                    if (!Directory.Exists(root))
                        continue;

                    var indexedPaths = IndexStore.GetIndexedPathsForRoot(AppPaths.IndexFile, root);
                    ShareWatchReconciler.CollectStalePaths(indexedPaths, removes);
                }

                foreach (var root in fullReconcileRoots)
                {
                    if (!Directory.Exists(root))
                        continue;

                    var indexedPaths = IndexStore.GetIndexedPathsForRoot(AppPaths.IndexFile, root);
                    ShareWatchReconciler.CollectChanges(
                        ShareIndexer.EnumerateDirectoryEntries(root, root),
                        indexedPaths,
                        seenPaths,
                        additions,
                        removes);
                }

                if (!TryCommitFlush(generation, additions, removes, callback))
                    RequeueFailedFlush(adds, removes, fullReconcileRoots, lightweightReconcileRoots);
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Share folder watch flush failed: {ex.Message}");
                RequeueFailedFlush(adds, removes, fullReconcileRoots, lightweightReconcileRoots);
            }
        }

        private bool TryCommitFlush(
            int generation,
            List<FolderEntry> additions,
            List<string> removes,
            Action<int>? callback)
        {
            lock (_lock)
            {
                if (_disposed || _onIndexChanged == null || generation != _operationGeneration)
                    return false;
            }

            var result = IndexStore.ApplyIncrementalChanges(AppPaths.IndexFile, additions, removes);
            if (result.Added == 0 && result.Removed == 0)
                return true;

            Diagnostics.Log(
                $"Index updated from share watch: +{result.Added}, -{result.Removed}, total {result.TotalCount:N0}");
            callback?.Invoke(result.TotalCount);
            return true;
        }

        private void RequeueFailedFlush(
            IReadOnlyList<string> adds,
            IReadOnlyList<string> removes,
            IReadOnlyList<string> fullReconcileRoots,
            IReadOnlyList<string> lightweightReconcileRoots)
        {
            lock (_lock)
            {
                if (_disposed || _onIndexChanged == null)
                    return;

                foreach (var path in adds)
                    _pendingAdds.Add(path);

                foreach (var path in removes)
                    _pendingRemoves.Add(path);

                foreach (var root in fullReconcileRoots)
                    _pendingFullReconcileRoots.Add(root);

                foreach (var root in lightweightReconcileRoots)
                    _pendingLightweightReconcileRoots.Add(root);

                ScheduleFlushRetryLocked();
            }
        }

        private void StopWatchersLocked(bool clearPending)
        {
            _operationGeneration++;
            _flushTimer?.Dispose();
            _flushTimer = null;
            _restartTimer?.Dispose();
            _restartTimer = null;
            _periodicReconcileTimer?.Dispose();
            _periodicReconcileTimer = null;

            if (clearPending)
            {
                _pendingAdds.Clear();
                _pendingRemoves.Clear();
                _pendingFullReconcileRoots.Clear();
                _pendingLightweightReconcileRoots.Clear();
            }

            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnWatcherEvent;
                watcher.Deleted -= OnWatcherEvent;
                watcher.Renamed -= OnWatcherRenamed;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
            }

            _watchers.Clear();
            _shareRoots.Clear();

            if (clearPending)
                _onIndexChanged = null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                StopWatchersLocked(clearPending: true);
            }
        }
    }
}
