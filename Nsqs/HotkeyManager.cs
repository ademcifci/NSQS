using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Interop;

namespace Nsqs
{
    public sealed class HotkeyManager : IDisposable
    {
        private const int HotkeyId = 0x4E53; // "NS"
        private const int WmHotkey = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly HwndSource _source;
        private bool _registered;

        public event Action? HotkeyPressed;

        public HotkeyManager()
        {
            var parameters = new HwndSourceParameters("NsqsHotkeyHost")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = new IntPtr(-3) // HWND_MESSAGE
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        public bool TryRegister(string hotkeyText, out string? error)
        {
            Unregister();

            if (!HotkeyParser.TryParse(hotkeyText, out var modifiers, out var key, out error))
                return false;

            _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers, (uint)key);
            if (_registered)
            {
                error = null;
                return true;
            }

            error = $"RegisterHotKey failed (error {Marshal.GetLastWin32Error()}). The shortcut may already be in use.";
            return false;
        }

        public void Unregister()
        {
            if (!_registered)
                return;

            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                HotkeyPressed?.Invoke();
                handled = true;
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
    }

    internal static class HotkeyParser
    {
        public static bool TryParse(string text, out uint modifiers, out Keys key, out string? error)
        {
            modifiers = 0;
            key = Keys.None;
            error = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Hotkey cannot be empty.";
                return false;
            }

            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                error = "Hotkey cannot be empty.";
                return false;
            }

            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!TryParseModifier(parts[i], out var mod))
                {
                    error = $"Unknown modifier: {parts[i]}";
                    return false;
                }

                modifiers |= mod;
            }

            var keyText = parts[^1];
            if (!Enum.TryParse(keyText, ignoreCase: true, out key) || key == Keys.None)
            {
                error = $"Unknown key: {keyText}";
                return false;
            }

            return true;
        }

        private static bool TryParseModifier(string part, out uint modifier)
        {
            modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => MOD_CONTROL,
                "shift" => MOD_SHIFT,
                "alt" => MOD_ALT,
                "win" or "windows" => MOD_WIN,
                _ => 0
            };
            return modifier != 0;
        }

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
    }
}
