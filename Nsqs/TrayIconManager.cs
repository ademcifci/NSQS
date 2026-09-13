using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace Nsqs
{
    public sealed class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Func<AppSettings> _getSettings;
        private readonly Action<bool> _onStartWithWindowsChanged;
        private readonly ToolStripMenuItem _startWithWindowsItem;
        private readonly ToolStripMenuItem _rebuildItem;
        private readonly CancelEventHandler _menuOpeningHandler;

        public event Action? LauncherRequested;
        public event Action? SettingsRequested;
        public event Action? RebuildIndexRequested;
        public event Action? ExitRequested;

        public TrayIconManager(Func<AppSettings> getSettings, Action<bool> onStartWithWindowsChanged)
        {
            _getSettings = getSettings;
            _onStartWithWindowsChanged = onStartWithWindowsChanged;

            _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
            {
                CheckOnClick = true,
                Checked = _getSettings().StartWithWindows
            };
            _startWithWindowsItem.Click += OnStartWithWindowsClick;

            _rebuildItem = new ToolStripMenuItem("Rebuild index now", null, (_, _) => RebuildIndexRequested?.Invoke());

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open search", null, (_, _) => LauncherRequested?.Invoke());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings...", null, (_, _) => SettingsRequested?.Invoke());
            menu.Items.Add(_rebuildItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_startWithWindowsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add($"Exit {App.ShortName}", null, (_, _) => ExitRequested?.Invoke());
            _menuOpeningHandler = (_, _) => _startWithWindowsItem.Checked = _getSettings().StartWithWindows;
            menu.Opening += _menuOpeningHandler;

            _notifyIcon = new NotifyIcon
            {
                Icon = IconFactory.CreateTrayIcon(),
                Text = App.ProductName,
                Visible = true,
                ContextMenuStrip = menu
            };

            _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        }

        private void OnStartWithWindowsClick(object? sender, EventArgs e)
        {
            _onStartWithWindowsChanged(_startWithWindowsItem.Checked);
        }

        private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Clicks != 1)
                return;

            LauncherRequested?.Invoke();
        }

        public void SetStatus(string text, string? prioritySuffix = null)
        {
            if (!string.IsNullOrEmpty(prioritySuffix))
                text = FormatTrayText(text, prioritySuffix);

            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        }

        public void ShowWarning(string title, string message, int timeoutMs = 5000)
        {
            _notifyIcon.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Warning);
        }

        private static string FormatTrayText(string text, string prioritySuffix)
        {
            const int maxLength = 63;
            var suffix = $" — {prioritySuffix}";
            if (text.Length + suffix.Length <= maxLength)
                return text + suffix;

            var available = maxLength - suffix.Length;
            if (available < 8)
                return prioritySuffix.Length <= maxLength ? prioritySuffix : prioritySuffix[..maxLength];

            return text[..available] + suffix;
        }

        public void SetRebuildEnabled(bool enabled)
        {
            _rebuildItem.Enabled = enabled;
        }

        public void RefreshSettings()
        {
            _startWithWindowsItem.Checked = _getSettings().StartWithWindows;
        }

        public void Dispose()
        {
            _notifyIcon.MouseClick -= OnNotifyIconMouseClick;
            _startWithWindowsItem.Click -= OnStartWithWindowsClick;
            _notifyIcon.ContextMenuStrip!.Opening -= _menuOpeningHandler;

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
