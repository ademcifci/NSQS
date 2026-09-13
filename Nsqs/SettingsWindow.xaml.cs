using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Nsqs
{
    public partial class SettingsWindow : Window
    {
        private AppSettings _settings;
        private readonly Func<AppSettings> _getSettings;
        private readonly ShareIndexer _indexer;
        private readonly IndexScheduler _scheduler;
        private readonly Action _onSaved;
        private readonly Action _requestRebuild;

        public SettingsWindow(
            Func<AppSettings> getSettings,
            ShareIndexer indexer,
            IndexScheduler scheduler,
            Action onSaved,
            Action requestRebuild)
        {
            InitializeComponent();
            Icon = IconFactory.CreateWindowIconSource();
            ApplyWindowTheme();

            _getSettings = getSettings;
            _indexer = indexer;
            _scheduler = scheduler;
            _onSaved = onSaved;
            _requestRebuild = requestRebuild;
            _settings = CloneSettings(getSettings());

            _indexer.ProgressChanged += OnIndexProgress;

            LoadFromSettings();
            RefreshStatus();
        }

        private static AppSettings CloneSettings(AppSettings source)
        {
            return new AppSettings
            {
                ShareRoots = source.ShareRoots.ToList(),
                Hotkey = source.Hotkey,
                StartWithWindows = source.StartWithWindows,
                LaunchToTray = source.LaunchToTray,
                IndexSchedule = new IndexScheduleSettings
                {
                    Enabled = source.IndexSchedule.Enabled,
                    Kind = source.IndexSchedule.Kind,
                    DayOfWeek = source.IndexSchedule.DayOfWeek,
                    TimeOfDay = source.IndexSchedule.TimeOfDay
                },
                RunMissedIndexOnStartup = source.RunMissedIndexOnStartup,
                MaxResults = source.MaxResults,
                LastSearchShareRoots = source.LastSearchShareRoots.ToList(),
                LastIndexedAt = source.LastIndexedAt,
                LastIndexEntryCount = source.LastIndexEntryCount,
                LastIndexDurationSeconds = source.LastIndexDurationSeconds,
                LastIndexError = source.LastIndexError
            };
        }

        private void ReloadSettingsFromSource()
        {
            _settings = CloneSettings(_getSettings());
        }

        private void LoadFromSettings()
        {
            ShareRootsList.ItemsSource = _settings.ShareRoots.ToList();
            HotkeyText.Text = _settings.Hotkey;
            MaxResultsText.Text = _settings.MaxResults.ToString();
            ScheduleEnabledCheck.IsChecked = _settings.IndexSchedule.Enabled;
            ScheduleKindCombo.SelectedIndex = _settings.IndexSchedule.Kind == IndexScheduleKind.Weekly ? 1 : 0;
            DayOfWeekCombo.SelectedIndex = (int)_settings.IndexSchedule.DayOfWeek;
            ScheduleTimeText.Text = AppSettings.FormatScheduleTime(_settings.GetScheduleTimeOfDay());
            RunMissedCheck.IsChecked = _settings.RunMissedIndexOnStartup;
            LaunchToTrayCheck.IsChecked = _settings.LaunchToTray;
            StartWithWindowsCheck.IsChecked = _settings.StartWithWindows;
            UpdateDayOfWeekVisibility();
        }

        private void RefreshStatus()
        {
            if (_settings.LastIndexedAt.HasValue)
            {
                LastIndexedText.Text =
                    $"Last indexed: {_settings.LastIndexedAt:yyyy-MM-dd HH:mm} — {_settings.LastIndexEntryCount:N0} folders ({_settings.LastIndexDurationSeconds:F0}s)";
            }
            else
            {
                LastIndexedText.Text = "Last indexed: never";
            }

            if (!string.IsNullOrEmpty(_settings.LastIndexError))
                LastIndexedText.Text += $"\nLast error: {_settings.LastIndexError}";

            NextRunText.Text = _scheduler.NextScheduledRun.HasValue
                ? $"Next scheduled run: {_scheduler.NextScheduledRun:yyyy-MM-dd HH:mm}"
                : "Next scheduled run: (not scheduled)";

            RebuildButton.IsEnabled = !_indexer.IsRunning;
            ExportIndexButton.IsEnabled = !_indexer.IsRunning && CanExportIndex();
            if (_indexer.IsRunning)
                ShowIndexProgress(new IndexProgress { CurrentRoot = "Starting…" });
            else
                HideIndexProgress();
        }

        private void ShowIndexProgress(IndexProgress progress)
        {
            IndexProgressPanel.Visibility = Visibility.Visible;
            IndexProgressBar.IsIndeterminate = true;
            IndexProgressText.Text = progress.FoldersIndexed > 0
                ? $"{progress.FoldersIndexed:N0} folders indexed"
                : "Indexing network shares…";
            IndexProgressDetailText.Text = BuildProgressDetail(progress);
        }

        private void HideIndexProgress()
        {
            IndexProgressPanel.Visibility = Visibility.Collapsed;
            IndexProgressText.Text = string.Empty;
            IndexProgressDetailText.Text = string.Empty;
        }

        private static string BuildProgressDetail(IndexProgress progress)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(progress.CurrentRoot))
                parts.Add(progress.CurrentRoot);

            if (progress.ElapsedSeconds > 0)
                parts.Add(FormatElapsed(progress.ElapsedSeconds));

            return string.Join(" · ", parts);
        }

        private static string FormatElapsed(double seconds)
        {
            if (seconds < 60)
                return $"{seconds:F0}s elapsed";

            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m elapsed"
                : $"{span.Minutes}m {span.Seconds}s elapsed";
        }

        private void OnIndexProgress(IndexProgress progress)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ReloadSettingsFromSource();

                if (progress.IsComplete)
                {
                    HideIndexProgress();
                    LoadFromSettings();
                    RefreshStatus();
                    return;
                }

                ShowIndexProgress(progress);
                RefreshStatus();
                RebuildButton.IsEnabled = false;
                ExportIndexButton.IsEnabled = false;
            });
        }

        private bool CanExportIndex() =>
            File.Exists(AppPaths.IndexFile) && _settings.LastIndexEntryCount > 0;

        private void ScheduleKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDayOfWeekVisibility();
        }

        private void UpdateDayOfWeekVisibility()
        {
            if (DayOfWeekRow == null) return;
            DayOfWeekRow.Visibility = ScheduleKindCombo.SelectedIndex == 1
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void BrowseShare_Click(object sender, RoutedEventArgs e)
        {
            var existing = ShareRootsList.SelectedItem as string
                ?? (ShareRootsList.ItemsSource as List<string>)?.FirstOrDefault()
                ?? NewShareText.Text;

            var picked = SharePathPicker.Browse(this, existing);
            if (picked == null)
                return;

            NewShareText.Text = picked;
            TryAddSharePath(picked);
        }

        private void AddShare_Click(object sender, RoutedEventArgs e)
        {
            var path = NewShareText.Text.Trim();
            if (string.IsNullOrEmpty(path))
                return;

            TryAddSharePath(path);
        }

        private void TryAddSharePath(string path)
        {
            var normalized = SharePathPicker.NormalizePickedPath(path)
                ?? ShareIndexer.NormalizeUncRoot(path);

            if (normalized == null)
            {
                System.Windows.MessageBox.Show(this,
                    "Select a network share folder (UNC path starting with \\\\). Local folders are not indexed.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var list = ShareRootsList.ItemsSource as List<string> ?? new List<string>();
            if (!list.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                list.Add(normalized);

            ShareRootsList.ItemsSource = null;
            ShareRootsList.ItemsSource = list;
            ShareRootsList.SelectedItem = normalized;
            NewShareText.Text = normalized;
        }

        private void RemoveShare_Click(object sender, RoutedEventArgs e)
        {
            if (ShareRootsList.SelectedItem is not string selected)
                return;

            var list = ShareRootsList.ItemsSource as List<string> ?? new List<string>();
            list.Remove(selected);
            ShareRootsList.ItemsSource = null;
            ShareRootsList.ItemsSource = list;
        }

        private void RebuildButton_Click(object sender, RoutedEventArgs e)
        {
            _requestRebuild();
            RefreshStatus();
        }

        private void ExportIndexButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanExportIndex())
            {
                System.Windows.MessageBox.Show(this,
                    "There is no index to export yet. Rebuild the index first.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                DefaultExt = "csv",
                FileName = $"NSQS-index-{DateTime.Now:yyyy-MM-dd}.csv"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                var count = IndexStore.ExportToCsv(AppPaths.IndexFile, dialog.FileName);
                System.Windows.MessageBox.Show(this,
                    $"Exported {count:N0} folders to:\n{dialog.FileName}",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Index export failed: {ex.Message}");
                System.Windows.MessageBox.Show(this,
                    $"Export failed:\n{ex.Message}",
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool ApplyToSettings()
        {
            var roots = (ShareRootsList.ItemsSource as List<string>)?.ToList() ?? new List<string>();

            if (!TimeSpan.TryParse(ScheduleTimeText.Text.Trim(), out var scheduleTime))
            {
                System.Windows.MessageBox.Show(this,
                    "Enter a valid time in HH:mm format (e.g. 19:00 or 13:00).",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var hotkey = HotkeyText.Text.Trim();
            if (!HotkeyParser.TryParse(hotkey, out _, out _, out var hotkeyError))
            {
                System.Windows.MessageBox.Show(this,
                    hotkeyError ?? "Enter a valid hotkey (e.g. Ctrl+Shift+Space).",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!int.TryParse(MaxResultsText.Text.Trim(), out var maxResults) || maxResults < 1 || maxResults > 500)
            {
                System.Windows.MessageBox.Show(this,
                    "Enter max search results between 1 and 500.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _settings.ShareRoots = roots;
            _settings.Hotkey = hotkey;
            _settings.MaxResults = maxResults;
            _settings.IndexSchedule.Enabled = ScheduleEnabledCheck.IsChecked == true;
            _settings.IndexSchedule.Kind = ScheduleKindCombo.SelectedIndex == 1
                ? IndexScheduleKind.Weekly
                : IndexScheduleKind.Daily;
            _settings.IndexSchedule.DayOfWeek = (DayOfWeek)DayOfWeekCombo.SelectedIndex;
            _settings.IndexSchedule.TimeOfDay = AppSettings.FormatScheduleTimeWithSeconds(scheduleTime);
            _settings.RunMissedIndexOnStartup = RunMissedCheck.IsChecked == true;
            _settings.LaunchToTray = LaunchToTrayCheck.IsChecked == true;
            _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
            AppSettings.PruneSearchShareRoots(_settings);
            return true;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var previousRoots = _getSettings().ShareRoots.ToList();

            if (!ApplyToSettings())
                return;

            AppSettingsManager.Save(_settings);
            StartupHelper.SetEnabled(_settings.StartWithWindows);

            var removedRoots = previousRoots
                .Where(root => !_settings.ShareRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (removedRoots.Count > 0 && File.Exists(AppPaths.IndexFile))
            {
                try
                {
                    var purgeResult = IndexStore.PurgeShareRoots(AppPaths.IndexFile, removedRoots);
                    if (purgeResult.Removed > 0)
                    {
                        Diagnostics.Log($"Purged {purgeResult.Removed} folders from removed share roots.");
                        _settings = AppSettingsManager.Update(settings =>
                        {
                            settings.LastIndexEntryCount = purgeResult.TotalCount;
                            if (settings.LastIndexEntryCount == 0)
                                settings.LastIndexedAt = null;
                        });
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Log($"Failed to purge removed share roots: {ex.Message}");
                    System.Windows.MessageBox.Show(this,
                        $"Settings saved, but purging removed shares from the index failed:\n{ex.Message}",
                        Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            _onSaved();
            ReloadSettingsFromSource();
            LoadFromSettings();
            RefreshStatus();

            System.Windows.MessageBox.Show(this,
                "Settings saved.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            ReloadSettingsFromSource();
            LoadFromSettings();
            RefreshStatus();
        }

        protected override void OnClosed(EventArgs e)
        {
            _indexer.ProgressChanged -= OnIndexProgress;
            base.OnClosed(e);
        }

        private void ApplyWindowTheme()
        {
            bool dark = ThemeHelper.IsSystemDarkTheme();
            if (dark) return;

            ThemeHelper.SetFrozenBrush(Resources, "WindowBackgroundBrush", Color.FromRgb(0xF3, 0xF3, 0xF3));
            ThemeHelper.SetFrozenBrush(Resources, "WindowForegroundBrush", Color.FromRgb(0x1A, 0x1A, 0x1A));
            ThemeHelper.SetFrozenBrush(Resources, "SubtleForegroundBrush", Color.FromRgb(0x6B, 0x6B, 0x6B));
            ThemeHelper.SetFrozenBrush(Resources, "CardBackgroundBrush", Colors.White);
            ThemeHelper.SetFrozenBrush(Resources, "ButtonBackgroundBrush", Colors.White);
            ThemeHelper.SetFrozenBrush(Resources, "ButtonHoverBrush", Color.FromRgb(0xED, 0xED, 0xED));
            ThemeHelper.SetFrozenBrush(Resources, "ButtonPressedBrush", Color.FromRgb(0xDC, 0xDC, 0xDC));
            ThemeHelper.SetFrozenBrush(Resources, "ButtonBorderBrush", Color.FromRgb(0xD0, 0xD0, 0xD0));
            ThemeHelper.SetFrozenBrush(Resources, "ControlBackgroundBrush", Colors.White);
            ThemeHelper.SetFrozenBrush(Resources, "ControlBorderBrush", Color.FromRgb(0xD0, 0xD0, 0xD0));
            ThemeHelper.SetFrozenBrush(Resources, "ControlHoverBorderBrush", Color.FromRgb(0xB0, 0xB0, 0xB0));
            ThemeHelper.SetFrozenBrush(Resources, "TrackBackgroundBrush", Color.FromRgb(0xD8, 0xD8, 0xD8));
            ThemeHelper.SetFrozenBrush(Resources, "PopupBackgroundBrush", Colors.White);
            ThemeHelper.SetFrozenBrush(Resources, "ItemHoverBrush", Color.FromRgb(0xE8, 0xE8, 0xE8));
            ThemeHelper.SetFrozenBrush(Resources, "SeparatorBrush", Color.FromRgb(0xDE, 0xDE, 0xDE));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            DarkTitleBar.Apply(hwnd, ThemeHelper.IsSystemDarkTheme());
        }
    }
}
