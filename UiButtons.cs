using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF
{
    // Single source of truth for themed dialog/popup buttons so the print dialog, signature popup,
    // KillerDialog, and any other dialog all share one look and can't drift apart.
    //
    //   accent == true  -> primary button: dim-accent fill, accent text; fills solid accent with
    //                      dark (BgModal) text on hover.
    //   accent == false -> secondary button: panel fill, primary text; panel-hover fill on hover.
    internal static class UiButtons
    {
        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];

        // Themed entry point for in-app dialogs/popups: resolves colors from the active theme.
        public static Button Make(object content, bool accent)
        {
            return accent
                ? Make(content, R("AccentDim"), R("Accent"),     R("Accent"),      AccentHoverFg(),  R("Accent"))
                : Make(content, R("BgPanel"),   R("BgHover"),    R("TextPrimary"), R("TextPrimary"), R("BorderDim"));
        }

        // Foreground for an accent button when hovered (solid-accent fill). White reads better than
        // the near-black BgModal on the bright accents of the Dark/Black themes; Light and the RGB
        // themes keep BgModal (their accent + modal pairing already reads well).
        private static Brush AccentHoverFg()
            => Services.ThemeManager.Current is Services.Theme.Dark or Services.Theme.Black
                ? System.Windows.Media.Brushes.White
                : R("BgModal");

        // Explicit-color entry point for self-contained windows that run before the theme is
        // loaded or on the failure path (the startup launcher, the crash dialog, the About box).
        // Same rounded template and hover-swap as the themed buttons, so the look can't drift.
        // Pass border = null for a borderless button.
        public static Button Make(object content, Brush normalBg, Brush hoverBg, Brush normalFg, Brush hoverFg, Brush? border = null)
        {
            var btn = new Button
            {
                Content         = content,
                Padding         = new Thickness(18, 6, 18, 6),
                Background      = normalBg,
                Foreground      = normalFg,
                BorderBrush     = border ?? Brushes.Transparent,
                BorderThickness = new Thickness(border == null ? 0 : 1),
                Cursor          = Cursors.Hand,
                FontFamily      = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                FontSize        = 12,
                FocusVisualStyle = null,
                Template        = Template(),
            };
            btn.MouseEnter += (_, _) => { btn.Background = hoverBg; btn.Foreground = hoverFg; };
            btn.MouseLeave += (_, _) => { btn.Background = normalBg; btn.Foreground = normalFg; };
            return btn;
        }

        // Rounded border that binds to the button's own Background/Border/Padding (so the hover
        // handlers' color swaps show), with a centred content presenter.
        private static ControlTemplate Template()
        {
            var bf = new FrameworkElementFactory(typeof(Border));
            bf.SetBinding(Border.BackgroundProperty,
                new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderBrushProperty,
                new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderThicknessProperty,
                new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.PaddingProperty,
                new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            return new ControlTemplate(typeof(Button)) { VisualTree = bf };
        }
    }
}
