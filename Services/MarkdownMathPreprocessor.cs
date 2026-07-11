using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;

namespace KillerPDF.Services
{
    /// <summary>
    /// Adds the math backend MarkdView does not provide. LaTeX spans are rendered locally with WpfMath
    /// to transparent PNGs and replaced with ordinary Markdown images, which MarkdView already handles.
    /// Rendered formulas are content-addressed and reused throughout the conversation.
    /// </summary>
    internal static class MarkdownMathPreprocessor
    {
        // Included in the cache key so changes to the visual metrics do not reuse older,
        // oversized formula bitmaps already stored on the machine.
        private const string RenderVersion = "v2-compact";
        private const double InlineScale = 9;
        private const double DisplayScale = 11;
        private static readonly Regex CodeFence = new("(```[\\s\\S]*?```|~~~[\\s\\S]*?~~~)", RegexOptions.Compiled);
        private static readonly Regex DollarBlock = new(@"\$\$([\s\S]+?)\$\$", RegexOptions.Compiled);
        private static readonly Regex BracketBlock = new(@"\\\[([\s\S]+?)\\\]", RegexOptions.Compiled);
        private static readonly Regex DollarInline = new(@"(?<!\\)(?<!\$)\$(?!\$)([^\r\n$]+?)(?<!\\)\$(?!\$)", RegexOptions.Compiled);
        private static readonly Regex ParenInline = new(@"\\\(([^\r\n]+?)\\\)", RegexOptions.Compiled);
        private static readonly object RenderLock = new();

        public static string Process(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return markdown;
            var parts = CodeFence.Split(markdown);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 1) continue; // never interpret source code as LaTeX
                string part = DollarBlock.Replace(parts[i], m => FormulaImage(m, m.Groups[1].Value, display: true));
                part = BracketBlock.Replace(part, m => FormulaImage(m, m.Groups[1].Value, display: true));
                part = DollarInline.Replace(part, m => FormulaImage(m, m.Groups[1].Value, display: false));
                part = ParenInline.Replace(part, m => FormulaImage(m, m.Groups[1].Value, display: false));
                parts[i] = part;
            }
            return string.Concat(parts);
        }

        private static string FormulaImage(Match original, string latex, bool display)
        {
            latex = latex.Trim();
            if (latex.Length == 0) return original.Value;
            try
            {
                string path = RenderFormula(latex, display);
                string uri = new Uri(path).AbsoluteUri;
                return display
                    ? $"\n\n![{EscapeAlt(latex)}]({uri})\n\n"
                    : $"![{EscapeAlt(latex)}]({uri})";
            }
            catch
            {
                return original.Value; // keep the LaTeX visible if a command is unsupported
            }
        }

        private static string RenderFormula(string latex, bool display)
        {
            string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KillerPDF", "MarkdownMath");
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                RenderVersion + (display ? ":D:" : ":I:") + latex)));
            string path = Path.Combine(cacheDir, key + ".png");
            if (File.Exists(path)) return path;

            lock (RenderLock)
            {
                if (File.Exists(path)) return path;
                Directory.CreateDirectory(cacheDir);
                TexFormula formula = WpfTeXFormulaParser.Instance.Parse(latex);
                // WpfMath's scale is substantially larger than a WPF text point size when the
                // result is displayed by MarkdView as an image. Keep inline math close to the
                // 13px chat text and give display equations only a modest emphasis.
                double scale = display ? DisplayScale : InlineScale;
                var environment = WpfTeXEnvironment.Create(
                    style: display ? TexStyle.Display : TexStyle.Text,
                    scale: scale,
                    systemTextFontName: "Microsoft YaHei UI",
                    foreground: Brushes.White,
                    background: Brushes.Transparent);
                BitmapSource bitmap = formula.RenderToBitmap(environment, scale, dpi: 144);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string temp = path + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    encoder.Save(stream);
                File.Move(temp, path, true);
                return path;
            }
        }

        private static string EscapeAlt(string text) => text.Replace("[", "\\[").Replace("]", "\\]");
    }
}
