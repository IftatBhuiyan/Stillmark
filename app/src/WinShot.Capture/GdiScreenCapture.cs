using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinShot.Core;
using WinShot.Interop;

namespace WinShot.Capture;

/// <summary>
/// GDI BitBlt screen capture. Solid and synchronous — used for stills.
/// A Windows.Graphics.Capture engine will join it in the recording phase.
/// </summary>
public static class GdiScreenCapture
{
    /// <summary>Captures a physical-pixel region of the virtual screen, optionally drawing the mouse cursor into it.</summary>
    public static BitmapSource CaptureRegion(PixelRect region, bool drawCursor = false)
    {
        if (region.IsEmpty)
            throw new ArgumentException("Capture region is empty.", nameof(region));

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC failed.");

        IntPtr memDc = IntPtr.Zero, dib = IntPtr.Zero, oldObj = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = region.Width,
                    biHeight = -region.Height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                },
            };
            dib = NativeMethods.CreateDIBSection(memDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection failed.");

            oldObj = NativeMethods.SelectObject(memDc, dib);
            if (!NativeMethods.BitBlt(memDc, 0, 0, region.Width, region.Height,
                    screenDc, region.X, region.Y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed (error {Marshal.GetLastWin32Error()}).");

            if (drawCursor)
                DrawCursor(memDc, region);

            int stride = region.Width * 4;
            // Bgr32, not Bgra32: BitBlt leaves the alpha channel zeroed, which would render fully transparent.
            var bitmap = BitmapSource.Create(
                region.Width, region.Height, 96, 96,
                PixelFormats.Bgr32, null, bits, stride * region.Height, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (oldObj != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
            if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Blits the live mouse cursor onto the capture DC at its screen position, honoring the hotspot.</summary>
    private static void DrawCursor(IntPtr memDc, PixelRect region)
    {
        var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.flags & NativeMethods.CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
            return;

        // Copy the cursor so DrawIconEx never races a shape change mid-draw, and so we can read its hotspot.
        IntPtr icon = NativeMethods.CopyIcon(info.hCursor);
        if (icon == IntPtr.Zero)
            return;

        int hotspotX = 0, hotspotY = 0;
        bool haveInfo = NativeMethods.GetIconInfo(icon, out var iconInfo);
        if (haveInfo)
        {
            hotspotX = iconInfo.xHotspot;
            hotspotY = iconInfo.yHotspot;
            if (iconInfo.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.hbmMask);
        }

        int x = info.ptScreenPos.X - region.X - hotspotX;
        int y = info.ptScreenPos.Y - region.Y - hotspotY;
        NativeMethods.DrawIconEx(memDc, x, y, icon, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
        NativeMethods.DestroyIcon(icon);
    }
}
