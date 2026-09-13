using System;
using System.IO;

namespace Nsqs
{
    public static class Diagnostics
    {
        public static readonly string LogPath = AppPaths.LogFile;

        private static readonly object Lock = new();
        private const long MaxBytes = 256 * 1024;
        private static bool _directoryReady;

        public static void Log(string message)
        {
            try
            {
                lock (Lock)
                {
                    if (!_directoryReady)
                    {
                        Directory.CreateDirectory(AppPaths.DataDirectory);
                        _directoryReady = true;
                    }

                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxBytes)
                        File.WriteAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  (earlier entries trimmed){Environment.NewLine}");

                    File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Diagnostics must never crash the app.
            }
        }
    }
}
