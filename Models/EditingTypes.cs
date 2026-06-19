using System;
using System.Windows;
using System.Windows.Media;

namespace KillerPDF
{
    public enum EditTool { Select, Text, Highlight, Strikethrough, Underline, Draw, Signature, Image, Crop }

    /// <summary>How a HighlightAnnotation paints over its bounds.</summary>
    public enum HighlightStyle { Fill, Strikethrough, Underline }

    public abstract class PageAnnotation
    {
        public int PageIndex { get; set; }
    }

    /// <summary>
    /// Base class for placed/resizable annotations (signature, image).
    /// Carries the shared position, scale, and source-dimension properties used by the resize handle.
    /// </summary>
    public abstract class PlacedAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public double Scale { get; set; } = 0.5;
        public double SourceWidth { get; set; } = 400;
        public double SourceHeight { get; set; } = 150;
    }

    public class TextAnnotation : PageAnnotation
    {
        public Point Position { get; set; }
        public string Content { get; set; } = "";
        public double FontSize { get; set; } = 14;
        public byte ColorR { get; set; } = 0;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        // Box geometry. Width is fixed (text wraps to it); Height auto-grows to fit the wrapped text.
        public double Width { get; set; } = 200;
        public double Height { get; set; } = 28;

        // Optional background fill (the "whiteout"/highlight behind the text). BgA == 0 means no fill.
        public byte BgR { get; set; } = 255;
        public byte BgG { get; set; } = 255;
        public byte BgB { get; set; } = 255;
        public byte BgA { get; set; } = 0;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        public Color GetFill() => Color.FromArgb(BgA, BgR, BgG, BgB);
        public void SetFill(Color c) { BgR = c.R; BgG = c.G; BgB = c.B; BgA = c.A; }
        public bool HasFill => BgA > 0;
    }

    public class InkAnnotation : PageAnnotation
    {
        public List<Point> Points { get; set; } = [];
        public double StrokeWidth { get; set; } = 2;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 0;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 255;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }
    }

    public class HighlightAnnotation : PageAnnotation
    {
        public Rect Bounds { get; set; }
        public HighlightStyle Style { get; set; } = HighlightStyle.Fill;
        public byte ColorR { get; set; } = 255;
        public byte ColorG { get; set; } = 255;
        public byte ColorB { get; set; } = 0;
        public byte ColorA { get; set; } = 80;

        public Color GetColor() => Color.FromArgb(ColorA, ColorR, ColorG, ColorB);
        public void SetColor(Color c) { ColorR = c.R; ColorG = c.G; ColorB = c.B; ColorA = c.A; }

        /// <summary>
        /// The actual rectangle painted for this annotation. Fill uses the whole bounds;
        /// strikethrough is a thin band at the vertical centre; underline sits at the bottom.
        /// </summary>
        public Rect DrawRect()
        {
            double t = Math.Max(2.0, Bounds.Height * 0.10);
            switch (Style)
            {
                case HighlightStyle.Strikethrough:
                    return new Rect(Bounds.X, Bounds.Y + Bounds.Height / 2 - t / 2, Bounds.Width, t);
                case HighlightStyle.Underline:
                    return new Rect(Bounds.X, Bounds.Y + Bounds.Height - t, Bounds.Width, t);
                default:
                    return Bounds;
            }
        }
    }

    /// <summary>
    /// Represents an edit to existing PDF text: whites out original bounds, draws replacement.
    /// </summary>
    public class TextEditAnnotation : PageAnnotation
    {
        public Rect OriginalBounds { get; set; }
        public Point Position { get; set; }
        public string NewContent { get; set; } = "";
        public string OriginalContent { get; set; } = "";
        public double FontSize { get; set; } = 14;
        public string FontName { get; set; } = "Segoe UI";
    }

    /// <summary>
    /// A signature placed on a PDF page: either ink strokes or an imported image.
    /// </summary>
    public class SignatureAnnotation : PlacedAnnotation
    {
        public List<List<Point>> Strokes { get; set; } = [];
        /// <summary>Base-64 encoded PNG. Non-null = image sig; null = drawn strokes.</summary>
        public string? ImageData { get; set; }
    }

    /// <summary>
    /// An image placed on a PDF page as a resizable annotation.
    /// </summary>
    public class ImageAnnotation : PlacedAnnotation
    {
        /// <summary>Base-64 encoded image bytes (PNG, JPG, BMP, etc.).</summary>
        public string ImageData { get; set; } = "";
    }

    /// <summary>
    /// A point that can be serialized to JSON (WPF Point doesn't serialize well).
    /// </summary>
    public class SerializablePoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>
    /// A saved signature stored in the user's AppData for reuse.
    /// </summary>
    public class SavedSignature
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Signature";
        public List<List<SerializablePoint>> Strokes { get; set; } = [];
        public double CanvasWidth { get; set; } = 400;
        public double CanvasHeight { get; set; } = 150;
        /// <summary>Base-64 encoded PNG for imported image signatures. Null = drawn strokes.</summary>
        public string? ImageData { get; set; }
    }
}
