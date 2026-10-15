using WinShot.Core.Hotkeys;
using Xunit;

namespace WinShot.Core.Tests;

public class HotkeyGestureTests
{
    [Fact]
    public void TryParse_CtrlShiftA()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+A", out var gesture));
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, gesture.Modifiers);
        Assert.Equal((uint)'A', gesture.VirtualKey);
    }

    [Theory]
    [InlineData("ctrl+shift+a", "Ctrl+Shift+A")]
    [InlineData("Win + Alt + F4", "Alt+Win+F4")]
    [InlineData("F11", "F11")]
    [InlineData("Ctrl+PrintScreen", "Ctrl+PrintScreen")]
    public void TryParse_NormalizesThroughToString(string input, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(input, out var gesture));
        Assert.Equal(expected, gesture.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]        // no key
    [InlineData("A")]                 // bare letter needs a modifier
    [InlineData("Ctrl+Foo")]          // unknown key
    [InlineData("Ctrl+A+B")]          // two keys
    public void TryParse_RejectsInvalid(string? input)
    {
        Assert.False(HotkeyGesture.TryParse(input, out _));
    }

    [Fact]
    public void TryParse_StandaloneFunctionKey_IsAllowed()
    {
        Assert.True(HotkeyGesture.TryParse("F6", out var gesture));
        Assert.Equal(HotkeyModifiers.None, gesture.Modifiers);
        Assert.Equal(0x75u, gesture.VirtualKey);
    }

    [Fact]
    public void ParseAndToString_RoundTripsForAllDefaults()
    {
        foreach (string text in new[] { "Ctrl+Shift+A", "Ctrl+Shift+F", "Ctrl+Shift+D" })
        {
            Assert.True(HotkeyGesture.TryParse(text, out var gesture));
            Assert.Equal(text, gesture.ToString());
        }
    }
}
