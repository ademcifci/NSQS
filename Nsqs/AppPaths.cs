using System;
using System.IO;

namespace Nsqs
{
    internal static class AppPaths
    {
        public const string FolderName = "NSQS";

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

        public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
        public static string IndexFile => Path.Combine(DataDirectory, "index.db");
        public static string IndexBuildingFile => Path.Combine(DataDirectory, "index.building.db");
        public static string LogFile => Path.Combine(DataDirectory, "debug.log");
    }
}
