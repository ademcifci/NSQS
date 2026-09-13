using System.Text.Json;

namespace Nsqs.Tests;

public class SettingsTests
{
    [Fact]
    public void RoundTrip_PreservesShareRootsAndSchedule()
    {
        var original = new AppSettings
        {
            ShareRoots = ["\\\\server\\movies", "\\\\server\\tv"],
            Hotkey = "Ctrl+Alt+F",
            StartWithWindows = true,
            LaunchToTray = false,
            MaxResults = 25,
            LastSearchShareRoots = ["\\\\server\\movies"],
            LastIndexedAt = new DateTime(2026, 3, 13, 19, 0, 0, DateTimeKind.Local),
            LastIndexEntryCount = 1234,
            IndexSchedule =
            {
                Enabled = true,
                Kind = IndexScheduleKind.Weekly,
                DayOfWeek = DayOfWeek.Tuesday,
                TimeOfDay = "19:30:00"
            }
        };

        var json = JsonSerializer.Serialize(original, AppSettings.JsonOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(original.ShareRoots, restored!.ShareRoots);
        Assert.Equal(original.Hotkey, restored.Hotkey);
        Assert.Equal(original.MaxResults, restored.MaxResults);
        Assert.Equal(original.IndexSchedule.Kind, restored.IndexSchedule.Kind);
        Assert.Equal(original.LastIndexEntryCount, restored.LastIndexEntryCount);
    }

    [Fact]
    public void SaveCore_WritesAtomically()
    {
        var tempDir = TestFileHelper.CreateTempDirectory();
        var settingsPath = Path.Combine(tempDir, "settings.json");

        try
        {
            var settings = new AppSettings { Hotkey = "Ctrl+Shift+A", ShareRoots = ["\\\\server\\share\\"] };
            AppSettings.SaveCoreToPath(settings, settingsPath);

            Assert.True(File.Exists(settingsPath));
            Assert.False(File.Exists(settingsPath + ".tmp"));

            var json = File.ReadAllText(settingsPath);
            var restored = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions);
            Assert.Equal("Ctrl+Shift+A", restored!.Hotkey);
        }
        finally
        {
            TestFileHelper.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void PruneSearchShareRoots_RemovesFiltersForRemovedShares()
    {
        var settings = new AppSettings
        {
            ShareRoots = ["\\\\server\\movies\\"],
            LastSearchShareRoots = ["\\\\server\\movies\\", "\\\\server\\tv\\"]
        };

        AppSettings.PruneSearchShareRoots(settings);

        Assert.Single(settings.LastSearchShareRoots);
        Assert.Equal("\\\\server\\movies\\", settings.LastSearchShareRoots[0]);
    }

    [Fact]
    public void ClampMaxResults_EnforcesBounds()
    {
        var settings = new AppSettings { MaxResults = 9999 };
        AppSettings.ClampMaxResults(settings);
        Assert.Equal(500, settings.MaxResults);

        settings.MaxResults = 0;
        AppSettings.ClampMaxResults(settings);
        Assert.Equal(1, settings.MaxResults);
    }
}
