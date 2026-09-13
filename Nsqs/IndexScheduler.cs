using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nsqs
{
    public sealed class IndexScheduler : IDisposable
    {
        private readonly ShareIndexer _indexer;
        private readonly Func<AppSettings> _getSettings;
        private readonly Action _beforeRebuild;
        private readonly Action _onIndexCompleted;
        private readonly Func<CancellationToken> _getCancellationToken;
        private CancellationTokenSource? _timerCts;
        private readonly object _lock = new();
        private bool _disposed;

        public IndexScheduler(
            ShareIndexer indexer,
            Func<AppSettings> getSettings,
            Action beforeRebuild,
            Action onIndexCompleted,
            Func<CancellationToken> getCancellationToken)
        {
            _indexer = indexer;
            _getSettings = getSettings;
            _beforeRebuild = beforeRebuild;
            _onIndexCompleted = onIndexCompleted;
            _getCancellationToken = getCancellationToken;
        }

        public DateTime? NextScheduledRun { get; private set; }

        public void Reschedule()
        {
            lock (_lock)
            {
                CancelTimerLocked();

                var settings = _getSettings();
                if (!settings.IndexSchedule.Enabled || settings.ShareRoots.Count == 0)
                {
                    NextScheduledRun = null;
                    return;
                }

                NextScheduledRun = ComputeNextRun(settings.IndexSchedule, DateTime.Now);
                var delay = NextScheduledRun.Value - DateTime.Now;
                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;

                _timerCts = new CancellationTokenSource();
                var token = _timerCts.Token;
                Diagnostics.Log($"Next index scheduled for {NextScheduledRun:yyyy-MM-dd HH:mm:ss} (in {delay:g})");
                _ = WaitAndRunAsync(delay, token);
            }
        }

        public async Task CheckMissedIndexOnStartupAsync()
        {
            var settings = _getSettings();
            if (!settings.RunMissedIndexOnStartup || !settings.IndexSchedule.Enabled || settings.ShareRoots.Count == 0)
                return;

            if (_indexer.IsRunning)
                return;

            var lastDue = ComputeLastDueRun(settings.IndexSchedule, DateTime.Now);
            if (lastDue == null)
                return;

            if (settings.LastIndexedAt.HasValue && settings.LastIndexedAt.Value >= lastDue.Value)
                return;

            Diagnostics.Log($"Missed scheduled index detected (due {lastDue:yyyy-MM-dd HH:mm:ss}); rebuilding now.");
            await RunIndexAsync();
        }

        private async Task WaitAndRunAsync(TimeSpan delay, CancellationToken token)
        {
            try
            {
                await Task.Delay(delay, token);
                await RunIndexAsync();
            }
            catch (OperationCanceledException)
            {
                // Rescheduled or shutting down.
            }
        }

        private async Task RunIndexAsync()
        {
            if (_indexer.IsRunning)
                return;

            var settings = _getSettings();
            if (settings.ShareRoots.Count == 0)
                return;

            try
            {
                _beforeRebuild();
                await _indexer.RebuildAsync(settings.ShareRoots, settings, _beforeRebuild, _getCancellationToken());
                _onIndexCompleted();
            }
            catch (OperationCanceledException)
            {
                // Ignore.
            }
            finally
            {
                if (!_disposed && !_getCancellationToken().IsCancellationRequested)
                    Reschedule();
            }
        }

        public static DateTime ComputeNextRun(IndexScheduleSettings schedule, DateTime from)
        {
            var time = ParseTimeOfDay(schedule.TimeOfDay);

            if (schedule.Kind == IndexScheduleKind.Daily)
            {
                var candidate = from.Date + time;
                if (candidate <= from)
                    candidate = candidate.AddDays(1);
                return candidate;
            }

            int daysUntil = ((int)schedule.DayOfWeek - (int)from.DayOfWeek + 7) % 7;
            var weekly = from.Date.AddDays(daysUntil) + time;
            if (weekly <= from)
                weekly = weekly.AddDays(7);
            return weekly;
        }

        public static DateTime? ComputeLastDueRun(IndexScheduleSettings schedule, DateTime from)
        {
            if (!schedule.Enabled)
                return null;

            var time = ParseTimeOfDay(schedule.TimeOfDay);

            if (schedule.Kind == IndexScheduleKind.Daily)
            {
                var today = from.Date + time;
                return today <= from ? today : today.AddDays(-1);
            }

            int daysSince = ((int)from.DayOfWeek - (int)schedule.DayOfWeek + 7) % 7;
            var candidate = from.Date.AddDays(-daysSince) + time;
            if (candidate > from)
                candidate = candidate.AddDays(-7);
            return candidate;
        }

        private static TimeSpan ParseTimeOfDay(string value)
        {
            return TimeSpan.TryParse(value, out var time) ? time : new TimeSpan(19, 0, 0);
        }

        private void CancelTimerLocked()
        {
            _timerCts?.Cancel();
            _timerCts?.Dispose();
            _timerCts = null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                CancelTimerLocked();
            }
        }
    }
}
