using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;

namespace KillerPDF
{
    internal sealed class AiConversationMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    internal sealed class AiConversationRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "Untitled conversation";
        public string DocumentPath { get; set; } = "";
        public string ModelProfileId { get; set; } = "";
        public string ModelName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<AiConversationMessage> Messages { get; set; } = [];
        public string DisplayTitle => $"{Title}\n{UpdatedAt:yyyy-MM-dd HH:mm}  ·  {Messages.Count} messages";
    }

    internal static class AiConversationStore
    {
        internal static readonly string StoragePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerPDF", "AiConversations.json");

        public static List<AiConversationRecord> Load()
        {
            try
            {
                if (!File.Exists(StoragePath)) return [];
                return JsonSerializer.Deserialize<List<AiConversationRecord>>(File.ReadAllText(StoragePath)) ?? [];
            }
            catch { return []; }
        }

        public static void Save(AiConversationRecord conversation)
        {
            var all = Load();
            int index = all.FindIndex(item => item.Id == conversation.Id);
            if (index >= 0) all[index] = conversation; else all.Add(conversation);
            all = all.OrderByDescending(item => item.UpdatedAt).Take(100).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
            string temp = StoragePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, StoragePath, true);
        }

        public static void Delete(string id)
        {
            var all = Load();
            all.RemoveAll(item => item.Id == id);
            if (all.Count == 0) { if (File.Exists(StoragePath)) File.Delete(StoragePath); return; }
            File.WriteAllText(StoragePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    internal sealed class AiConversationHistoryWindow : Window
    {
        private readonly ListBox _list = new() { MinWidth = 360 };
        private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        private readonly Wpf.Ui.Controls.Button _open = new() { Content = "Open conversation", Appearance = ControlAppearance.Primary, Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        private readonly Wpf.Ui.Controls.Button _delete = new() { Content = "Delete", Appearance = ControlAppearance.Secondary, Padding = new Thickness(14, 6, 14, 6) };
        private List<AiConversationRecord> _items = [];
        public AiConversationRecord? SelectedConversation { get; private set; }

        public AiConversationHistoryWindow()
        {
            Title = "AI conversation history";
            Width = 650; Height = 450; MinWidth = 540; MinHeight = 350;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = UiKit.UiFont;
            Background = SystemColors.WindowBrush;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

            _list.Margin = new Thickness(0, 0, 14, 0);
            _list.DisplayMemberPath = nameof(AiConversationRecord.DisplayTitle);
            _list.SelectionChanged += (_, _) => UpdateSelection();
            _list.MouseDoubleClick += (_, _) => OpenSelected();
            root.Children.Add(_list);

            var detailPanel = new StackPanel();
            Grid.SetColumn(detailPanel, 1);
            detailPanel.Children.Add(new TextBlock { Text = "Conversation details", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            detailPanel.Children.Add(_details);
            root.Children.Add(detailPanel);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 2);
            var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", Appearance = ControlAppearance.Secondary, Padding = new Thickness(14, 6, 14, 6), IsCancel = true };
            _delete.Margin = new Thickness(0, 0, 8, 0); cancel.Margin = new Thickness(0, 0, 8, 0);
            _delete.Click += (_, _) => DeleteSelected(); _open.Click += (_, _) => OpenSelected();
            footer.Children.Add(_delete); footer.Children.Add(cancel); footer.Children.Add(_open);
            root.Children.Add(footer);
            Content = root;

            Loaded += (_, _) => { ApplicationThemeManager.Apply(this); Reload(); };
        }

        private void Reload()
        {
            _items = AiConversationStore.Load().OrderByDescending(item => item.UpdatedAt).ToList();
            _list.ItemsSource = null; _list.ItemsSource = _items;
            if (_items.Count > 0) _list.SelectedIndex = 0; else UpdateSelection();
        }

        private void UpdateSelection()
        {
            var item = _list.SelectedItem as AiConversationRecord;
            _open.IsEnabled = _delete.IsEnabled = item is not null;
            _details.Text = item is null
                ? "No saved conversations."
                : $"Document\n{(string.IsNullOrWhiteSpace(item.DocumentPath) ? "Not recorded" : item.DocumentPath)}\n\nModel\n{item.ModelName}\n\nUpdated\n{item.UpdatedAt:yyyy-MM-dd HH:mm:ss}";
        }

        private void OpenSelected()
        {
            if (_list.SelectedItem is not AiConversationRecord item) return;
            SelectedConversation = item; DialogResult = true;
        }

        private void DeleteSelected()
        {
            if (_list.SelectedItem is not AiConversationRecord item) return;
            AiConversationStore.Delete(item.Id); Reload();
        }
    }
}
