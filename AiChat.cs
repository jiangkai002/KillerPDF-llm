using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using KillerPDF.Services;
using MarkdView.Controls;
using MarkdView.Enums;

namespace KillerPDF
{
    public partial class MainWindow
    {
        private readonly List<(string role, string content)> _aiHistory = [];
        private string? _aiCapturedText;
        private bool _aiCaptureMode;
        private bool _aiSending;
        private List<AiModelProfile> _aiProfiles = [];

        private void AiChat_Click(object sender, RoutedEventArgs e)
        {
            AiChatPanel.Visibility = AiChatPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (AiChatPanel.Visibility != Visibility.Visible) return;
            ReloadAiProfiles();
            if (AiMessagesPanel.Children.Count == 0)
                AddAiBubble("assistant", "Hi — ask me about this PDF, or use Screenshot & OCR to select a region first.");
            AiPromptBox.Focus();
        }

        private void AiChatClose_Click(object sender, RoutedEventArgs e) => AiChatPanel.Visibility = Visibility.Collapsed;

        private void ReloadAiProfiles()
        {
            _aiProfiles = AiModelProfileStore.Load();
            string selected = App.GetSetting("AiSelectedProfile") ?? "";
            AiModelPicker.ItemsSource = null;
            AiModelPicker.ItemsSource = _aiProfiles;
            AiModelPicker.DisplayMemberPath = nameof(AiModelProfile.Name);
            int index = _aiProfiles.FindIndex(p => p.Id == selected);
            AiModelPicker.SelectedIndex = index >= 0 ? index : (_aiProfiles.Count > 0 ? 0 : -1);
        }

        private void AiModelSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AiModelSettingsWindow { Owner = this };
            dialog.ShowDialog();
            ReloadAiProfiles();
        }

        private void AiModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AiModelPicker.SelectedItem is AiModelProfile profile)
                App.SetSetting("AiSelectedProfile", profile.Id);
        }

        private void AiCapture_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            AiChatPanel.Visibility = Visibility.Collapsed;
            _aiCaptureMode = true;
            BeginOcrRegion();
            SetStatus("Drag over PDF content to screenshot and ask AI");
        }

        private void SetAiCapture(byte[] bgra, int width, int height, string text)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            bitmap.Freeze();
            AiCaptureImage.Source = bitmap;
            _aiCapturedText = text.Trim();
            AiCaptureText.Text = string.IsNullOrWhiteSpace(_aiCapturedText) ? "No text recognized" : _aiCapturedText;
            AiCaptureCard.Visibility = Visibility.Visible;
            AiChatPanel.Visibility = Visibility.Visible;
            AiPromptBox.Focus();
            SetStatus("Screenshot OCR ready — type your question");
        }

        private void AiClearCapture_Click(object sender, RoutedEventArgs e)
        {
            _aiCapturedText = null;
            AiCaptureImage.Source = null;
            AiCaptureCard.Visibility = Visibility.Collapsed;
        }

        private void AiPromptBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = SendAiAsync();
            }
        }

        private void AiSend_Click(object sender, RoutedEventArgs e) => _ = SendAiAsync();

        private async void AiSaveNote_Click(object sender, RoutedEventArgs e)
        {
            if (_aiHistory.Count == 0)
            {
                KillerDialog.Show(this, "There is no conversation to save yet.");
                return;
            }
            if (_aiSending) return;
            if (AiModelPicker.SelectedItem is not AiModelProfile profile)
            {
                KillerDialog.Show(this, "Add and select a model configuration first.");
                return;
            }

            string documentName = Path.GetFileNameWithoutExtension(_originalFile ?? _currentFile ?? "PDF conversation");
            var dialog = new SaveFileDialog
            {
                Title = "Save AI conversation note",
                Filter = "Markdown note|*.md",
                DefaultExt = ".md",
                AddExtension = true,
                FileName = SanitizeNoteFileName(documentName + " - AI notes") + ".md"
            };
            if (dialog.ShowDialog(this) != true) return;

            _aiSending = true;
            AiSendBtn.IsEnabled = false;
            AiSaveNoteBtn.IsEnabled = false;
            SetStatus("AI is summarizing the conversation into a Markdown note...");
            try
            {
                string generatedNote = await GenerateMarkdownNoteAsync(profile, documentName);
                var markdown = new StringBuilder();
                markdown.AppendLine($"> Generated by KillerPDF on {DateTime.Now:yyyy-MM-dd HH:mm}");
                markdown.AppendLine($"> Model: {profile.Name} (`{profile.ModelName}`)");
                if (!string.IsNullOrWhiteSpace(_originalFile ?? _currentFile))
                    markdown.AppendLine($"> Source: `{(_originalFile ?? _currentFile)!.Replace("`", "\\`")}`");
                markdown.AppendLine();
                markdown.AppendLine(StripOuterMarkdownFence(generatedNote));

                File.WriteAllText(dialog.FileName, markdown.ToString(), new UTF8Encoding(true));
                SetStatus("Markdown note saved: " + dialog.FileName);
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, "Could not save the Markdown note:\n" + ex.Message,
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _aiSending = false;
                AiSendBtn.IsEnabled = true;
                AiSaveNoteBtn.IsEnabled = true;
            }
        }

        private async Task<string> GenerateMarkdownNoteAsync(AiModelProfile profile, string documentName)
        {
            var messages = new List<object>
            {
                new
                {
                    role = "system",
                    content = "You are a professional note-taking assistant. Turn the supplied PDF-related conversation into a self-contained, well-structured Markdown note. Summarize and reorganize the useful knowledge instead of transcribing the dialogue. Remove conversational filler and repeated content; preserve important facts, reasoning, code, formulas, caveats, and conclusions. Use clear headings and lists. Write in the primary language used by the user. Return Markdown only and do not wrap the whole response in a code fence."
                }
            };
            foreach (var message in _aiHistory)
                messages.Add(new { role = message.role, content = message.content });
            messages.Add(new
            {
                role = "user",
                content = $"Now create the final Markdown study note for the document titled: {documentName}. Do not reproduce the conversation question-by-question."
            });

            string endpoint = profile.Endpoint.Trim().TrimEnd('/');
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", profile.ApiKey);
            string json = JsonSerializer.Serialize(new
            {
                model = profile.ModelName.Trim(),
                messages,
                stream = false
            });
            using var response = await client.PostAsync(endpoint + "/chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"));
            string body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"API {(int)response.StatusCode}: {ReadApiError(body)}");

            using var result = JsonDocument.Parse(body);
            string? note = result.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(note))
                throw new InvalidOperationException("The model returned an empty note.");
            return note;
        }

        private static string StripOuterMarkdownFence(string markdown)
        {
            string value = markdown.Trim();
            if (!(value.StartsWith("```markdown", StringComparison.OrdinalIgnoreCase) ||
                  value.StartsWith("```md", StringComparison.OrdinalIgnoreCase))) return value;

            int firstLineEnd = value.IndexOf('\n');
            if (firstLineEnd < 0) return value;
            value = value.Substring(firstLineEnd + 1).TrimEnd();
            if (value.EndsWith("```", StringComparison.Ordinal))
                value = value.Substring(0, value.Length - 3).TrimEnd();
            return value;
        }

        private static string SanitizeNoteFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private async Task SendAiAsync()
        {
            if (_aiSending) return;
            string prompt = AiPromptBox.Text.Trim();
            if (prompt.Length == 0) return;
            if (AiModelPicker.SelectedItem is not AiModelProfile profile)
            {
                KillerDialog.Show(this, "Add and select a model configuration first.");
                return;
            }
            string key = profile.ApiKey;
            string endpoint = profile.Endpoint.Trim().TrimEnd('/');
            string model = profile.ModelName.Trim();
            string content = prompt;
            if (!string.IsNullOrWhiteSpace(_aiCapturedText))
                content += "\n\nThe following text was OCR-recognized from the user's selected PDF screenshot. Use it as document context:\n---\n" + _aiCapturedText + "\n---";
            _aiHistory.Add(("user", content));
            AddAiBubble("user", prompt + (!string.IsNullOrWhiteSpace(_aiCapturedText) ? "\n[PDF screenshot attached]" : ""));
            AiPromptBox.Clear();
            AiClearCapture_Click(this, new RoutedEventArgs());
            _aiSending = true;
            AiSendBtn.IsEnabled = false;
            SetStatus("AI is thinking...");
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                var messages = new List<object> { new { role = "system", content = "You are a concise PDF reading assistant. Answer from supplied document context when available, and clearly say when the context is insufficient. Format responses as Markdown, but never wrap the entire response in a ```markdown or ```md code fence. Use fenced code blocks only for actual source code." } };
                foreach (var m in _aiHistory) messages.Add(new { role = m.role, content = m.content });
                string json = JsonSerializer.Serialize(new { model, messages, stream = true });
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "/chat/completions")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    throw new InvalidOperationException($"API {(int)response.StatusCode}: {ReadApiError(body)}");
                }

                var answerBuilder = new StringBuilder();
                MarkdownViewer answerBlock = AddAiBubble("assistant", "");
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(stream))
                {
                    while (!reader.EndOfStream)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                        string data = line.Substring(5).Trim();
                        if (data == "[DONE]") break;
                        try
                        {
                            using var chunk = JsonDocument.Parse(data);
                            var choices = chunk.RootElement.GetProperty("choices");
                            if (choices.GetArrayLength() == 0) continue;
                            var delta = choices[0].GetProperty("delta");
                            if (!delta.TryGetProperty("content", out var contentPart) || contentPart.ValueKind != JsonValueKind.String) continue;
                            string token = contentPart.GetString() ?? "";
                            if (token.Length == 0) continue;
                            answerBuilder.Append(token);
                            answerBlock.Content = PrepareMarkdownForDisplay(answerBuilder.ToString());
                            AiMessagesScroll.ScrollToEnd();
                            // A buffered network stream can complete many ReadLineAsync calls synchronously.
                            // Yield briefly so WPF gets a render frame between chunks instead of painting only
                            // after the entire SSE response has already been consumed.
                            await Task.Delay(12);
                        }
                        catch (JsonException) { /* ignore keep-alive or provider-specific SSE metadata */ }
                    }
                }
                string answer = answerBuilder.Length > 0 ? answerBuilder.ToString() : "(Empty response)";
                answerBlock.Content = PrepareMarkdownForDisplay(answer);
                _aiHistory.Add(("assistant", answer));
                SetStatus("AI response received");
            }
            catch (Exception ex)
            {
                AddAiBubble("error", "Request failed: " + ex.Message);
                SetStatus("AI request failed");
            }
            finally { _aiSending = false; AiSendBtn.IsEnabled = true; }
        }

        private static string ReadApiError(string body)
        {
            try { using var d = JsonDocument.Parse(body); return d.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body; }
            catch { return body.Length > 300 ? body.Substring(0, 300) : body; }
        }

        private MarkdownViewer AddAiBubble(string role, string text)
        {
            bool user = role == "user";
            var markdownView = new MarkdownViewer
            {
                MaxWidth = 280,
                Content = PrepareMarkdownForDisplay(text),
                Theme = ThemeMode.Dark,
                EnableStreaming = true,
                StreamingThrottle = 50,
                EnableSyntaxHighlighting = true,
                UseTransparentCanvas = true,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            markdownView.RenderCompleted += (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => ConstrainRenderedMathImages(markdownView)));
            var bubble = new Border
            {
                Background = user ? (Brush)(TryFindResource("SelectionBg") ?? Brushes.DimGray) : (Brush)(TryFindResource("BgPanel") ?? Brushes.DimGray),
                BorderBrush = role == "error" ? Brushes.IndianRed : (Brush)(TryFindResource("BorderDim") ?? Brushes.Gray),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10),
                Margin = new Thickness(user ? 38 : 0, 0, user ? 0 : 24, 8), HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = markdownView
            };
            AiMessagesPanel.Children.Add(bubble);
            Dispatcher.BeginInvoke(new Action(() => AiMessagesScroll.ScrollToEnd()));
            return markdownView;
        }

        // MarkdView treats every Markdown image as a general-purpose content image. Inside its
        // FlowDocument an InlineUIContainer can consequently arrange a tiny formula across the
        // available line width. Restore the bitmap's DPI-aware natural size after each render.
        private static void ConstrainRenderedMathImages(DependencyObject root)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.Image image &&
                    image.Tag is string sourceUrl && IsRenderedMathImage(sourceUrl) &&
                    image.Source is BitmapSource bitmap)
                {
                    double dpiX = bitmap.DpiX > 0 ? bitmap.DpiX : 96;
                    double dpiY = bitmap.DpiY > 0 ? bitmap.DpiY : 96;
                    double naturalWidth = bitmap.PixelWidth * 96d / dpiX;
                    double naturalHeight = bitmap.PixelHeight * 96d / dpiY;
                    double maxWidth = 250;
                    double fit = naturalWidth > maxWidth ? maxWidth / naturalWidth : 1;

                    image.Width = naturalWidth * fit;
                    image.Height = naturalHeight * fit;
                    image.MaxWidth = maxWidth;
                    image.Stretch = Stretch.Uniform;
                    image.Margin = new Thickness(0, 2, 0, 2);
                }
                ConstrainRenderedMathImages(child);
            }
        }

        private static bool IsRenderedMathImage(string sourceUrl)
        {
            if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri) || !uri.IsFile) return false;
            string mathDirectory = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KillerPDF", "MarkdownMath"));
            string imageDirectory = Path.GetFullPath(Path.GetDirectoryName(uri.LocalPath) ?? "");
            return string.Equals(imageDirectory, mathDirectory, StringComparison.OrdinalIgnoreCase);
        }

        // Some models wrap the whole answer in ```markdown ... ```. A renderer correctly treats that
        // as source code, so remove only this explicit document-level wrapper before handing text to
        // MarkdView. Real fenced code blocks inside the answer are left untouched.
        private static string PrepareMarkdownForDisplay(string markdown)
        {
            string value = (markdown ?? "").TrimStart();
            string? fence = null;
            if (value.StartsWith("```markdown", StringComparison.OrdinalIgnoreCase)) fence = "```";
            else if (value.StartsWith("```md", StringComparison.OrdinalIgnoreCase)) fence = "```";
            else if (value.StartsWith("~~~markdown", StringComparison.OrdinalIgnoreCase)) fence = "~~~";
            else if (value.StartsWith("~~~md", StringComparison.OrdinalIgnoreCase)) fence = "~~~";
            if (fence is null) return MarkdownMathPreprocessor.Process(markdown ?? "");

            int firstLineEnd = value.IndexOf('\n');
            if (firstLineEnd < 0) return "";
            value = value.Substring(firstLineEnd + 1);
            string trimmed = value.TrimEnd();
            if (trimmed.EndsWith(fence, StringComparison.Ordinal))
            {
                int closing = trimmed.LastIndexOf(fence, StringComparison.Ordinal);
                if (closing == 0 || trimmed[closing - 1] == '\n')
                    trimmed = trimmed.Substring(0, closing).TrimEnd();
            }
            return MarkdownMathPreprocessor.Process(trimmed);
        }
    }
}
