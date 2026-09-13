using System;
using System.Windows.Forms;

namespace Nsqs
{
    public sealed class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly AppSettings _settings;
        private readonly ToolStripMenuItem _startWithWindowsItem;
        private readonly ToolStripMenuItem _rebuildItem;

        public event Action? LauncherRequested;
        public event Action? SettingsRequested;
        public event Action? RebuildIndexRequested;
        public event Action? ExitRequested;

        public TrayIconManager(AppSettings settings)
        {
            _settings = settings;

            _startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
            {
                CheckOnClick = true,
                Checked = _settings.StartWithWindows
            };
            _startWithWindowsItem.Click += (_, _) =>
            {
                _settings.StartWithWindows = _startWithWindowsItem.Checked;
                _settings.Save();
                StartupHelper.SetEnabled(_settings.StartWithWindows);
            };

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
            menu.Opening += (_, _) => _startWithWindowsItem.Checked = _settings.StartWithWindows;

            _notifyIcon = new NotifyIcon
            {
                Icon = IconFactory.CreateTrayIcon(),
                Text = App.ProductName,
                Visible = true,
                ContextMenuStrip = menu
            };

            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    LauncherRequested?.Invoke();
            };

            _notifyIcon.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    LauncherRequested?.Invoke();
            };
        }

        public void SetStatus(string text)
        {
            _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
        }

        public void SetRebuildEnabled(bool enabled)
        {
            _rebuildItem.Enabled = enabled;
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
    }
}
