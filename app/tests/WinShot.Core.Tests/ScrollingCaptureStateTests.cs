using WinShot.App.Scrolling;
using WinShot.Capture;
using WinShot.Core;
using WinShot.Core.Update;
using Xunit;

namespace WinShot.Core.Tests;

public sealed class ScrollingCaptureStateTests
{
    [Fact]
    public void ControllerRefusesScrollingCaptureBeforeOpeningUiDuringUpdateQuiescence()
    {
        var activities = new UpdateActivityLedger();
        Assert.True(activities.TryBeginQuiescence(out UpdateQuiescenceLease? quiescence));
        using (quiescence)
        {
            var controller = new ScrollingCaptureController(activities);
            string? message = null;
            controller.Notify += (_, body, _) => message = body;

            controller.Start(new PixelRect(0, 0, 100, 100));

            Assert.False(controller.IsActive);
            Assert.NotNull(message);
            Assert.Contains("update", message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                new UpdateActivitySnapshot(false, false, false),
                activities.GetCurrentActivity());
        }
    }

    [Fact]
    public void BudgetPreflightRejectsInvalidAndOversizedFrames()
    {
        Assert.False(ScrollCaptureBudget.CanStart(0, 100, 1024, out _, out _));
        Assert.False(ScrollCaptureBudget.CanStart(100, 100, 39_999, out _, out _));
        Assert.False(ScrollCaptureBudget.CanStart(
            int.MaxValue,
            int.MaxValue,
            long.MaxValue,
            out long overflowPixels,
            out long overflowBytes));
        Assert.Equal(0, overflowPixels);
        Assert.Equal(0, overflowBytes);

        Assert.True(ScrollCaptureBudget.CanStart(100, 100, 40_000, out long pixels, out long bytes));
        Assert.Equal(10_000, pixels);
        Assert.Equal(40_000, bytes);
    }

    [Fact]
    public void LimitReachedRemainsDeliverableUntilFinishCompletes()
    {
        var state = new ScrollCaptureSessionState();

        Assert.True(state.TryMarkLimitReached());
        Assert.Equal(ScrollCaptureState.LimitReached, state.State);
        Assert.True(state.CanPublish);
        Assert.True(state.TryRequestFinish());
        Assert.False(state.TryCancel());
        Assert.True(state.TryComplete());
        Assert.Equal(ScrollCaptureState.Completed, state.State);
        Assert.False(state.CanPublish);
    }

    [Fact]
    public void CancelWinsExactlyOnce()
    {
        var state = new ScrollCaptureSessionState();

        Assert.True(state.TryCancel());
        Assert.False(state.TryCancel());
        Assert.False(state.TryRequestFinish());
        Assert.False(state.TryFail());
        Assert.Equal(ScrollCaptureState.Cancelled, state.State);
    }

    [Fact]
    public void FinalizationFailureCannotPublishCompletedResult()
    {
        var state = new ScrollCaptureSessionState();

        Assert.True(state.TryRequestFinish());
        Assert.True(state.TryFail());
        Assert.False(state.TryComplete());
        Assert.Equal(ScrollCaptureState.Failed, state.State);
    }
}
