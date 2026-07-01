using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerPDF.Services;

namespace KillerPDF
{
    // ============================================================
    // Themed dialog - replaces MessageBox for dark-UI consistency
    // ============================================================
    internal static class KillerDialog
    {
        // Pulls the current theme brush at call time so dialogs respect light/dark/HC themes.
        private static SolidColorBrush R(string key)
            => (SolidColorBrush)Application.Current.Resources[key];

        // Carries the checkbox state of the last Show() call back to ShowWithCheckbox. Dialogs are
        // modal and UI-thread only, so a shared field is safe and avoids a duplicate dialog body.
        private static bool _lastCheckboxChecked;

#pragma warning disable IDE0060 // image intentionally kept for API parity with MessageBox; not yet rendered
        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None,
            bool fadeClose = true,
            string? checkboxText = null)
#pragma warning restore IDE0060
        {
            var result = MessageBoxResult.OK;
            bool boxChecked = false;

            var win = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height
            };
            DialogChrome.Configure(win, owner, fade: fadeClose);

            var outerBorder = new Border
            {
                Background = R("BgModal"),
                BorderBrush = R("AccentBorder"),   // match the app window / Settings card border, not the bright accent
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10),    // transparent halo so the drop shadow can render
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Direction = 270,
                    Opacity = 0.6
                }
            };

            var root = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                // Transparent so the dialog-wide film grain shows through the title bar too (it sits
                // over the same BgModal surface, so it still reads as one continuous surface).
                Background = Brushes.Transparent,
                Padding = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            // When the title is just "KillerPDF", render it as the main window's wordmark - "Killer"
            // in the primary text color and "PDF" in the green logo accent, bold, with a soft shadow.
            if (title == "KillerPDF")
            {
                var wm = new StackPanel { Orientation = Orientation.Horizontal };
                var wmTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                wmTb.Inlines.Add(new System.Windows.Documents.Run("Killer") { FontFamily = UiKit.WordmarkFont, FontWeight = FontWeights.Normal, FontSize = 15, Foreground = R("TextPrimary") });
                wmTb.Inlines.Add(new System.Windows.Documents.Run("PDF") { FontFamily = UiKit.WordmarkFontPdf, FontWeight = FontWeights.Bold, FontSize = 18, Foreground = R("AccentLogo") });
                wm.Children.Add(wmTb);
                // No DropShadowEffect on the text - it rasterizes and blurs the wordmark. Kept crisp.
                titleBar.Child = wm;
            }
            else
            {
                titleBar.Child = new TextBlock
                {
                    Text = title,
                    Foreground = R("Accent"),
                    FontWeight = FontWeights.Bold,   // blue title -> bold
                    FontSize = 14,
                    FontFamily = UiKit.MonoFont
                };
            }
            root.Children.Add(titleBar);

            // Message
            var msgBorder = new Border
            {
                Padding = new Thickness(20, 16, 20, 8),
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = R("TextPrimary"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            root.Children.Add(msgBorder);

            // Optional checkbox (e.g. "Remember my choice"). Extra top padding sets it apart from the message.
            if (checkboxText is not null)
            {
                var chk = UiKit.CheckBox(checkboxText);
                chk.Margin = new Thickness(20, 10, 20, 4);
                chk.Checked += (_, _2) => boxChecked = true;
                chk.Unchecked += (_, _2) => boxChecked = false;
                root.Children.Add(chk);
            }

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            // Build a minimal ControlTemplate so Background binds correctly and
            // WPF's default blue hover chrome can't override our colors.
            Button MakeBtn(string label, MessageBoxResult res, bool accent = false)
            {
                // Shared themed button (UiKit.Make) so this dialog matches the print dialog et al.
                var btn = UiKit.Make(label, accent);
                btn.Margin = new Thickness(8, 0, 0, 0);
                btn.IsDefault = accent;                           // Enter triggers the primary action
                btn.IsCancel  = res == MessageBoxResult.Cancel;   // Esc triggers Cancel where there is one
                btn.Click += (_, _2) => { result = res; win.Close(); };
                return btn;
            }

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    btnPanel.Children.Add(MakeBtn("OK", MessageBoxResult.OK, accent: true));
                    break;
                case MessageBoxButton.OKCancel:
                    btnPanel.Children.Add(MakeBtn("OK", MessageBoxResult.OK, accent: true));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
                    break;
                case MessageBoxButton.YesNo:
                    btnPanel.Children.Add(MakeBtn("Yes", MessageBoxResult.Yes, accent: true));
                    btnPanel.Children.Add(MakeBtn("No", MessageBoxResult.No));
                    break;
                case MessageBoxButton.YesNoCancel:
                    btnPanel.Children.Add(MakeBtn("Yes", MessageBoxResult.Yes, accent: true));
                    btnPanel.Children.Add(MakeBtn("No", MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
                    break;
            }

            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 8, 16, 16),
                Child = btnPanel
            });

            // Paint the same film-grain texture the app's panels use, behind the content, so the
            // dialog reads as part of the same surface family instead of a flat box.
            var contentGrid = new Grid();
            var grain = (owner as MainWindow)?.GrainTexture;
            if (grain is not null)
            {
                double grainOpacity = Application.Current.Resources["GrainOpacity"] is double go ? go : 0.05;
                contentGrid.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Opacity = grainOpacity,
                    Background = new System.Windows.Media.ImageBrush(grain)
                    {
                        TileMode = System.Windows.Media.TileMode.Tile,
                        ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, 256, 256),
                        Stretch = System.Windows.Media.Stretch.None
                    }
                });
            }
            contentGrid.Children.Add(root);
            outerBorder.Child = contentGrid;

            win.Content = outerBorder;
            win.ShowDialog();
            _lastCheckboxChecked = boxChecked;
            return result;
        }

        /// <summary>
        /// Like <see cref="Show"/> but with a custom set of buttons. Returns the index of the clicked
        /// button, or -1 if the dialog was closed without a choice. The button at <paramref name="accentIndex"/>
        /// is rendered as the primary (accent) action.
        /// </summary>
        public static int ShowChoices(
            Window? owner,
            string message,
            string[] labels,
            int accentIndex = 0,
            string title = "KillerPDF")
        {
            int result = -1;

            var win = new Window { Title = title, MinWidth = 380, MaxWidth = 760, SizeToContent = SizeToContent.WidthAndHeight };
            DialogChrome.Configure(win, owner, fade: true);

            var outerBorder = new Border
            {
                Background = R("BgModal"),
                BorderBrush = R("AccentBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.6 }
            };

            var root = new StackPanel();

            var titleBar = new Border { Background = Brushes.Transparent, Padding = new Thickness(16, 10, 16, 10) };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            if (title == "KillerPDF")
            {
                var wmTb = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
                wmTb.Inlines.Add(new System.Windows.Documents.Run("Killer") { FontFamily = UiKit.WordmarkFont, FontWeight = FontWeights.Normal, FontSize = 15, Foreground = R("TextPrimary") });
                wmTb.Inlines.Add(new System.Windows.Documents.Run("PDF") { FontFamily = UiKit.WordmarkFontPdf, FontWeight = FontWeights.Bold, FontSize = 18, Foreground = R("AccentLogo") });
                titleBar.Child = wmTb;
            }
            else
            {
                titleBar.Child = new TextBlock { Text = title, Foreground = R("Accent"), FontWeight = FontWeights.Bold, FontSize = 14, FontFamily = UiKit.MonoFont };
            }
            root.Children.Add(titleBar);

            root.Children.Add(new Border
            {
                Padding = new Thickness(20, 16, 20, 8),
                Child = new TextBlock { Text = message, Foreground = R("TextPrimary"), FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 }
            });

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < labels.Length; i++)
            {
                int idx = i;
                var btn = UiKit.Make(labels[i], accent: i == accentIndex);
                btn.Padding = new Thickness(22, 8, 22, 8);
                btn.MinWidth = 96;
                btn.Margin = new Thickness(8, 0, 0, 0);
                btn.Click += (_, _2) => { result = idx; win.Close(); };
                btnPanel.Children.Add(btn);
            }
            root.Children.Add(new Border { Padding = new Thickness(16, 8, 16, 16), Child = btnPanel });

            var contentGrid = new Grid();
            var grain = (owner as MainWindow)?.GrainTexture;
            if (grain is not null)
            {
                double grainOpacity = Application.Current.Resources["GrainOpacity"] is double go ? go : 0.05;
                contentGrid.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Opacity = grainOpacity,
                    Background = new ImageBrush(grain)
                    {
                        TileMode = TileMode.Tile,
                        ViewportUnits = BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, 256, 256),
                        Stretch = Stretch.None
                    }
                });
            }
            contentGrid.Children.Add(root);
            outerBorder.Child = contentGrid;

            win.Content = outerBorder;
            win.ShowDialog();
            return result;
        }

        /// <summary>
        /// Like <see cref="Show"/> but with a "don't warn again" style checkbox between the message and the
        /// buttons. Returns the button result and the checkbox state.
        /// </summary>
        // Same dialog as Show(), plus a checkbox (e.g. "Remember my choice"). Delegates to the single
        // Show() implementation so there is one KillerDialog box, not a duplicate.
        public static (MessageBoxResult result, bool isChecked) ShowWithCheckbox(
            Window? owner,
            string message,
            string checkboxText,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OKCancel)
        {
            var result = Show(owner, message, title, buttons, checkboxText: checkboxText);
            return (result, _lastCheckboxChecked);
        }

    }
}
