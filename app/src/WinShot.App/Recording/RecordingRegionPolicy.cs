using WinShot.Core;
using WinShot.Capture;

namespace WinShot.App.Recording;

/// <summary>
/// ScreenRecorderLib records a single display source. Keep the user's requested rectangle intact
/// or reject it; silently intersecting it would produce a recording different from the selection.
/// </summary>
internal static class RecordingRegionPolicy
{
    internal static bool TryUseSingleDisplay(
        PixelRect requested,
        IReadOnlyList<MonitorInfo> monitors,
        out PixelRect region)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        region = default;
        if (requested.IsEmpty)
            return false;

        foreach (MonitorInfo monitor in monitors)
        {
            if (requested.Intersect(monitor.Bounds) != requested)
                continue;

            region = requested;
            return true;
        }

        return false;
    }
}
