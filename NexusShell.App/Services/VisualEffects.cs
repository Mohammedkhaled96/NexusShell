using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using NexusShell.App.Models;

namespace NexusShell.App.Services
{
    /// <summary>
    /// App-wide, opt-in UI motion. Every method is a no-op when <see cref="Enabled"/> is
    /// false, so callers never have to branch. Static (like <see cref="AIAudioPlayer"/>)
    /// because it is invoked from Views / code-behind that are not DI-resolved.
    ///
    /// <para>CRITICAL: animate CHROME only — windows, the overlay panel, content opacity,
    /// input glow. NEVER animate the AvalonEdit terminal document or per-output lines: that
    /// makes a screen reader walk a huge UIA subtree and hangs the UI thread.</para>
    /// </summary>
    public static class VisualEffects
    {
        private static volatile bool _enabled = true;

        /// <summary>Master switch — honours the user setting AND the OS "show animations" flag.</summary>
        public static bool Enabled => _enabled;

        /// <summary>
        /// Applies the persisted preference, forcing motion OFF when the OS has animations
        /// disabled (reduce-motion) or rendering is software-only (Tier 0).
        /// </summary>
        public static void ApplySettings(AppSettings? settings)
            => SetUserPreference(settings?.EnableVisualEffects ?? true);

        /// <summary>Live toggle from Settings; still forced off by OS reduce-motion / software rendering.</summary>
        public static void SetUserPreference(bool enabled)
        {
            bool osAllows = SafeClientAreaAnimation() && (RenderCapability.Tier >> 16) > 0;
            _enabled = enabled && osAllows;
        }

        private static bool SafeClientAreaAnimation()
        {
            try { return SystemParameters.ClientAreaAnimation; }
            catch { return true; }
        }

        private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        /// <summary>
        /// Fade a window in on open. Sets Opacity to 0 BEFORE it is shown (no flash) and
        /// animates to 1 once loaded. Call immediately after creating the window, before Show().
        /// </summary>
        public static void FadeInWindow(Window? window, int ms = 180)
        {
            if (!_enabled || window == null) return;
            window.Opacity = 0;
            window.Loaded += (_, _) => BeginOpacity(window, 0.0, 1.0, ms);
        }

        /// <summary>Fade an element in (opacity 0 → 1).</summary>
        public static void FadeIn(FrameworkElement? element, int ms = 180)
        {
            if (!_enabled || element == null) return;
            BeginOpacity(element, 0.0, 1.0, ms);
        }

        /// <summary>Fade + gentle scale-up entrance (0.97 → 1) — e.g. the interactive overlay panel.</summary>
        public static void FadeInScale(FrameworkElement? element, int ms = 160)
        {
            if (!_enabled || element == null) return;

            var scale = EnsureScaleTransform(element);
            var anim = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            BeginOpacity(element, 0.0, 1.0, ms);
        }

        /// <summary>Quick fade for content swaps (e.g. switching the active terminal tab).</summary>
        public static void CrossFade(FrameworkElement? element, int ms = 140)
        {
            if (!_enabled || element == null) return;
            BeginOpacity(element, 0.35, 1.0, ms);
        }

        /// <summary>Turn a soft accent focus-glow on or off for an input element.</summary>
        public static void Glow(FrameworkElement? element, bool on)
        {
            if (element == null) return;
            if (!_enabled) { element.Effect = null; return; }

            if (element.Effect is not DropShadowEffect glow)
            {
                glow = new DropShadowEffect
                {
                    Color       = Color.FromRgb(0x0A, 0x84, 0xFF),
                    ShadowDepth = 0,
                    BlurRadius  = 12,
                    Opacity     = 0
                };
                element.Effect = glow;
            }

            double to = on ? 0.55 : 0.0;
            glow.BeginAnimation(DropShadowEffect.OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = EaseOut });
        }

        private static void BeginOpacity(UIElement element, double from, double to, int ms)
        {
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        }

        private static ScaleTransform EnsureScaleTransform(FrameworkElement element)
        {
            element.RenderTransformOrigin = new Point(0.5, 0.5);
            if (element.RenderTransform is ScaleTransform existing) return existing;
            var scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
            return scale;
        }
    }
}
