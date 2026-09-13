using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace Nsqs
{
    public static class ThemeHelper
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetColorizationColor(out uint colorizationColor, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);

        public static bool IsSystemDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int i)
                    return i == 0;
            }
            catch
            {
                // Fall through to default.
            }

            return true;
        }

        public static Color GetSystemAccentColor()
        {
            try
            {
                if (DwmGetColorizationColor(out uint argb, out _) == 0)
                {
                    byte r = (byte)(argb >> 16);
                    byte g = (byte)(argb >> 8);
                    byte b = (byte)argb;
                    return Color.FromRgb(r, g, b);
                }
            }
            catch
            {
                // Fall through to default.
            }

            return Color.FromRgb(0x00, 0x78, 0xD4);
        }

        public static Color Lighten(Color c, double amount)
        {
            byte L(byte channel) => (byte)(channel + (255 - channel) * amount);
            return Color.FromRgb(L(c.R), L(c.G), L(c.B));
        }

        public static Color Darken(Color c, double amount)
        {
            byte D(byte channel) => (byte)(channel * (1 - amount));
            return Color.FromRgb(D(c.R), D(c.G), D(c.B));
        }
    }
}
