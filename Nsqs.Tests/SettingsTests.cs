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
}
