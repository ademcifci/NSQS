using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Nsqs
{
    public static class StartupHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "NSQS";

        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) != null;
        }

        public static void RepairPathIfEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null) return;

                if (key.GetValue(ValueName) is not string existing) return;

                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                var expected = $"\"{exePath}\"";
                if (string.Equals(existing, expected, StringComparison.OrdinalIgnoreCase)) return;

                key.SetValue(ValueName, expected);
                Diagnostics.Log($"Autostart path updated: {existing} -> {expected}");
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Autostart path repair failed: {ex.Message}");
            }
        }
    }
}
