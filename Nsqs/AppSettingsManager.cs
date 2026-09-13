using System;

namespace Nsqs
{
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
    }
}
