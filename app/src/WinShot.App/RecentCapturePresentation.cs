using System.Globalization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinShot.App;

internal enum RecentCapturePresentationState
{
    Loading,
    Available,
    PreviewUnavailable,
    CaptureUnavailable,
    OpenFailed,
    NetworkLocation,
}

internal readonly record struct RecentCapturePresentation(
    bool IsActionable,
    string AutomationName,
    string HelpText,
    string ItemStatus,
    string ToolTip,
    string PlaceholderText);

internal static class RecentCapturePresentationPolicy
{
    internal static RecentCapturePresentation Create(
        DateTime timestamp,
        int position,
        int count,
        RecentCapturePresentationState state,
        CultureInfo? culture = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, position);

        culture ??= CultureInfo.CurrentCulture;
        string capturedAt = timestamp.ToString("MMMM d 'at' h:mm tt", culture);
        string shortCapturedAt = timestamp.ToString("MMM d, h:mm tt", culture);
        string prefix = $"Recent capture {position} of {count}, captured {capturedAt}.";

        return state switch
        {
            RecentCapturePresentationState.Loading => new RecentCapturePresentation(
                IsActionable: true,
                AutomationName: $"{prefix} Preview loading.",
                HelpText: "Open this capture in the default image app.",
                ItemStatus: "Preview loading",
                ToolTip: $"{shortCapturedAt} — Preview loading",
                PlaceholderText: "•••"),
            RecentCapturePresentationState.Available => new RecentCapturePresentation(
                IsActionable: true,
                AutomationName: $"{prefix} Preview available.",
                HelpText: "Open this capture in the default image app.",
                ItemStatus: "Preview available",
                ToolTip: $"{shortCapturedAt} — Open capture",
                PlaceholderText: ""),
            RecentCapturePresentationState.PreviewUnavailable => new RecentCapturePresentation(
                IsActionable: true,
                AutomationName: $"{prefix} Preview unavailable; capture can still be opened.",
                HelpText: "Open this capture in the default image app.",
                ItemStatus: "Preview unavailable; capture can still be opened",
                ToolTip: $"{shortCapturedAt} — Preview unavailable; open capture",
                PlaceholderText: "Preview unavailable"),
            RecentCapturePresentationState.CaptureUnavailable => new RecentCapturePresentation(
                IsActionable: false,
                AutomationName: $"{prefix} Capture unavailable.",
                HelpText: "This recent capture is no longer available.",
                ItemStatus: "Capture unavailable",
                ToolTip: $"{shortCapturedAt} — Capture unavailable",
                PlaceholderText: "Unavailable"),
            RecentCapturePresentationState.NetworkLocation => new RecentCapturePresentation(
                IsActionable: true,
                AutomationName: $"{prefix} Saved in a network location. Preview not loaded.",
                HelpText: "Open this capture from its network location in the default image app.",
                ItemStatus: "Network image; open to access",
                ToolTip: $"{shortCapturedAt} — Open network image",
                PlaceholderText: "Network image"),
            RecentCapturePresentationState.OpenFailed => new RecentCapturePresentation(
                IsActionable: true,
                AutomationName: $"{prefix} The capture could not be opened.",
                HelpText: "Try opening this capture again.",
                ItemStatus: "Could not open capture",
                ToolTip: $"{shortCapturedAt} — Could not open; try again",
                PlaceholderText: "Could not open"),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }

    internal static RecentCapturePresentationState FromThumbnailStatus(
        HistoryThumbnailStatus status)
    {
        return status switch
        {
            HistoryThumbnailStatus.Loaded => RecentCapturePresentationState.Available,
            HistoryThumbnailStatus.Unreadable => RecentCapturePresentationState.PreviewUnavailable,
            HistoryThumbnailStatus.Remote => RecentCapturePresentationState.NetworkLocation,
            HistoryThumbnailStatus.Missing or HistoryThumbnailStatus.Rejected =>
                RecentCapturePresentationState.CaptureUnavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
    }
}

internal enum RecentCaptureActivationStatus
{
    Opened,
    Unavailable,
    Failed,
}

internal readonly record struct RecentCaptureActivationResult(
    RecentCaptureActivationStatus Status,
    Exception? Exception = null);

internal static class RecentCaptureActivator
{
    internal static Task<RecentCaptureActivationResult> OpenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => OpenOnWorker(path, cancellationToken),
            cancellationToken);
    }

    private static RecentCaptureActivationResult OpenOnWorker(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!HistoryThumbnailPolicy.TryNormalizeImagePathForShellAction(path, out string fullPath)
                || !File.Exists(fullPath))
            {
                return new RecentCaptureActivationResult(
                    RecentCaptureActivationStatus.Unavailable);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            return new RecentCaptureActivationResult(RecentCaptureActivationStatus.Opened);
        }
        catch (Exception ex) when (ex is Win32Exception
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return new RecentCaptureActivationResult(
                RecentCaptureActivationStatus.Failed,
                ex);
        }
    }
}

internal static class RecentCaptureFocusPolicy
{
    internal static int? FindRecoveryIndex(
        IReadOnlyList<string> entryIds,
        IReadOnlyList<bool> actionable,
        string? previousEntryId,
        int previousIndex)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        ArgumentNullException.ThrowIfNull(actionable);
        if (entryIds.Count != actionable.Count)
            throw new ArgumentException("Entry and actionability counts must match.");
        if (entryIds.Count == 0)
            return null;

        if (!string.IsNullOrEmpty(previousEntryId)
            && TryFindUniqueIndex(entryIds, previousEntryId, out int matchingIndex)
            && actionable[matchingIndex])
        {
            return matchingIndex;
        }

        int anchor = Math.Clamp(previousIndex, 0, entryIds.Count - 1);
        for (int distance = 0; distance < entryIds.Count; distance++)
        {
            int after = anchor + distance;
            if (after < entryIds.Count && actionable[after])
                return after;

            int before = anchor - distance;
            if (distance > 0 && before >= 0 && actionable[before])
                return before;
        }

        return null;
    }

    private static bool TryFindUniqueIndex(
        IReadOnlyList<string> values,
        string target,
        out int matchingIndex)
    {
        matchingIndex = -1;
        for (int index = 0; index < values.Count; index++)
        {
            if (!string.Equals(values[index], target, StringComparison.Ordinal))
                continue;
            if (matchingIndex >= 0)
                return false;
            matchingIndex = index;
        }
        return matchingIndex >= 0;
    }
}

internal static class RecentCaptureEntryIdentityPolicy
{
    internal const int MaximumPersistedIdLength = 128;

    internal static string CreateFocusKey(string? persistedId, int position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        return string.IsNullOrWhiteSpace(persistedId)
               || persistedId.Length > MaximumPersistedIdLength
            ? $"recent-position-{position}"
            : persistedId;
    }
}

internal sealed class RecentCaptureButton : Button
{
    private readonly TextBlock _placeholder;
    private int _activationInProgress;
    private int _presentationRevision;

    internal RecentCaptureButton(string entryId, int position, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, position);

        EntryId = entryId;
        Position = position;
        Count = count;
        _placeholder = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(8),
        };
        Content = _placeholder;
        AutomationProperties.SetAutomationId(this, $"RecentCapture_{position - 1}");
        AutomationProperties.SetPositionInSet(this, position);
        AutomationProperties.SetSizeOfSet(this, count);
        GotKeyboardFocus += (_, _) => BringIntoView();
    }

    internal string EntryId { get; }

    internal int Position { get; }

    internal int Count { get; }

    internal bool TryBeginActivation() =>
        Interlocked.CompareExchange(ref _activationInProgress, 1, 0) == 0;

    internal void EndActivation() => Volatile.Write(ref _activationInProgress, 0);

    internal void AnnounceStatusChange()
    {
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            return;

        AutomationPeer? peer =
            UIElementAutomationPeer.FromElement(this)
            ?? UIElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal int ApplyPresentation(
        DateTime timestamp,
        RecentCapturePresentationState state,
        ImageSource? image = null)
    {
        RecentCapturePresentation presentation =
            RecentCapturePresentationPolicy.Create(timestamp, Position, Count, state);
        IsEnabled = presentation.IsActionable;
        Focusable = presentation.IsActionable;
        ToolTip = presentation.ToolTip;
        AutomationProperties.SetName(this, presentation.AutomationName);
        AutomationProperties.SetHelpText(this, presentation.HelpText);
        AutomationProperties.SetItemStatus(this, presentation.ItemStatus);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Off);
        _placeholder.Text = presentation.PlaceholderText;

        if (image is null)
        {
            Background = (Brush)FindResource("SurfaceRaisedBrush");
            Content = _placeholder;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Unspecified);
            return ++_presentationRevision;
        }

        Content = null;
        Background = new ImageBrush(image)
        {
            Stretch = Stretch.UniformToFill,
        };
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        return ++_presentationRevision;
    }

    internal bool TryApplyThumbnailPresentation(
        int expectedRevision,
        DateTime timestamp,
        RecentCapturePresentationState state,
        ImageSource? image)
    {
        if (_presentationRevision != expectedRevision)
            return false;
        ApplyPresentation(timestamp, state, image);
        return true;
    }
}
