using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace KillerPDF
{

    // Shared window transitions. EnableFadeClose makes a window fade out instead of vanishing when it
    // closes: the first close request is cancelled, opacity animates to 0, then the window really closes.
    // Works for modal dialogs too - the DialogResult is already set by the time Closing fires, so it
    // survives the deferred close.
    internal static class WindowFx
    {
        public const int FadeMs = 150;

        public static void EnableFadeClose(Window w, int ms = FadeMs)
        {
            bool fading = false;
            bool readyToClose = false;
            w.Closing += (s, e) =>
            {
                if (readyToClose) return;  // our own post-fade Close - let it through
                // Cancel EVERY other close attempt (incl. the second one from a "DialogResult = x; Close();"
                // pair) so the window can't slip out from under the fade. DialogResult, if set, is preserved.
                e.Cancel = true;
                if (fading) return;        // already fading - ignore repeat triggers
                fading = true;
                var anim = new DoubleAnimation(w.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(ms)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                anim.Completed += (_, _) => { readyToClose = true; w.Close(); };
                w.BeginAnimation(UIElement.OpacityProperty, anim);
            };
        }
    }
}
