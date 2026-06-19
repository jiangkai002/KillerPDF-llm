using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF
{
    /// <summary>
    /// Small themed dialog that collects page-number stamping options: starting number,
    /// format string ({n} = number, {N} = total), position, and font size.
    /// </summary>
    internal sealed class StampNumbersDialog : Window
    {
        private TextBox _startBox  = null!;
        private TextBox _formatBox = null!;
        private TextBox _sizeBox   = null!;
        private ComboBox _posCombo = null!;

        // (label, horizontal 0/1/2, vertical 0=top/2=bottom)
        // (resource key, horizontal 0/1/2, vertical 0=top/2=bottom)
        private static readonly (string key, int h, int v)[] Positions =
        [
            ("Str_Pos_BottomCenter", 1, 2), ("Str_Pos_BottomRight", 2, 2), ("Str_Pos_BottomLeft", 0, 2),
            ("Str_Pos_TopCenter", 1, 0), ("Str_Pos_TopRight", 2, 0), ("Str_Pos_TopLeft", 0, 0)
        ];

        public int StartNumber { get; private set; } = 1;
        public string Format    { get; private set; } = "{n}";
        public double FontSizePt { get; private set; } = 12;
        public int PosH { get; private set; } = 1;
        public int PosV { get; private set; } = 2;

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];
        private static string S(string key) => Application.Current.TryFindResource(key) as string ?? key;
        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        public StampNumbersDialog(Window? owner)
        {
            Title  = "KillerPDF - Page Numbers";
            Width  = 340;
            SizeToContent = SizeToContent.Height;
            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            Background            = Brushes.Transparent;
            ResizeMode            = ResizeMode.NoResize;
            Owner                 = owner;
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            UseLayoutRounding = true;

            BuildUi();
        }

        private void BuildUi()
        {
            var card = new Border
            {
                Background      = R("BgModal"),
                BorderBrush     = R("AccentBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(14),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.55
                }
            };
            var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
            card.Child = panel;
            Content = card;

            var title = new TextBlock
            {
                Text = S("Str_Stamp_Title"),
                Foreground = R("TextPrimary"),
                FontSize = 14, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 14)
            };
            title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            panel.Children.Add(title);

            panel.Children.Add(Label(S("Str_Stamp_StartAt")));
            _startBox = Field("1");
            panel.Children.Add(_startBox);

            panel.Children.Add(Label(S("Str_Stamp_Format")));
            _formatBox = Field("{n}");
            panel.Children.Add(_formatBox);
            panel.Children.Add(new TextBlock
            {
                Text = S("Str_Stamp_Hint"),
                Foreground = R("TextSecondary"), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            panel.Children.Add(Label(S("Str_Stamp_Position")));
            _posCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 10), Height = 26 };
            ApplyComboStyle(_posCombo);
            foreach (var (key, _, _) in Positions) _posCombo.Items.Add(S(key));
            _posCombo.SelectedIndex = 0;
            panel.Children.Add(_posCombo);

            panel.Children.Add(Label(S("Str_Stamp_FontSize")));
            _sizeBox = Field("12");
            panel.Children.Add(_sizeBox);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var cancel = MakeButton(S("Str_Stamp_Cancel"), false);
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            var ok = MakeButton(S("Str_Stamp_Stamp"), true);
            ok.Margin = new Thickness(8, 0, 0, 0);
            ok.Click += (_, _) => Accept();
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(ok);
            panel.Children.Add(btnRow);
        }

        private void Accept()
        {
            if (!int.TryParse(_startBox.Text?.Trim(), out int start)) start = 1;
            StartNumber = start;

            string fmt = _formatBox.Text ?? "";
            Format = string.IsNullOrWhiteSpace(fmt) ? "{n}" : fmt;

            if (double.TryParse(_sizeBox.Text?.Trim(), out double sz) && sz > 0) FontSizePt = sz;

            int i = _posCombo.SelectedIndex;
            if (i >= 0 && i < Positions.Length) { PosH = Positions[i].h; PosV = Positions[i].v; }

            DialogResult = true;
            Close();
        }

        private TextBlock Label(string text) => new()
        {
            Text = text, Foreground = R("TextPrimary"),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        };

        private TextBox Field(string text) => new()
        {
            Text = text,
            Margin = new Thickness(0, 0, 0, 10),
            Background = R("BgCanvas"), Foreground = R("TextPrimary"),
            BorderBrush = R("BorderDim"), BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            CaretBrush = R("TextPrimary"),
            SelectionBrush = R("AccentDim"),
            SelectionTextBrush = R("TextPrimary"),
            Template = MakeTextBoxTemplate()
        };

        // Themed TextBox template so the OS default blue focus border / selection chrome doesn't show.
        private static ControlTemplate MakeTextBoxTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            return new ControlTemplate(typeof(TextBox)) { VisualTree = b };
        }

        private void ApplyComboStyle(ComboBox combo)
        {
            if (FindOwnerStyle("DarkComboBox") is Style s) combo.Style = s;
            else { combo.Foreground = R("TextPrimary"); combo.BorderBrush = R("BorderDim"); }
            combo.Background = R("BgCanvas");
        }

        private static ControlTemplate MakeBtnTemplate()
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

        private static Button MakeButton(string label, bool accent) => new()
        {
            Content         = label,
            Padding         = new Thickness(18, 6, 18, 6),
            Background      = accent ? R("AccentDim") : R("BgPanel"),
            Foreground      = accent ? R("Accent") : R("TextPrimary"),
            BorderBrush     = accent ? R("Accent") : R("BorderDim"),
            BorderThickness = new Thickness(1),
            Cursor          = Cursors.Hand,
            Template        = MakeBtnTemplate()
        };
    }
}
