using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nsqs
{
    public enum IndexScheduleKind
    {
        Daily,
        Weekly
    }

    public class IndexScheduleSettings
    {
        public bool Enabled { get; set; } = true;
        public IndexScheduleKind Kind { get; set; } = IndexScheduleKind.Weekly;
        public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Tuesday;

        /// <summary>Local time of day, 24-hour HH:mm:ss.</summary>
        public string TimeOfDay { get; set; } = "19:00:00";
    }

    public class AppSettings
    {
        public List<string> ShareRoots { get; set; } = new();
        public string Hotkey { get; set; } = "Ctrl+Shift+Space";
        public bool StartWithWindows { get; set; }
        public bool LaunchToTray { get; set; } = true;
        public IndexScheduleSettings IndexSchedule { get; set; } = new();
        public bool RunMissedIndexOnStartup { get; set; } = true;
        public int MaxResults { get; set; } = 50;

        /// <summary>Empty = search all shares. Otherwise UNC roots to filter search.</summary>
        public List<string> LastSearchShareRoots { get; set; } = new();

        [Obsolete("Use LastSearchShareRoots.")]
        public string LastSearchShareRoot { get; set; } = "";

        public DateTime? LastIndexedAt { get; set; }
        public int LastIndexEntryCount { get; set; }
        public double LastIndexDurationSeconds { get; set; }
        public string? LastIndexError { get; set; }

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static string FilePath => AppPaths.SettingsFile;

        public static AppSettings Load()
        {
            return AppSettingsManager.Load();
        }

        public void Save()
        {
            AppSettingsManager.Save(this);
        }

        internal static AppSettings LoadCore()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        MigrateSearchShareRoots(settings);
                        ValidateShareRoots(settings);
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Settings.Load failed, using defaults: {ex.Message}");
            }

            return new AppSettings();
        }

        internal static void SaveCore(AppSettings settings)
        {
            SaveCoreToPath(settings, FilePath);
        }

        internal static void SaveCoreToPath(AppSettings settings, string filePath)
        {
            ValidateShareRoots(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);

            if (File.Exists(filePath))
                File.Replace(tempPath, filePath, destinationBackupFileName: filePath + ".bak", ignoreMetadataErrors: true);
            else
                File.Move(tempPath, filePath);
        }

        private static void ValidateShareRoots(AppSettings settings)
        {
            settings.ShareRoots.RemoveAll(root => ShareIndexer.NormalizeUncRoot(root) == null);
            settings.LastSearchShareRoots.RemoveAll(root => ShareIndexer.NormalizeUncRoot(root) == null);
            PruneSearchShareRoots(settings);
            ClampMaxResults(settings);
        }

        internal static void PruneSearchShareRoots(AppSettings settings)
        {
            if (settings.LastSearchShareRoots.Count == 0)
                return;

            settings.LastSearchShareRoots.RemoveAll(filter =>
            {
                var normalizedFilter = ShareIndexer.NormalizeUncRoot(filter);
                if (normalizedFilter == null)
                    return true;

                foreach (var root in settings.ShareRoots)
                {
                    var normalizedRoot = ShareIndexer.NormalizeUncRoot(root);
                    if (normalizedRoot != null &&
                        string.Equals(normalizedRoot, normalizedFilter, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return true;
            });
        }

        internal static void ClampMaxResults(AppSettings settings)
        {
            if (settings.MaxResults < 1)
                settings.MaxResults = 1;
            else if (settings.MaxResults > 500)
                settings.MaxResults = 500;
        }

        public TimeSpan GetScheduleTimeOfDay()
        {
            if (TimeSpan.TryParse(IndexSchedule.TimeOfDay, out var time))
                return time;

            return new TimeSpan(19, 0, 0);
        }

        /// <summary>Formats a time-of-day for display/editing. TimeSpan uses hh, not DateTime's HH.</summary>
        public static string FormatScheduleTime(TimeSpan time) => time.ToString(@"hh\:mm");

        public static string FormatScheduleTimeWithSeconds(TimeSpan time) => time.ToString(@"hh\:mm\:ss");

        private static void MigrateSearchShareRoots(AppSettings settings)
        {
            if (settings.LastSearchShareRoots.Count > 0)
                return;

#pragma warning disable CS0618
            if (string.IsNullOrWhiteSpace(settings.LastSearchShareRoot))
                return;

            settings.LastSearchShareRoots.Add(settings.LastSearchShareRoot);
            settings.LastSearchShareRoot = string.Empty;
#pragma warning restore CS0618
        }
    }
}
