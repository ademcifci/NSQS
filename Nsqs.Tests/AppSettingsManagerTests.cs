namespace Nsqs.Tests;

public class AppSettingsManagerTests
{
    [Fact]
    public void ApplyIndexMetadata_MergesWithoutOverwritingOtherSettings()
    {
        var settings = new AppSettings
        {
            ShareRoots = ["\\\\server\\share\\"],
            Hotkey = "Ctrl+Shift+Q",
            MaxResults = 25,
            LastIndexError = "previous warning"
        };

        AppSettingsManager.ApplyIndexMetadata(settings, new IndexMetadata
        {
            LastIndexedAt = new DateTime(2026, 3, 13, 19, 0, 0),
            LastIndexEntryCount = 42,
            LastIndexDurationSeconds = 12.5,
            ClearLastIndexError = true
        });

        Assert.Equal("Ctrl+Shift+Q", settings.Hotkey);
        Assert.Equal(25, settings.MaxResults);
        Assert.Equal(42, settings.LastIndexEntryCount);
        Assert.Null(settings.LastIndexError);
    }

    [Fact]
    public void ApplyIndexMetadata_PreservesWarningWhenSkippedSharesReported()
    {
        var settings = new AppSettings { Hotkey = "Ctrl+Shift+Space" };

        AppSettingsManager.ApplyIndexMetadata(settings, new IndexMetadata
        {
            LastIndexEntryCount = 10,
            LastIndexError = "Skipped unreachable shares: \\\\server\\offline\\"
        });

        Assert.Contains("offline", settings.LastIndexError);
        Assert.Equal("Ctrl+Shift+Space", settings.Hotkey);
    }
}
