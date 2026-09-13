using System.Windows.Forms;

namespace Nsqs.Tests;

public class HotkeyParserTests
{
    [Fact]
    public void TryParse_AcceptsCtrlShiftSpace()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Shift+Space", out var modifiers, out var key, out var error));
        Assert.Equal(Keys.Space, key);
        Assert.Null(error);
        Assert.NotEqual(0u, modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_RejectsEmptyHotkey(string input)
    {
        Assert.False(HotkeyParser.TryParse(input, out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_RejectsUnknownModifier()
    {
        Assert.False(HotkeyParser.TryParse("Meta+Space", out _, out _, out var error));
        Assert.Contains("Unknown modifier", error);
    }
}
