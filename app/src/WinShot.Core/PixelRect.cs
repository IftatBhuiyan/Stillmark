namespace WinShot.Core;

/// <summary>A rectangle in physical (device) pixels, in virtual-screen coordinates.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int px, int py) => px >= X && px < Right && py >= Y && py < Bottom;

    public static PixelRect FromLtrb(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public static PixelRect FromCorners(int x1, int y1, int x2, int y2) =>
        FromLtrb(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public PixelRect Intersect(PixelRect other)
    {
        int l = Math.Max(X, other.X);
        int t = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right);
        int b = Math.Min(Bottom, other.Bottom);
        return r > l && b > t ? FromLtrb(l, t, r, b) : default;
    }
}
