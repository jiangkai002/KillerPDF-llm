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

#pragma warning disable IDE0060 // image intentionally kept for API parity with MessageBox; not yet rendered
        public static MessageBoxResult Show(
            Window? owner,
            string message,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage image = MessageBoxImage.None,
            bool fadeClose = true)
#pragma warning restore IDE0060
        {
            var result = MessageBoxResult.OK;

            var win = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };
            if (fadeClose) WindowFx.EnableFadeClose(win);
            // AllowsTransparency windows can't use ClearType. Display mode pixel-snaps the (unscaled)
            // dialog text so it stays crisp, and Grayscale gives smooth anti-aliased edges - the best
            // combination available on a layered window.
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(win, TextRenderingMode.Grayscale);

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
                wm.Children.Add(new TextBlock { Text = "Killer", FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"), FontWeight = FontWeights.Bold, FontSize = 15.5, Foreground = R("TextPrimary") });
                wm.Children.Add(new TextBlock { Text = "PDF", FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"), FontWeight = FontWeights.Bold, FontSize = 15.5, Foreground = R("AccentLogo") });
                wm.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.6 };
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
                    FontFamily = new System.Windows.Media.FontFamily("Consolas")
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
                // Shared themed button (see UiButtons) so this dialog matches the print dialog et al.
                var btn = UiButtons.Make(label, accent);
                btn.Margin = new Thickness(8, 0, 0, 0);
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
            return result;
        }

        /// <summary>
        /// Like <see cref="Show"/> but with a "don't warn again" style checkbox between the message and the
        /// buttons. Returns the button result and the checkbox state.
        /// </summary>
        public static (MessageBoxResult result, bool isChecked) ShowWithCheckbox(
            Window? owner,
            string message,
            string checkboxText,
            string title = "KillerPDF",
            MessageBoxButton buttons = MessageBoxButton.OKCancel)
        {
            var result = MessageBoxResult.Cancel;
            bool boxChecked = false;

            var win = new Window
            {
                Title = title,
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize
            };
            WindowFx.EnableFadeClose(win);
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(win, TextRenderingMode.Grayscale);

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
            titleBar.Child = new TextBlock
            {
                Text = title,
                Foreground = R("Accent"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                FontFamily = new FontFamily("Consolas")
            };
            root.Children.Add(titleBar);

            root.Children.Add(new Border
            {
                Padding = new Thickness(20, 4, 20, 8),
                Child = new TextBlock { Text = message, Foreground = R("TextPrimary"), FontSize = 13, TextWrapping = TextWrapping.Wrap }
            });

            var chk = new CheckBox
            {
                Content = checkboxText,
                Foreground = R("TextSecondary"),
                FontSize = 12,
                Margin = new Thickness(20, 2, 20, 4),
                Cursor = Cursors.Hand,
                VerticalContentAlignment = VerticalAlignment.Center,
                Template = ThemedCheckTemplate()
            };
            chk.Checked += (_, _2) => boxChecked = true;
            chk.Unchecked += (_, _2) => boxChecked = false;
            root.Children.Add(chk);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button MakeBtn(string label, MessageBoxResult res, bool accent = false)
            {
                var btn = UiButtons.Make(label, accent);
                btn.Margin = new Thickness(8, 0, 0, 0);
                btn.Click += (_, _2) => { result = res; win.Close(); };
                return btn;
            }
            if (buttons == MessageBoxButton.YesNo)
            {
                btnPanel.Children.Add(MakeBtn("Yes", MessageBoxResult.Yes, accent: true));
                btnPanel.Children.Add(MakeBtn("No", MessageBoxResult.No));
            }
            else
            {
                btnPanel.Children.Add(MakeBtn("OK", MessageBoxResult.OK, accent: true));
                btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
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
            return (result, boxChecked);
        }

        // Themed checkbox chrome (no XAML CheckBox style exists): a small rounded box with an accent
        // checkmark shown when checked, matching the rest of the app instead of the OS default chrome.
        private static ControlTemplate ThemedCheckTemplate()
        {
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            var box = new FrameworkElementFactory(typeof(Border));
            box.SetValue(Border.WidthProperty, 16.0);
            box.SetValue(Border.HeightProperty, 16.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetValue(Border.BorderBrushProperty, R("BorderDim"));
            box.SetValue(Border.BackgroundProperty, R("BgCanvas"));
            box.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetValue(Border.MarginProperty, new Thickness(0, 0, 8, 0));

            var check = new FrameworkElementFactory(typeof(TextBlock)) { Name = "chk" };
            check.SetValue(TextBlock.TextProperty, "✓");   // check mark
            check.SetValue(TextBlock.FontSizeProperty, 11.0);
            check.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            check.SetValue(TextBlock.ForegroundProperty, R("Accent"));
            check.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(check);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            row.AppendChild(box);
            row.AppendChild(content);

            var ct = new ControlTemplate(typeof(CheckBox)) { VisualTree = row };
            var trig = new Trigger { Property = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, Value = true };
            trig.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible) { TargetName = "chk" });
            ct.Triggers.Add(trig);
            return ct;
        }
    }
}
