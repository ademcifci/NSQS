using System;

namespace Nsqs
{
    public sealed class IndexMetadata
    {
        public DateTime? LastIndexedAt { get; init; }
        public int? LastIndexEntryCount { get; init; }
        public double? LastIndexDurationSeconds { get; init; }
        public string? LastIndexError { get; init; }
        public bool ClearLastIndexError { get; init; }
    }

    public static class AppSettingsManager
    {
        private static readonly object Lock = new();

        public static AppSettings Load()
        {
            lock (Lock)
                return AppSettings.LoadCore();
        }

        public static void Save(AppSettings settings)
        {
            lock (Lock)
                AppSettings.SaveCore(settings);
        }

        public static AppSettings Update(Action<AppSettings> mutate)
        {
            lock (Lock)
            {
                var settings = AppSettings.LoadCore();
                mutate(settings);
                AppSettings.SaveCore(settings);
                return settings;
            }
        }

        public static AppSettings SaveIndexMetadata(IndexMetadata metadata)
        {
            lock (Lock)
            {
                var settings = AppSettings.LoadCore();
                ApplyIndexMetadata(settings, metadata);
                AppSettings.SaveCore(settings);
                return settings;
            }
        }

        internal static void ApplyIndexMetadata(AppSettings settings, IndexMetadata metadata)
        {
            if (metadata.LastIndexedAt.HasValue)
                settings.LastIndexedAt = metadata.LastIndexedAt;

            if (metadata.LastIndexEntryCount.HasValue)
                settings.LastIndexEntryCount = metadata.LastIndexEntryCount.Value;

            if (metadata.LastIndexDurationSeconds.HasValue)
                settings.LastIndexDurationSeconds = metadata.LastIndexDurationSeconds.Value;

            if (metadata.ClearLastIndexError)
                settings.LastIndexError = null;
            else if (metadata.LastIndexError != null)
                settings.LastIndexError = metadata.LastIndexError;
        }
    }
}
