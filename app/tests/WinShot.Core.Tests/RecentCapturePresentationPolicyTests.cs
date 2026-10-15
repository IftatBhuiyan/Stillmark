using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using WinShot.App;
using Xunit;

namespace WinShot.Core.Tests;

public class RecentCapturePresentationPolicyTests
{
    private static readonly DateTime CapturedAt = new(2026, 7, 29, 9, 15, 0);
    private static readonly CultureInfo TestCulture =
        CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData((int)RecentCapturePresentationState.Loading, true, "Preview loading")]
    [InlineData((int)RecentCapturePresentationState.Available, true, "Preview available")]
    [InlineData((int)RecentCapturePresentationState.NetworkLocation, true, "Network image; open to access")]
    [InlineData(
        (int)RecentCapturePresentationState.PreviewUnavailable,
        true,
        "Preview unavailable; capture can still be opened")]
    [InlineData(
        (int)RecentCapturePresentationState.CaptureUnavailable,
        false,
        "Capture unavailable")]
    [InlineData(
        (int)RecentCapturePresentationState.OpenFailed,
        true,
        "Could not open capture")]
    public void Create_MapsActionabilityAndStatus(
        int stateValue,
        bool expectedActionable,
        string expectedStatus)
    {
        var state = (RecentCapturePresentationState)stateValue;
        RecentCapturePresentation result =
            RecentCapturePresentationPolicy.Create(
                CapturedAt,
                position: 2,
                count: 5,
                state,
                TestCulture);

        Assert.Equal(expectedActionable, result.IsActionable);
        Assert.Equal(expectedStatus, result.ItemStatus);
    }

    [Fact]
    public void Create_UsesExactPathPrivateOrdinalAndTimestampMetadata()
    {
        RecentCapturePresentation result =
            RecentCapturePresentationPolicy.Create(
                CapturedAt,
                position: 2,
                count: 5,
                RecentCapturePresentationState.Available,
                TestCulture);

        Assert.Equal(
            "Recent capture 2 of 5, captured July 29 at 9:15 AM. Preview available.",
            result.AutomationName);
        Assert.Equal(
            "Open this capture in the default image app.",
            result.HelpText);
        Assert.DoesNotContain('\\', result.AutomationName);
        Assert.DoesNotContain(".png", result.AutomationName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    public void Create_RejectsInvalidPositionOrCount(int position, int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecentCapturePresentationPolicy.Create(
                CapturedAt,
                position,
                count,
                RecentCapturePresentationState.Available,
                TestCulture));
    }

    [Theory]
    [InlineData(
        (int)HistoryThumbnailStatus.Loaded,
        (int)RecentCapturePresentationState.Available)]
    [InlineData(
        (int)HistoryThumbnailStatus.Unreadable,
        (int)RecentCapturePresentationState.PreviewUnavailable)]
    [InlineData(
        (int)HistoryThumbnailStatus.Missing,
        (int)RecentCapturePresentationState.CaptureUnavailable)]
    [InlineData(
        (int)HistoryThumbnailStatus.Rejected,
        (int)RecentCapturePresentationState.CaptureUnavailable)]
    [InlineData(
        (int)HistoryThumbnailStatus.Remote,
        (int)RecentCapturePresentationState.NetworkLocation)]
    public void FromThumbnailStatus_MapsHonestPresentation(
        int statusValue,
        int expectedValue)
    {
        Assert.Equal(
            (RecentCapturePresentationState)expectedValue,
            RecentCapturePresentationPolicy.FromThumbnailStatus(
                (HistoryThumbnailStatus)statusValue));
    }

    [Fact]
    public void NativeButton_ExposesButtonInvokeAndPrivateSetMetadata()
    {
        RunOnStaThread(() =>
        {
            const string sentinelPath = @"C:\Private\User\secret-capture.png";
            var button = new RecentCaptureButton("entry-private-id", position: 1, count: 2);
            RecentCapturePresentation presentation =
                RecentCapturePresentationPolicy.Create(
                    CapturedAt,
                    position: 1,
                    count: 2,
                    RecentCapturePresentationState.Available,
                    TestCulture);
            AutomationProperties.SetName(button, presentation.AutomationName);
            AutomationProperties.SetHelpText(button, presentation.HelpText);
            AutomationProperties.SetItemStatus(button, presentation.ItemStatus);

            var peer = new ButtonAutomationPeer(button);

            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            Assert.IsAssignableFrom<IInvokeProvider>(
                peer.GetPattern(PatternInterface.Invoke));
            Assert.Equal("RecentCapture_0", AutomationProperties.GetAutomationId(button));
            Assert.Equal(1, AutomationProperties.GetPositionInSet(button));
            Assert.Equal(2, AutomationProperties.GetSizeOfSet(button));
            Assert.DoesNotContain(
                sentinelPath,
                AutomationProperties.GetName(button),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "secret-capture.png",
                AutomationProperties.GetHelpText(button),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "entry-private-id",
                AutomationProperties.GetItemStatus(button),
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ActivationGuard_AllowsOneRouteUntilCompletion()
    {
        RunOnStaThread(() =>
        {
            var button = new RecentCaptureButton("entry-1", position: 1, count: 1);

            Assert.True(button.TryBeginActivation());
            Assert.False(button.TryBeginActivation());
            button.EndActivation();
            Assert.True(button.TryBeginActivation());
            button.EndActivation();
        });
    }

    [Theory]
    [InlineData(null, 1, "recent-position-1")]
    [InlineData("", 2, "recent-position-2")]
    [InlineData("   ", 3, "recent-position-3")]
    [InlineData("persisted-id", 4, "persisted-id")]
    public void EntryIdentity_CreatesPrivateBoundedFocusKey(
        string? persistedId,
        int position,
        string expected)
    {
        Assert.Equal(
            expected,
            RecentCaptureEntryIdentityPolicy.CreateFocusKey(
                persistedId,
                position));
    }

    [Fact]
    public void EntryIdentity_ExcessiveIdUsesOrdinalFallback()
    {
        string excessive = new(
            'x',
            RecentCaptureEntryIdentityPolicy.MaximumPersistedIdLength + 1);

        Assert.Equal(
            "recent-position-5",
            RecentCaptureEntryIdentityPolicy.CreateFocusKey(excessive, 5));
    }

    [Fact]
    public void ThumbnailPresentation_DoesNotOverwriteNewerActivationState()
    {
        RunOnStaThread(() =>
        {
            var button = new RecentCaptureButton("entry-1", position: 1, count: 1);
            button.Resources["SurfaceRaisedBrush"] =
                new SolidColorBrush(Color.FromRgb(36, 37, 45));
            int loadingRevision = button.ApplyPresentation(
                CapturedAt,
                RecentCapturePresentationState.Loading);
            button.ApplyPresentation(
                CapturedAt,
                RecentCapturePresentationState.OpenFailed);

            bool applied = button.TryApplyThumbnailPresentation(
                loadingRevision,
                CapturedAt,
                RecentCapturePresentationState.Available,
                image: null);

            Assert.False(applied);
            Assert.Equal(
                "Could not open capture",
                AutomationProperties.GetItemStatus(button));
        });
    }

    [Fact]
    public void FocusPolicy_PreservesUniqueAvailableEntry()
    {
        int? result = RecentCaptureFocusPolicy.FindRecoveryIndex(
            ["new", "kept", "old"],
            [true, true, true],
            previousEntryId: "kept",
            previousIndex: 2);

        Assert.Equal(1, result);
    }

    [Fact]
    public void FocusPolicy_UnavailableEntryPrefersNextThenPrevious()
    {
        int? next = RecentCaptureFocusPolicy.FindRecoveryIndex(
            ["new", "missing", "older"],
            [true, false, true],
            previousEntryId: "missing",
            previousIndex: 1);
        int? previous = RecentCaptureFocusPolicy.FindRecoveryIndex(
            ["new", "missing"],
            [true, false],
            previousEntryId: "missing",
            previousIndex: 1);

        Assert.Equal(2, next);
        Assert.Equal(0, previous);
    }

    [Fact]
    public void FocusPolicy_DuplicateIdFallsBackToPreviousIndex()
    {
        int? result = RecentCaptureFocusPolicy.FindRecoveryIndex(
            ["duplicate", "other", "duplicate"],
            [true, true, true],
            previousEntryId: "duplicate",
            previousIndex: 2);

        Assert.Equal(2, result);
    }

    [Fact]
    public void FocusPolicy_FallbackAndPersistedIdCollisionUsesPreviousIndex()
    {
        string fallback =
            RecentCaptureEntryIdentityPolicy.CreateFocusKey(null, position: 1);
        string collidingPersisted =
            RecentCaptureEntryIdentityPolicy.CreateFocusKey(
                "recent-position-1",
                position: 2);

        int? result = RecentCaptureFocusPolicy.FindRecoveryIndex(
            [fallback, collidingPersisted],
            [true, true],
            previousEntryId: fallback,
            previousIndex: 0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void FocusPolicy_NoActionableEntryFallsBackOutsideStrip()
    {
        int? result = RecentCaptureFocusPolicy.FindRecoveryIndex(
            ["missing", "rejected"],
            [false, false],
            previousEntryId: "missing",
            previousIndex: 0);

        Assert.Null(result);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test thread timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
