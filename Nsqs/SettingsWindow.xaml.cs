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
        private readonly AppSettings _settings;
        private readonly ShareIndexer _indexer;
        private readonly IndexScheduler _scheduler;
        private readonly Action _onSaved;
        private readonly Action _requestRebuild;

        public SettingsWindow(
            AppSettings settings,
            ShareIndexer indexer,
            IndexScheduler scheduler,
            Action onSaved,
            Action requestRebuild)
        {
            InitializeComponent();
            Icon = IconFactory.CreateWindowIconSource();
            ApplyWindowTheme();

            _settings = settings;
            _indexer = indexer;
            _scheduler = scheduler;
            _onSaved = onSaved;
            _requestRebuild = requestRebuild;

            _indexer.ProgressChanged += OnIndexProgress;

            LoadFromSettings();
            RefreshStatus();
        }

        private void LoadFromSettings()
        {
            ShareRootsList.ItemsSource = _settings.ShareRoots.ToList();
            HotkeyText.Text = _settings.Hotkey;
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
                if (progress.IsComplete)
                {
                    HideIndexProgress();
                    RefreshStatus();
                    return;
                }

                ShowIndexProgress(progress);
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

            _settings.ShareRoots = roots;
            _settings.Hotkey = HotkeyText.Text.Trim();
            _settings.IndexSchedule.Enabled = ScheduleEnabledCheck.IsChecked == true;
            _settings.IndexSchedule.Kind = ScheduleKindCombo.SelectedIndex == 1
                ? IndexScheduleKind.Weekly
                : IndexScheduleKind.Daily;
            _settings.IndexSchedule.DayOfWeek = (DayOfWeek)DayOfWeekCombo.SelectedIndex;
            _settings.IndexSchedule.TimeOfDay = AppSettings.FormatScheduleTimeWithSeconds(scheduleTime);
            _settings.RunMissedIndexOnStartup = RunMissedCheck.IsChecked == true;
            _settings.LaunchToTray = LaunchToTrayCheck.IsChecked == true;
            _settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
            return true;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ApplyToSettings())
                return;

            _settings.Save();
            StartupHelper.SetEnabled(_settings.StartWithWindows);
            _onSaved();
            RefreshStatus();

            System.Windows.MessageBox.Show(this,
                "Settings saved.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
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

            Resources["WindowBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            Resources["WindowForegroundBrush"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            Resources["SubtleForegroundBrush"] = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x6B));
            Resources["CardBackgroundBrush"] = new SolidColorBrush(Colors.White);
            Resources["ButtonBackgroundBrush"] = new SolidColorBrush(Colors.White);
            Resources["ButtonHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
            Resources["ButtonPressedBrush"] = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));
            Resources["ButtonBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            Resources["ControlBackgroundBrush"] = new SolidColorBrush(Colors.White);
            Resources["ControlBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            Resources["ControlHoverBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
            Resources["TrackBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8));
            Resources["PopupBackgroundBrush"] = new SolidColorBrush(Colors.White);
            Resources["ItemHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            Resources["SeparatorBrush"] = new SolidColorBrush(Color.FromRgb(0xDE, 0xDE, 0xDE));
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            DarkTitleBar.Apply(hwnd, ThemeHelper.IsSystemDarkTheme());
        }
    }
}
