namespace WinShot.App;

internal enum TransientOverlaySurface
{
    QuickAccessImage,
    QuickAccessRecording,
    Pin,
}

internal enum TransientOverlayKey
{
    None,
    Escape,
    C,
    E,
    O,
    P,
    S,
}

[Flags]
internal enum TransientOverlayModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8,
}

internal enum TransientOverlayCommand
{
    None,
    Copy,
    Annotate,
    Pin,
    Save,
    Open,
    Close,
}

internal enum TransientOverlayNavigation
{
    Previous,
    Next,
    First,
    Last,
}

/// <summary>
/// UI-free shortcut and action-order policy for transient overlays after the user explicitly
/// activates them. The windows remain responsible for preserving their initial no-activate state.
/// </summary>
internal static class TransientOverlayKeyboardInteraction
{
    private static readonly IReadOnlyList<TransientOverlayCommand> QuickAccessImageActions =
        Array.AsReadOnly<TransientOverlayCommand>(
        [
            TransientOverlayCommand.Copy,
            TransientOverlayCommand.Annotate,
            TransientOverlayCommand.Pin,
            TransientOverlayCommand.Save,
            TransientOverlayCommand.Open,
            TransientOverlayCommand.Close,
        ]);

    private static readonly IReadOnlyList<TransientOverlayCommand> QuickAccessRecordingActions =
        Array.AsReadOnly<TransientOverlayCommand>(
        [
            TransientOverlayCommand.Copy,
            TransientOverlayCommand.Save,
            TransientOverlayCommand.Open,
            TransientOverlayCommand.Close,
        ]);

    private static readonly IReadOnlyList<TransientOverlayCommand> PinActions =
        Array.AsReadOnly<TransientOverlayCommand>(
        [
            TransientOverlayCommand.Copy,
            TransientOverlayCommand.Save,
            TransientOverlayCommand.Close,
        ]);

    internal static IReadOnlyList<TransientOverlayCommand> GetOrderedActions(
        TransientOverlaySurface surface) =>
        surface switch
        {
            TransientOverlaySurface.QuickAccessImage => QuickAccessImageActions,
            TransientOverlaySurface.QuickAccessRecording => QuickAccessRecordingActions,
            TransientOverlaySurface.Pin => PinActions,
            _ => throw new ArgumentOutOfRangeException(nameof(surface)),
        };

    internal static TransientOverlayCommand Resolve(
        TransientOverlaySurface surface,
        TransientOverlayKey key,
        TransientOverlayModifiers modifiers,
        bool isRepeat)
    {
        _ = GetOrderedActions(surface);
        if (isRepeat)
            return TransientOverlayCommand.None;

        if (key == TransientOverlayKey.Escape)
        {
            return modifiers == TransientOverlayModifiers.None
                ? TransientOverlayCommand.Close
                : TransientOverlayCommand.None;
        }

        if (modifiers != TransientOverlayModifiers.Control)
            return TransientOverlayCommand.None;

        return surface switch
        {
            TransientOverlaySurface.QuickAccessImage => key switch
            {
                TransientOverlayKey.C => TransientOverlayCommand.Copy,
                TransientOverlayKey.E => TransientOverlayCommand.Annotate,
                TransientOverlayKey.P => TransientOverlayCommand.Pin,
                TransientOverlayKey.S => TransientOverlayCommand.Save,
                TransientOverlayKey.O => TransientOverlayCommand.Open,
                _ => TransientOverlayCommand.None,
            },
            TransientOverlaySurface.QuickAccessRecording => key switch
            {
                TransientOverlayKey.C => TransientOverlayCommand.Copy,
                TransientOverlayKey.S => TransientOverlayCommand.Save,
                TransientOverlayKey.O => TransientOverlayCommand.Open,
                _ => TransientOverlayCommand.None,
            },
            TransientOverlaySurface.Pin => key switch
            {
                TransientOverlayKey.C => TransientOverlayCommand.Copy,
                TransientOverlayKey.S => TransientOverlayCommand.Save,
                _ => TransientOverlayCommand.None,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(surface)),
        };
    }

    internal static int FindEnabledAction(
        int currentIndex,
        IReadOnlyList<bool> enabledActions,
        TransientOverlayNavigation navigation)
    {
        ArgumentNullException.ThrowIfNull(enabledActions);
        if (currentIndex < 0 || currentIndex >= enabledActions.Count)
            throw new ArgumentOutOfRangeException(nameof(currentIndex));

        if (navigation is TransientOverlayNavigation.First or TransientOverlayNavigation.Last)
        {
            int start = navigation == TransientOverlayNavigation.First
                ? 0
                : enabledActions.Count - 1;
            int end = navigation == TransientOverlayNavigation.First
                ? enabledActions.Count
                : -1;
            int step = navigation == TransientOverlayNavigation.First ? 1 : -1;
            for (int index = start; index != end; index += step)
            {
                if (enabledActions[index])
                    return index;
            }

            return -1;
        }

        int offset = navigation switch
        {
            TransientOverlayNavigation.Previous => -1,
            TransientOverlayNavigation.Next => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(navigation)),
        };
        for (int distance = 1; distance <= enabledActions.Count; distance++)
        {
            int index = (currentIndex + (offset * distance)) % enabledActions.Count;
            if (index < 0)
                index += enabledActions.Count;
            if (enabledActions[index])
                return index;
        }

        return -1;
    }
}
