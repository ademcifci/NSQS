using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Nsqs
{
    public sealed class ShareFilterItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public required string RootPath { get; init; }
        public required string DisplayName { get; init; }
        public required string PillLabel { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class LauncherWindow : Window
    {
        private readonly Func<AppSettings> _getSettings;
        private readonly Func<bool>? _isIndexerRunning;
        private AppSettings _settings;
        private readonly ObservableCollection<FolderEntry> _results = new();
        private readonly ObservableCollection<ShareFilterItem> _shareFilters = new();
        private readonly DispatcherTimer _searchTimer = new();
        private readonly DispatcherTimer _filterSaveTimer = new();
        private int _selectedIndex;
        private int _searchGeneration;
        private bool _suppressDeactivateHide;
        private bool? _appliedDarkTheme;
        private ShareFilterItem? _focusedFilterItem;

        public LauncherWindow(Func<AppSettings> getSettings, Func<bool>? isIndexerRunning = null)
        {
            InitializeComponent();
            _getSettings = getSettings;
            _isIndexerRunning = isIndexerRunning;
            _settings = getSettings();

            Icon = IconFactory.CreateWindowIconSource();
            AppIconImage.Source = Icon;

            ResultsList.ItemsSource = _results;
            ShareFilterList.ItemsSource = _shareFilters;
            _searchTimer.Interval = TimeSpan.FromMilliseconds(120);
            _searchTimer.Tick += (_, _) =>
            {
                _searchTimer.Stop();
                _ = RunSearchAsync();
            };

            _filterSaveTimer.Interval = TimeSpan.FromMilliseconds(300);
            _filterSaveTimer.Tick += (_, _) =>
            {
                _filterSaveTimer.Stop();
                SaveSelectedShareFilters();
            };

            Closed += (_, _) =>
            {
                _searchTimer.Stop();
                _filterSaveTimer.Stop();
            };
        }

        public void RefreshSettings(AppSettings settings)
        {
            _settings = settings;
        }

        public void ShowLauncher()
        {
            _settings = _getSettings();
            ApplyTheme();
            LoadShareFilters();
            SearchBox.Text = string.Empty;
            _results.Clear();
            _selectedIndex = -1;
            UpdateResultState(hasQuery: false, resultCount: 0);

            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Top + workArea.Height * 0.26;

            Opacity = 0;
            Show();
            Activate();
            SearchBox.Focus();
            FadeTo(1);
        }

        public void HideLauncher()
        {
            CloseShareFilterPopup();
            SaveSelectedShareFilters();
            FadeTo(0, () =>
            {
                Hide();
                SearchBox.Text = string.Empty;
                _results.Clear();
            });
        }

        private void LoadShareFilters()
        {
            _shareFilters.Clear();

            var savedRoots = _settings.LastSearchShareRoots
                .Select(r => ShareIndexer.NormalizeUncRoot(r))
                .Where(r => r != null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var root in _settings.ShareRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalized = ShareIndexer.NormalizeUncRoot(root);
                if (normalized == null)
                    continue;

                _shareFilters.Add(new ShareFilterItem
                {
                    RootPath = normalized,
                    DisplayName = normalized,
                    PillLabel = GetSharePillLabel(normalized),
                    IsSelected = savedRoots.Contains(normalized)
                });
            }

            UpdateFilterDisplay();
        }

        private static string GetSharePillLabel(string uncPath)
        {
            var normalized = uncPath.Trim().TrimEnd('\\');
            if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
                return normalized;

            var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2)
                return $@"\{segments[1]}\";

            return normalized;
        }

        private IReadOnlyList<string>? GetSelectedShareRoots()
        {
            var selected = _shareFilters.Where(f => f.IsSelected).Select(f => f.RootPath).ToList();
            return selected.Count == 0 ? null : selected;
        }

        private void ShareFilter_Changed(object sender, RoutedEventArgs e)
        {
            ScheduleFilterSave();
            UpdateFilterDisplay();

            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                _ = RunSearchAsync();
        }

        private void FilterDropdown_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && IsWithinShareFilterPill(source))
                return;

            ToggleShareFilterPopup();
            e.Handled = true;
        }

        private void ToggleShareFilterPopup()
        {
            if (_shareFilters.Count == 0)
                return;

            if (ShareFilterPopup.IsOpen)
            {
                CloseShareFilterPopup();
                return;
            }

            OpenShareFilterPopup();
        }

        private void OpenShareFilterPopup()
        {
            ShareFilterPopup.IsOpen = true;
            if (_shareFilters.Count > 0)
                MoveShareFilterListSelectionTo(0);
        }

        private void CloseShareFilterPopup()
        {
            ShareFilterPopup.IsOpen = false;
            FilterDropdown.Focus();
        }

        private void FilterDropdown_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ShareFilterPopup.IsOpen)
            {
                switch (e.Key)
                {
                    case Key.Down:
                        ShareFilterList.Focus();
                        MoveShareFilterListSelection(1);
                        e.Handled = true;
                        return;
                    case Key.Up:
                        ShareFilterList.Focus();
                        MoveShareFilterListSelection(-1);
                        e.Handled = true;
                        return;
                    case Key.Escape:
                        CloseShareFilterPopup();
                        e.Handled = true;
                        return;
                    case Key.Tab:
                        CloseShareFilterPopup();
                        MoveLauncherTabFocus(fromFilterDropdown: true, reverse: Keyboard.Modifiers == ModifierKeys.Shift);
                        e.Handled = true;
                        return;
                }
            }

            switch (e.Key)
            {
                case Key.Enter:
                case Key.Space:
                case Key.Down:
                case Key.F4:
                    OpenShareFilterPopup();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    CloseShareFilterPopup();
                    e.Handled = true;
                    break;
                case Key.Tab:
                    CloseShareFilterPopup();
                    MoveLauncherTabFocus(fromFilterDropdown: true, reverse: Keyboard.Modifiers == ModifierKeys.Shift);
                    e.Handled = true;
                    break;
                case Key.Delete:
                case Key.Back:
                    if (_focusedFilterItem != null)
                    {
                        RemoveShareFilter(_focusedFilterItem);
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void ShareFilterList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                    MoveShareFilterListSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveShareFilterListSelection(1);
                    e.Handled = true;
                    break;
                case Key.Home:
                    MoveShareFilterListSelectionTo(0);
                    e.Handled = true;
                    break;
                case Key.End:
                    MoveShareFilterListSelectionTo(_shareFilters.Count - 1);
                    e.Handled = true;
                    break;
                case Key.Space:
                case Key.Enter:
                    ToggleShareFilterListItem(ShareFilterList.SelectedItem as ShareFilterItem);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    CloseShareFilterPopup();
                    e.Handled = true;
                    break;
                case Key.Tab:
                    CloseShareFilterPopup();
                    if (Keyboard.Modifiers == ModifierKeys.Shift)
                        FilterDropdown.Focus();
                    else
                        MoveLauncherTabFocus(fromFilterDropdown: true, reverse: false);
                    e.Handled = true;
                    break;
            }
        }

        private void MoveLauncherTabFocus(bool fromFilterDropdown, bool reverse)
        {
            if (fromFilterDropdown)
            {
                if (reverse)
                {
                    SearchBox.Focus();
                    return;
                }

                if (IsExportButtonVisible)
                    ExportResultsButton.Focus();
                else
                    SearchBox.Focus();
                return;
            }

            if (reverse)
            {
                if (IsExportButtonVisible)
                    ExportResultsButton.Focus();
                else
                    FilterDropdown.Focus();
                return;
            }

            FilterDropdown.Focus();
        }

        private void MoveShareFilterListSelection(int delta)
        {
            if (_shareFilters.Count == 0)
                return;

            var current = ShareFilterList.SelectedIndex;
            if (current < 0)
                current = 0;

            MoveShareFilterListSelectionTo(Math.Clamp(current + delta, 0, _shareFilters.Count - 1));
        }

        private void MoveShareFilterListSelectionTo(int index)
        {
            if (_shareFilters.Count == 0)
                return;

            index = Math.Clamp(index, 0, _shareFilters.Count - 1);
            ShareFilterList.SelectedIndex = index;
            ShareFilterList.ScrollIntoView(ShareFilterList.SelectedItem);

            if (ShareFilterList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem listItem)
                listItem.Focus();
            else
                ShareFilterList.Focus();
        }

        private void ToggleShareFilterListItem(ShareFilterItem? item)
        {
            if (item == null)
                return;

            item.IsSelected = !item.IsSelected;
            ScheduleFilterSave();
            UpdateFilterDisplay();

            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                _ = RunSearchAsync();
        }

        private void ShareFilterPill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ShareFilterItem item)
            {
                _focusedFilterItem = item;
                element.Focus();
                e.Handled = true;
            }
        }

        private void ShareFilterPill_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Delete or Key.Back && sender is FrameworkElement element && element.DataContext is ShareFilterItem item)
            {
                RemoveShareFilter(item);
                e.Handled = true;
            }
        }

        private void ShareFilterPillRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is ShareFilterItem item)
                RemoveShareFilter(item);

            e.Handled = true;
        }

        private void RemoveShareFilter(ShareFilterItem item)
        {
            item.IsSelected = false;
            if (_focusedFilterItem == item)
                _focusedFilterItem = null;

            SaveSelectedShareFilters();
            UpdateFilterDisplay();

            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                _ = RunSearchAsync();

            FilterDropdown.Focus();
        }

        private static bool IsWithinShareFilterPill(DependencyObject source)
        {
            while (source != null)
            {
                if (source is FrameworkElement { Tag: string tag } && tag == "ShareFilterPill")
                    return true;

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private void SaveSelectedShareFilters()
        {
            var selected = _shareFilters
                .Where(f => f.IsSelected)
                .Select(f => f.RootPath)
                .ToList();

            AppSettingsManager.Update(settings =>
            {
                settings.LastSearchShareRoots = selected;
#pragma warning disable CS0618
                settings.LastSearchShareRoot = string.Empty;
#pragma warning restore CS0618
            });

            _settings = _getSettings();
        }

        private void ScheduleFilterSave()
        {
            _filterSaveTimer.Stop();
            _filterSaveTimer.Start();
        }

        private void UpdateFilterDisplay()
        {
            if (_shareFilters.Count == 0)
            {
                ShareFilterPills.ItemsSource = null;
                ShareFilterPillsHost.Visibility = Visibility.Collapsed;
                FilterPlaceholder.Text = "Add network shares in Settings to filter";
                FilterPlaceholder.Visibility = Visibility.Visible;
                return;
            }

            var selected = _shareFilters.Where(f => f.IsSelected).ToList();
            ShareFilterPills.ItemsSource = selected;
            ShareFilterPillsHost.Visibility = selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FilterPlaceholder.Text = "All network shares";
            FilterPlaceholder.Visibility = selected.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyTheme()
        {
            bool dark = ThemeHelper.IsSystemDarkTheme();
            if (_appliedDarkTheme == dark)
                return;

            _appliedDarkTheme = dark;
            var bg = dark ? Color.FromArgb(245, 0x20, 0x20, 0x20) : Color.FromArgb(250, 0xF3, 0xF3, 0xF3);
            var fg = dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
            var subtle = dark ? Color.FromRgb(0xBD, 0xBD, 0xBD) : Color.FromRgb(0x52, 0x52, 0x52);
            var hover = dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);
            var controlBg = dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(80, 0, 0, 0);
            var controlBorder = dark ? Color.FromRgb(0x8A, 0x8A, 0x8A) : Color.FromRgb(0x76, 0x76, 0x76);
            var pillBg = dark ? Color.FromRgb(0x48, 0x48, 0x48) : Color.FromRgb(0xE8, 0xE8, 0xEE);
            var pillBorder = dark ? Color.FromRgb(0x9E, 0x9E, 0x9E) : Color.FromRgb(0x76, 0x76, 0x76);
            var pillFg = dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);
            var pillRemove = dark ? Color.FromRgb(0xD6, 0xD6, 0xD6) : Color.FromRgb(0x52, 0x52, 0x52);
            var checkboxBorder = dark ? Color.FromRgb(0xAD, 0xAD, 0xAD) : Color.FromRgb(0x76, 0x76, 0x76);
            var listFocus = dark ? Color.FromArgb(68, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);

            RootBorder.Background = ThemeHelper.CreateFrozenBrush(bg);
            RootBorder.BorderBrush = ThemeHelper.CreateFrozenBrush(controlBorder);
            SearchBox.Foreground = ThemeHelper.CreateFrozenBrush(fg);
            SearchBox.CaretBrush = ThemeHelper.CreateFrozenBrush(fg);
            HintText.Foreground = ThemeHelper.CreateFrozenBrush(subtle);
            Resources["ResultHoverBrush"] = ThemeHelper.CreateFrozenBrush(hover);
            Resources["ResultForegroundBrush"] = ThemeHelper.CreateFrozenBrush(fg);
            Resources["ResultSubtleBrush"] = ThemeHelper.CreateFrozenBrush(subtle);
            Resources["LauncherControlBackgroundBrush"] = ThemeHelper.CreateFrozenBrush(controlBg);
            Resources["LauncherControlBorderBrush"] = ThemeHelper.CreateFrozenBrush(controlBorder);
            Resources["FilterPillBackgroundBrush"] = ThemeHelper.CreateFrozenBrush(pillBg);
            Resources["FilterPillBorderBrush"] = ThemeHelper.CreateFrozenBrush(pillBorder);
            Resources["FilterPillForegroundBrush"] = ThemeHelper.CreateFrozenBrush(pillFg);
            Resources["FilterPillRemoveBrush"] = ThemeHelper.CreateFrozenBrush(pillRemove);
            Resources["FilterCheckboxBorderBrush"] = ThemeHelper.CreateFrozenBrush(checkboxBorder);
            Resources["FilterListFocusBrush"] = ThemeHelper.CreateFrozenBrush(listFocus);

            try
            {
                Resources["AccentBrush"] = Application.Current.Resources["AccentBrush"];
            }
            catch
            {
                Resources["AccentBrush"] = ThemeHelper.CreateFrozenBrush(ThemeHelper.GetSystemAccentColor());
            }

            var accentColor = ((SolidColorBrush)Resources["AccentBrush"]).Color;
            var dropdownFocus = Color.FromArgb(dark ? (byte)72 : (byte)48, accentColor.R, accentColor.G, accentColor.B);
            Resources["FilterDropdownFocusBrush"] = ThemeHelper.CreateFrozenBrush(dropdownFocus);
        }

        private void FadeTo(double target, Action? onComplete = null)
        {
            BeginAnimation(OpacityProperty, null);

            var animation = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
            };
            if (onComplete != null)
                animation.Completed += (_, _) => onComplete();

            BeginAnimation(OpacityProperty, animation);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private async Task RunSearchAsync()
        {
            var query = SearchBox.Text.Trim();
            var generation = ++_searchGeneration;

            _results.Clear();
            _selectedIndex = -1;

            if (query.Length == 0 || !File.Exists(AppPaths.IndexFile))
            {
                UpdateResultState(hasQuery: false, resultCount: 0);
                return;
            }

            if (_isIndexerRunning?.Invoke() == true)
            {
                _results.Clear();
                _selectedIndex = -1;
                EmptyStateText.Text = "Index is rebuilding…";
                EmptyStateText.Visibility = Visibility.Visible;
                ResultCountText.Text = string.Empty;
                ExportResultsButton.Visibility = Visibility.Collapsed;
                return;
            }

            UpdateResultState(hasQuery: true, resultCount: 0);
            EmptyStateText.Text = "Searching…";
            EmptyStateText.Visibility = Visibility.Visible;

            try
            {
                var maxResults = _settings.MaxResults;
                var roots = GetSelectedShareRoots()?.ToList();
                var results = await Task.Run(() =>
                    IndexStore.SearchSnapshot(AppPaths.IndexFile, query, maxResults, roots));

                if (generation != _searchGeneration)
                    return;

                foreach (var entry in results)
                    _results.Add(entry);

                if (_results.Count > 0)
                {
                    _selectedIndex = 0;
                    ResultsList.SelectedIndex = 0;
                }

                UpdateResultState(hasQuery: true, resultCount: _results.Count);
            }
            catch (Exception ex)
            {
                if (generation != _searchGeneration)
                    return;

                Diagnostics.Log($"Search failed: {ex.Message}");
                EmptyStateText.Text = "Search failed";
                EmptyStateText.Visibility = Visibility.Visible;
                ResultCountText.Text = string.Empty;
                ExportResultsButton.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateResultState(bool hasQuery, int resultCount)
        {
            if (!hasQuery)
            {
                EmptyStateText.Text = "Type to search indexed folders";
                EmptyStateText.Visibility = Visibility.Visible;
                ResultCountText.Text = string.Empty;
                ExportResultsButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (resultCount == 0)
            {
                EmptyStateText.Text = "No folders found";
                EmptyStateText.Visibility = Visibility.Visible;
                ResultCountText.Text = "0 results";
                ExportResultsButton.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyStateText.Visibility = Visibility.Collapsed;
            ResultCountText.Text = resultCount == 1 ? "1 result" : $"{resultCount} results";
            ExportResultsButton.Visibility = Visibility.Visible;
        }

        private void ExportResultsButton_Click(object sender, RoutedEventArgs e) => ExportCurrentResults();

        private bool IsExportButtonVisible => ExportResultsButton.Visibility == Visibility.Visible;

        private void ExportResultsButton_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Tab)
                return;

            if (Keyboard.Modifiers == ModifierKeys.Shift)
                FilterDropdown.Focus();
            else
                SearchBox.Focus();

            e.Handled = true;
        }

        private void ExportCurrentResults()
        {
            if (_results.Count == 0)
                return;

            var entries = _results.ToList();

            var query = SearchBox.Text.Trim();
            var safeQuery = string.Join('-', query.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
                .Trim('-');
            if (safeQuery.Length > 40)
                safeQuery = safeQuery[..40];

            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv",
                DefaultExt = "csv",
                FileName = string.IsNullOrEmpty(safeQuery)
                    ? $"NSQS-results-{DateTime.Now:yyyy-MM-dd}.csv"
                    : $"NSQS-results-{safeQuery}-{DateTime.Now:yyyy-MM-dd}.csv"
            };

            _suppressDeactivateHide = true;
            try
            {
                if (dialog.ShowDialog(this) != true)
                    return;

                try
                {
                    var count = IndexStore.ExportEntriesToCsv(entries, dialog.FileName);
                    System.Windows.MessageBox.Show(this,
                        $"Exported {count:N0} result{(count == 1 ? "" : "s")} to:\n{dialog.FileName}",
                        Title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log($"Results export failed: {ex.Message}");
                    System.Windows.MessageBox.Show(this,
                        $"Export failed:\n{ex.Message}",
                        Title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                _suppressDeactivateHide = false;
            }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    OpenSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    if (ShareFilterPopup.IsOpen)
                    {
                        CloseShareFilterPopup();
                        e.Handled = true;
                    }
                    else
                    {
                        HideLauncher();
                        e.Handled = true;
                    }
                    break;
                case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                    MoveLauncherTabFocus(fromFilterDropdown: false, reverse: false);
                    e.Handled = true;
                    break;
                case Key.Tab when Keyboard.Modifiers == ModifierKeys.Shift:
                    MoveLauncherTabFocus(fromFilterDropdown: false, reverse: true);
                    e.Handled = true;
                    break;
                case Key.E when Keyboard.Modifiers == ModifierKeys.Control:
                    ExportCurrentResults();
                    e.Handled = true;
                    break;
            }
        }

        private void MoveSelection(int delta)
        {
            if (_results.Count == 0) return;

            _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _results.Count - 1);
            ResultsList.SelectedIndex = _selectedIndex;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelected();
        }

        private void ResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OpenSelected();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ExportCurrentResults();
                e.Handled = true;
            }
        }

        private void OpenSelected()
        {
            if (ResultsList.SelectedItem is not FolderEntry entry)
                return;

            if (!SharePathHelper.IsSafeIndexedPath(entry, _settings.ShareRoots))
            {
                Diagnostics.Log($"Blocked opening unsafe path: {entry.Path}");
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = entry.Path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Failed to open {entry.Path}: {ex.Message}");
            }

            HideLauncher();
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);

            if (ShareFilterPopup.IsOpen)
            {
                CloseShareFilterPopup();
                return;
            }

            if (_suppressDeactivateHide)
                return;

            if (IsVisible)
                HideLauncher();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            DarkTitleBar.Apply(hwnd, ThemeHelper.IsSystemDarkTheme());
        }
    }
}
