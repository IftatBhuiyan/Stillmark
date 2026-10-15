using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class TransientOverlayKeyboardInteractionTests
{
    [Theory]
    [InlineData(true, System.Windows.DragDropEffects.None, false)]
    [InlineData(true, System.Windows.DragDropEffects.Copy, true)]
    [InlineData(false, System.Windows.DragDropEffects.Copy, false)]
    public void Canceled_drag_keeps_the_capture_available(bool closeAfterDrag,
        System.Windows.DragDropEffects result, bool shouldClose)
    {
        Assert.Equal(shouldClose,
            WinShot.App.QuickAccess.QuickAccessWindow.ShouldDismissAfterDrag(closeAfterDrag, result));
    }

    [Fact]
    public void OrderedActions_AreStableAndSurfaceSpecific()
    {
        Assert.Equal(
            [
                TransientOverlayCommand.Copy,
                TransientOverlayCommand.Annotate,
                TransientOverlayCommand.Pin,
                TransientOverlayCommand.Save,
                TransientOverlayCommand.Open,
                TransientOverlayCommand.Close,
            ],
            TransientOverlayKeyboardInteraction.GetOrderedActions(
                TransientOverlaySurface.QuickAccessImage));
        Assert.Equal(
            [
                TransientOverlayCommand.Copy,
                TransientOverlayCommand.Save,
                TransientOverlayCommand.Open,
                TransientOverlayCommand.Close,
            ],
            TransientOverlayKeyboardInteraction.GetOrderedActions(
                TransientOverlaySurface.QuickAccessRecording));
        Assert.Equal(
            [
                TransientOverlayCommand.Copy,
                TransientOverlayCommand.Save,
                TransientOverlayCommand.Close,
            ],
            TransientOverlayKeyboardInteraction.GetOrderedActions(
                TransientOverlaySurface.Pin));
    }

    [Theory]
    [InlineData(TransientOverlaySurface.QuickAccessImage, TransientOverlayKey.C, TransientOverlayCommand.Copy)]
    [InlineData(TransientOverlaySurface.QuickAccessImage, TransientOverlayKey.E, TransientOverlayCommand.Annotate)]
    [InlineData(TransientOverlaySurface.QuickAccessImage, TransientOverlayKey.P, TransientOverlayCommand.Pin)]
    [InlineData(TransientOverlaySurface.QuickAccessImage, TransientOverlayKey.S, TransientOverlayCommand.Save)]
    [InlineData(TransientOverlaySurface.QuickAccessImage, TransientOverlayKey.O, TransientOverlayCommand.Open)]
    [InlineData(TransientOverlaySurface.QuickAccessRecording, TransientOverlayKey.S, TransientOverlayCommand.Save)]
    [InlineData(TransientOverlaySurface.QuickAccessRecording, TransientOverlayKey.C, TransientOverlayCommand.Copy)]
    [InlineData(TransientOverlaySurface.QuickAccessRecording, TransientOverlayKey.O, TransientOverlayCommand.Open)]
    [InlineData(TransientOverlaySurface.Pin, TransientOverlayKey.C, TransientOverlayCommand.Copy)]
    [InlineData(TransientOverlaySurface.Pin, TransientOverlayKey.S, TransientOverlayCommand.Save)]
    public void ControlShortcut_ResolvesSupportedCommand(
        object surfaceValue,
        object keyValue,
        object expectedValue)
    {
        var surface = (TransientOverlaySurface)surfaceValue;
        var key = (TransientOverlayKey)keyValue;
        var expected = (TransientOverlayCommand)expectedValue;
        Assert.Equal(
            expected,
            TransientOverlayKeyboardInteraction.Resolve(
                surface,
                key,
                TransientOverlayModifiers.Control,
                isRepeat: false));
    }

    [Theory]
    [InlineData(TransientOverlaySurface.QuickAccessImage)]
    [InlineData(TransientOverlaySurface.QuickAccessRecording)]
    [InlineData(TransientOverlaySurface.Pin)]
    public void EscapeWithoutModifiers_ClosesEverySurface(
        object surfaceValue)
    {
        var surface = (TransientOverlaySurface)surfaceValue;
        Assert.Equal(
            TransientOverlayCommand.Close,
            TransientOverlayKeyboardInteraction.Resolve(
                surface,
                TransientOverlayKey.Escape,
                TransientOverlayModifiers.None,
                isRepeat: false));
    }

    [Theory]
    [InlineData(TransientOverlayKey.E)]
    [InlineData(TransientOverlayKey.P)]
    public void RecordingCard_RejectsImageOnlyCommands(object keyValue)
    {
        var key = (TransientOverlayKey)keyValue;
        Assert.Equal(
            TransientOverlayCommand.None,
            TransientOverlayKeyboardInteraction.Resolve(
                TransientOverlaySurface.QuickAccessRecording,
                key,
                TransientOverlayModifiers.Control,
                isRepeat: false));
    }

    [Theory]
    [InlineData(TransientOverlayModifiers.None)]
    [InlineData(TransientOverlayModifiers.Shift)]
    [InlineData(TransientOverlayModifiers.Alt)]
    [InlineData(TransientOverlayModifiers.Windows)]
    [InlineData(TransientOverlayModifiers.Control | TransientOverlayModifiers.Shift)]
    [InlineData(TransientOverlayModifiers.Control | TransientOverlayModifiers.Alt)]
    [InlineData(TransientOverlayModifiers.Control | TransientOverlayModifiers.Windows)]
    public void UnsupportedOrExtraModifiers_PassThrough(
        object modifiersValue)
    {
        var modifiers = (TransientOverlayModifiers)modifiersValue;
        Assert.Equal(
            TransientOverlayCommand.None,
            TransientOverlayKeyboardInteraction.Resolve(
                TransientOverlaySurface.QuickAccessImage,
                TransientOverlayKey.C,
                modifiers,
                isRepeat: false));
        Assert.Equal(
            TransientOverlayCommand.None,
            TransientOverlayKeyboardInteraction.Resolve(
                TransientOverlaySurface.Pin,
                TransientOverlayKey.Escape,
                modifiers == TransientOverlayModifiers.None
                    ? TransientOverlayModifiers.Control
                    : modifiers,
                isRepeat: false));
    }

    [Theory]
    [InlineData(TransientOverlaySurface.QuickAccessImage, TransientOverlayKey.C, TransientOverlayModifiers.Control)]
    [InlineData(TransientOverlaySurface.QuickAccessRecording, TransientOverlayKey.O, TransientOverlayModifiers.Control)]
    [InlineData(TransientOverlaySurface.Pin, TransientOverlayKey.S, TransientOverlayModifiers.Control)]
    [InlineData(TransientOverlaySurface.Pin, TransientOverlayKey.Escape, TransientOverlayModifiers.None)]
    public void RepeatedActivation_IsInert(
        object surfaceValue,
        object keyValue,
        object modifiersValue)
    {
        var surface = (TransientOverlaySurface)surfaceValue;
        var key = (TransientOverlayKey)keyValue;
        var modifiers = (TransientOverlayModifiers)modifiersValue;
        Assert.Equal(
            TransientOverlayCommand.None,
            TransientOverlayKeyboardInteraction.Resolve(
                surface,
                key,
                modifiers,
                isRepeat: true));
    }

    [Fact]
    public void UnknownKey_PassesThrough()
    {
        Assert.Equal(
            TransientOverlayCommand.None,
            TransientOverlayKeyboardInteraction.Resolve(
                TransientOverlaySurface.Pin,
                TransientOverlayKey.None,
                TransientOverlayModifiers.Control,
                isRepeat: false));
    }

    [Fact]
    public void UnknownSurface_IsRejected()
    {
        var unknown = (TransientOverlaySurface)int.MaxValue;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransientOverlayKeyboardInteraction.GetOrderedActions(unknown));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransientOverlayKeyboardInteraction.Resolve(
                unknown,
                TransientOverlayKey.C,
                TransientOverlayModifiers.Control,
                isRepeat: false));
    }

    [Theory]
    [InlineData(3, TransientOverlayNavigation.Previous, 2)]
    [InlineData(3, TransientOverlayNavigation.Next, 5)]
    [InlineData(3, TransientOverlayNavigation.First, 0)]
    [InlineData(3, TransientOverlayNavigation.Last, 5)]
    [InlineData(0, TransientOverlayNavigation.Previous, 5)]
    [InlineData(5, TransientOverlayNavigation.Next, 0)]
    public void Navigation_SkipsDisabledActions(
        int currentIndex,
        object navigationValue,
        int expectedIndex)
    {
        var navigation = (TransientOverlayNavigation)navigationValue;
        bool[] enabled = [true, false, true, false, false, true];

        Assert.Equal(
            expectedIndex,
            TransientOverlayKeyboardInteraction.FindEnabledAction(
                currentIndex,
                enabled,
                navigation));
    }

    [Fact]
    public void Navigation_WithNoEnabledAction_ReturnsNoTarget()
    {
        Assert.Equal(
            -1,
            TransientOverlayKeyboardInteraction.FindEnabledAction(
                1,
                [false, false, false],
                TransientOverlayNavigation.Next));
    }
}
