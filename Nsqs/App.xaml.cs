using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace Nsqs
{
    public partial class App : Application
    {
        public const string ProductName = "Network Share Quick Search (NSQS)";
        public const string ShortName = "NSQS";
        public static string SettingsWindowTitle => $"{ProductName} Settings";

        private const string SingleInstanceMutexName = "NSQS-SingleInstance-7C4A9E2F-1B3D-4F8A-9D6E-2A5B8C1D0E3F";

        private Mutex? _mutex;
        private readonly CancellationTokenSource _appLifetimeCts = new();
        private AppSettings _settings = new();
        private IndexStore _indexStore = new();
        private ShareIndexer _indexer = new();
        private ShareFolderWatcher? _folderWatcher;
        private IndexScheduler? _scheduler;
        private HotkeyManager? _hotkeyManager;
        private LauncherWindow? _launcherWindow;
        private MainWindow? _mainWindow;
        private SingleInstanceNotifier? _singleInstanceNotifier;
        private TrayIconManager? _trayIcon;
        private SettingsWindow? _settingsWindow;
        private bool _isExiting;
        private bool _isClosingMainWindowForModeChange;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                Diagnostics.Log($"UNHANDLED (dispatcher): {args.Exception}");
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Diagnostics.Log($"UNHANDLED (appdomain): {args.ExceptionObject}");
            };

            Diagnostics.Log("Startup begin");

            try
            {
                _mutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
                if (!createdNew)
                {
                    Diagnostics.Log("Another instance is already running; activating existing instance.");
                    SingleInstanceNotifier.TrySignalExistingInstance();
                    Shutdown();
                    return;
                }

                _singleInstanceNotifier = SingleInstanceNotifier.StartListening(Dispatcher, ActivateLauncherFromExternalRequest);

                ApplySystemAccent();
                StartupHelper.RepairPathIfEnabled();
                _settings = AppSettings.Load();

                EnsureIndexDatabase();
                _indexStore.OpenForSearch(AppPaths.IndexFile);

                _indexer.ProgressChanged += OnIndexProgress;
                _scheduler = new IndexScheduler(
                    _indexer,
                    () => _settings,
                    PrepareForIndexRebuild,
                    OnIndexDatabaseSwapped,
                    () => _appLifetimeCts.Token);

                _launcherWindow = new LauncherWindow(_indexStore, _settings);
                _hotkeyManager = new HotkeyManager();
                _hotkeyManager.HotkeyPressed += ToggleLauncher;
                RegisterHotkeyFromSettings();

                ApplyLaunchMode();
                if (!_settings.LaunchToTray)
                    ShowLauncher();

                _trayIcon = new TrayIconManager(_settings);
                _trayIcon.LauncherRequested += ActivateLauncherFromExternalRequest;
                _trayIcon.SettingsRequested += OpenSettings;
                _trayIcon.RebuildIndexRequested += RequestRebuildIndex;
                _trayIcon.ExitRequested += ExitApplication;

                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

                _scheduler.Reschedule();
                _ = StartupIndexChecksAsync();

                UpdateTrayStatus();
                Diagnostics.Log("Startup complete");
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Startup FAILED: {ex}");
                throw;
            }
        }

        private void EnsureIndexDatabase()
        {
            if (!System.IO.File.Exists(AppPaths.IndexFile))
                IndexStore.InitializeDatabase(AppPaths.IndexFile);
        }

        private async Task StartupIndexChecksAsync()
        {
            if (_scheduler == null) return;

            await _scheduler.CheckMissedIndexOnStartupAsync();

            if (_settings.ShareRoots.Count > 0 &&
                !_settings.LastIndexedAt.HasValue &&
                !_indexer.IsRunning)
            {
                Diagnostics.Log("No index yet; starting initial rebuild.");
                RequestRebuildIndex();
                return;
            }

            StartFolderWatcherIfReady();
        }

        private void StartFolderWatcherIfReady()
        {
            if (_indexer.IsRunning || _settings.ShareRoots.Count == 0)
                return;

            if (!File.Exists(AppPaths.IndexFile) || !_settings.LastIndexedAt.HasValue)
                return;

            _folderWatcher ??= new ShareFolderWatcher();
            _folderWatcher.Start(_settings.ShareRoots, OnFolderIndexChanged);
        }

        private void StopFolderWatcher()
        {
            _folderWatcher?.Stop();
        }

        private void OnFolderIndexChanged(int totalCount)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_appLifetimeCts.IsCancellationRequested)
                    return;

                if (_indexStore.IsOpen)
                    _indexStore.Reopen();

                _settings.LastIndexEntryCount = totalCount;
                _settings.Save();
                UpdateTrayStatus();
            });
        }

        private void RegisterHotkeyFromSettings()
        {
            if (_hotkeyManager == null) return;

            if (_hotkeyManager.TryRegister(_settings.Hotkey, out var error))
            {
                Diagnostics.Log($"Hotkey registered: {_settings.Hotkey}");
                return;
            }

            Diagnostics.Log($"Hotkey registration failed: {error}");
            _trayIcon?.SetStatus($"{ProductName} (hotkey unavailable)");
        }

        private void ToggleLauncher()
        {
            if (_launcherWindow == null) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (_launcherWindow.IsVisible)
                    _launcherWindow.HideLauncher();
                else
                    ShowLauncher();
            });
        }

        private void ShowLauncher()
        {
            _launcherWindow?.ShowLauncher();
        }

        private void ActivateLauncherFromExternalRequest()
        {
            if (_launcherWindow == null)
                return;

            if (_launcherWindow.IsVisible)
            {
                _launcherWindow.Activate();
                return;
            }

            ShowLauncher();
        }

        private void ApplyLaunchMode()
        {
            if (_settings.LaunchToTray)
            {
                if (_mainWindow != null)
                {
                    _isClosingMainWindowForModeChange = true;
                    try
                    {
                        DetachMainWindow();
                        _mainWindow.Close();
                    }
                    finally
                    {
                        _isClosingMainWindowForModeChange = false;
                    }

                    _mainWindow = null;
                }

                return;
            }

            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow();
                AttachMainWindow(_mainWindow);
            }

            _mainWindow.PrepareForTaskbar();
        }

        private void AttachMainWindow(MainWindow window)
        {
            window.ActivateRequested += OnMainWindowActivateRequested;
            window.Closed += OnMainWindowClosed;
        }

        private void DetachMainWindow()
        {
            if (_mainWindow == null)
                return;

            _mainWindow.ActivateRequested -= OnMainWindowActivateRequested;
            _mainWindow.Closed -= OnMainWindowClosed;
        }

        private void OnMainWindowClosed(object? sender, EventArgs e)
        {
            _mainWindow = null;

            if (_isClosingMainWindowForModeChange || _isExiting)
                return;

            Diagnostics.Log("Taskbar window closed; shutting down.");
            ExitApplication();
        }

        private void OnMainWindowActivateRequested()
        {
            ActivateLauncherFromExternalRequest();
        }

        private void ExitApplication()
        {
            if (_isExiting)
                return;

            _isExiting = true;
            Diagnostics.Log("Exit requested.");
            _appLifetimeCts.Cancel();
            _settingsWindow?.Close();
            _launcherWindow?.Hide();

            if (_mainWindow != null)
            {
                DetachMainWindow();
                _mainWindow.Close();
                _mainWindow = null;
            }

            Shutdown();
        }

        private void PrepareForIndexRebuild()
        {
            StopFolderWatcher();
            _indexStore.Close();
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SqliteConnection.ClearAllPools();
            Diagnostics.Log("Released live index locks for rebuild.");
        }

        private void EnsureIndexStoreOpen()
        {
            if (!_indexStore.IsOpen && File.Exists(AppPaths.IndexFile))
                _indexStore.OpenForSearch(AppPaths.IndexFile);
        }

        private void RequestRebuildIndex()
        {
            if (_indexer.IsRunning || _settings.ShareRoots.Count == 0)
                return;

            PrepareForIndexRebuild();
            _trayIcon?.SetRebuildEnabled(false);
            _ = _indexer.RebuildAsync(
                _settings.ShareRoots,
                _settings,
                PrepareForIndexRebuild,
                _appLifetimeCts.Token).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    Diagnostics.Log($"Rebuild task faulted: {t.Exception.GetBaseException().Message}");

                Dispatcher.BeginInvoke(() =>
                {
                    if (_appLifetimeCts.IsCancellationRequested)
                        return;

                    EnsureIndexStoreOpen();
                    _settings = AppSettings.Load();
                    _trayIcon?.SetRebuildEnabled(true);
                    UpdateTrayStatus();
                    StartFolderWatcherIfReady();
                });
            }, TaskScheduler.Default);
        }

        private void OnIndexDatabaseSwapped()
        {
            _indexStore.Close();
            _indexStore.OpenForSearch(AppPaths.IndexFile);
            _settings = AppSettings.Load();
            UpdateTrayStatus();
            StartFolderWatcherIfReady();
        }

        private void OnIndexProgress(IndexProgress progress)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_appLifetimeCts.IsCancellationRequested)
                    return;

                if (progress.IsComplete)
                {
                    if (!progress.IsFailed)
                        OnIndexDatabaseSwapped();
                    else
                    {
                        EnsureIndexStoreOpen();
                        StartFolderWatcherIfReady();
                    }

                    _trayIcon?.SetRebuildEnabled(true);
                }

                UpdateTrayStatus(progress.IsComplete ? null : progress);
            });
        }

        private void UpdateTrayStatus(IndexProgress? progress = null)
        {
            if (_trayIcon == null) return;

            if (progress != null)
            {
                var elapsed = FormatElapsed(progress.ElapsedSeconds);
                _trayIcon.SetStatus(progress.CurrentRoot == null
                    ? $"{ProductName}: indexing {progress.FoldersIndexed:N0} folders ({elapsed})"
                    : $"{ProductName}: {progress.FoldersIndexed:N0} folders ({elapsed})");
                return;
            }

            if (_settings.LastIndexedAt.HasValue)
            {
                _trayIcon.SetStatus($"{ProductName}: {_settings.LastIndexEntryCount:N0} folders — {_settings.Hotkey}");
            }
            else
            {
                _trayIcon.SetStatus($"{ProductName}: no index yet — {_settings.Hotkey}");
            }
        }

        private static string FormatElapsed(double seconds)
        {
            if (seconds < 60)
                return $"{seconds:F0}s";

            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : $"{span.Minutes}m {span.Seconds}s";
        }

        private void ApplySystemAccent()
        {
            var accent = ThemeHelper.GetSystemAccentColor();
            Resources["AccentBrush"] = new SolidColorBrush(accent);
            Resources["AccentHoverBrush"] = new SolidColorBrush(ThemeHelper.Lighten(accent, 0.15));
            Resources["AccentPressedBrush"] = new SolidColorBrush(ThemeHelper.Darken(accent, 0.2));
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color)
                return;

            Dispatcher.BeginInvoke(ApplySystemAccent);
        }

        private void OpenSettings()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(
                _settings,
                _indexer,
                _scheduler!,
                onSaved: () =>
                {
                    _settings = AppSettings.Load();
                    RegisterHotkeyFromSettings();
                    _scheduler?.Reschedule();
                    ApplyLaunchMode();
                    UpdateTrayStatus();
                    StartFolderWatcherIfReady();
                },
                requestRebuild: RequestRebuildIndex);

            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Diagnostics.Log("Application shutting down.");
            _appLifetimeCts.Cancel();

            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            _indexer.ProgressChanged -= OnIndexProgress;
            _folderWatcher?.Dispose();
            _scheduler?.Dispose();
            _hotkeyManager?.Dispose();
            _trayIcon?.Dispose();
            _singleInstanceNotifier?.Dispose();
            _indexStore.Dispose();
            _appLifetimeCts.Dispose();

            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
                _mutex.Dispose();
            }

            base.OnExit(e);
            Diagnostics.Log("Shutdown complete.");

            // Background NAS work can otherwise keep the process alive briefly after Exit.
            Environment.Exit(0);
        }
    }
}
