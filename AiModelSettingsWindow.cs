using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace KillerPDF
{
    internal sealed class AiModelProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New model";
        public string Endpoint { get; set; } = "https://api.openai.com/v1";
        public string ModelName { get; set; } = "gpt-4.1-mini";
        public string ApiKey { get; set; } = "";
    }

    internal static class AiModelProfileStore
    {
        private const string SettingName = "AiModelProfiles";

        public static List<AiModelProfile> Load()
        {
            try
            {
                string? json = App.GetSetting(SettingName);
                return string.IsNullOrWhiteSpace(json)
                    ? new List<AiModelProfile>()
                    : JsonSerializer.Deserialize<List<AiModelProfile>>(json!) ?? new List<AiModelProfile>();
            }
            catch { return new List<AiModelProfile>(); }
        }

        public static void Save(List<AiModelProfile> profiles) =>
            App.SetSetting(SettingName, JsonSerializer.Serialize(profiles));
    }

    internal sealed class AiModelSettingsWindow : Window
    {
        private readonly ListBox _list = new ListBox { Width = 175, Margin = new Thickness(0, 0, 12, 0) };
        private readonly Wpf.Ui.Controls.TextBox _name = new Wpf.Ui.Controls.TextBox();
        private readonly Wpf.Ui.Controls.TextBox _endpoint = new Wpf.Ui.Controls.TextBox();
        private readonly Wpf.Ui.Controls.TextBox _model = new Wpf.Ui.Controls.TextBox();
        private readonly Wpf.Ui.Controls.PasswordBox _key = new Wpf.Ui.Controls.PasswordBox();
        private List<AiModelProfile> _profiles = [];
        private AiModelProfile? _current;

        public AiModelSettingsWindow()
        {
            Title = "Model configurations";
            Width = 620; Height = 410; MinWidth = 560; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = SystemColors.WindowBrush;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

            var root = new Grid { Margin = new Thickness(16) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var left = new DockPanel();
            var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 12, 0) };
            var add = new Wpf.Ui.Controls.Button { Content = "Add", Icon = new SymbolIcon(SymbolRegular.Add24), Appearance = ControlAppearance.Secondary, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 6, 0) };
            var remove = new Wpf.Ui.Controls.Button { Content = "Delete", Icon = new SymbolIcon(SymbolRegular.Delete24), Appearance = ControlAppearance.Secondary, Padding = new Thickness(14, 5, 14, 5) };
            add.Click += (_, _) => AddProfile(); remove.Click += (_, _) => DeleteProfile();
            leftButtons.Children.Add(add); leftButtons.Children.Add(remove);
            DockPanel.SetDock(leftButtons, Dock.Bottom); left.Children.Add(leftButtons); left.Children.Add(_list);
            root.Children.Add(left);

            var form = new Grid(); Grid.SetColumn(form, 1); root.Children.Add(form);
            for (int i = 0; i < 8; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddField(form, "Configuration name", _name, 0);
            AddField(form, "Endpoint", _endpoint, 2);
            AddField(form, "Model name", _model, 4);
            AddField(form, "API key", _key, 6);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2); root.Children.Add(footer);
            var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", Appearance = ControlAppearance.Secondary, IsCancel = true, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) };
            var save = new Wpf.Ui.Controls.Button { Content = "Save", Icon = new SymbolIcon(SymbolRegular.Save24), Appearance = ControlAppearance.Primary, IsDefault = true, Padding = new Thickness(22, 6, 22, 6) };
            save.Click += (_, _) => SaveAndClose(); footer.Children.Add(cancel); footer.Children.Add(save);
            Content = root;

            _list.SelectionChanged += (_, _) => SelectProfile(_list.SelectedItem as AiModelProfile);
            Loaded += (_, _) =>
            {
                ApplicationThemeManager.Apply(this);
                _profiles = AiModelProfileStore.Load(); RefreshList();
                if (_profiles.Count > 0) _list.SelectedIndex = 0; else AddProfile();
            };
        }

        private static void AddField(Grid grid, string label, Control input, int row)
        {
            grid.Children.Add(new System.Windows.Controls.TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 4) });
            // WPF UI text fields use a taller Fluent template than stock WPF controls. A 27px hard
            // height clips the text baseline; 38px leaves room for the template padding and DPI scaling.
            Grid.SetRow(input, row + 1); input.MinHeight = 38; grid.Children.Add(input);
            Grid.SetRow(grid.Children[grid.Children.Count - 2], row);
        }

        private void CommitCurrent()
        {
            if (_current is null) return;
            _current.Name = string.IsNullOrWhiteSpace(_name.Text) ? "Unnamed model" : _name.Text.Trim();
            _current.Endpoint = _endpoint.Text.Trim(); _current.ModelName = _model.Text.Trim(); _current.ApiKey = _key.Password;
        }

        private void SelectProfile(AiModelProfile? profile)
        {
            CommitCurrent(); _current = profile;
            bool enabled = profile is not null; _name.IsEnabled = _endpoint.IsEnabled = _model.IsEnabled = _key.IsEnabled = enabled;
            _name.Text = profile?.Name ?? ""; _endpoint.Text = profile?.Endpoint ?? ""; _model.Text = profile?.ModelName ?? ""; _key.Password = profile?.ApiKey ?? "";
        }

        private void AddProfile()
        {
            CommitCurrent(); var profile = new AiModelProfile(); _profiles.Add(profile); RefreshList(); _list.SelectedItem = profile; _name.Focus(); _name.SelectAll();
        }

        private void DeleteProfile()
        {
            if (_list.SelectedItem is not AiModelProfile profile) return;
            int index = _list.SelectedIndex; _profiles.Remove(profile); _current = null; RefreshList();
            if (_profiles.Count > 0) _list.SelectedIndex = Math.Min(index, _profiles.Count - 1); else SelectProfile(null);
        }

        private void RefreshList() { _list.ItemsSource = null; _list.ItemsSource = _profiles; _list.DisplayMemberPath = nameof(AiModelProfile.Name); }

        private void SaveAndClose()
        {
            CommitCurrent();
            foreach (var p in _profiles)
                if (string.IsNullOrWhiteSpace(p.Endpoint) || string.IsNullOrWhiteSpace(p.ModelName)) { System.Windows.MessageBox.Show(this, "Endpoint and model name are required."); return; }
            AiModelProfileStore.Save(_profiles); DialogResult = true;
        }
    }
}
