using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinShot.App;

internal static class ThemePulseFeedback
{
    internal static bool ShouldPulse(bool clientAreaAnimation, bool highContrast) =>
        clientAreaAnimation && !highContrast;

    internal static bool TryPulse(
        FrameworkElement element,
        bool clientAreaAnimation,
        bool highContrast)
    {
        if (!ShouldPulse(clientAreaAnimation, highContrast))
            return false;

        element.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var pulse = new DoubleAnimation
        {
            From = 1,
            To = 1.08,
            Duration = TimeSpan.FromMilliseconds(90),
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = easing,
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse.Clone());
        return true;
    }
}
