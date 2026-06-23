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
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Annotation management
        // ============================================================

        // Stamps a page number onto every page as a text annotation (so it renders, saves, and
        // flattens like any other annotation). One undo removes the whole batch.
        private void StampPageNumbers()
        {
            if (_doc is null) { SetStatus("Open a document first"); return; }

            var dlg = new StampNumbersDialog(this);
            if (dlg.ShowDialog() != true) return;

            int start = dlg.StartNumber;
            string fmt = dlg.Format;
            double ptSize = dlg.FontSizePt;
            int posH = dlg.PosH;   // 0 left, 1 center, 2 right
            int posV = dlg.PosV;   // 0 top, 2 bottom
            int n = _doc.PageCount;
            bool wasDirty = _isDirty;
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            var stamped = new List<int>();
            for (int i = 0; i < n; i++)
            {
                double pw = _doc.Pages[i].Width.Point;
                double phpt = _doc.Pages[i].Height.Point;
                if (_pageRotations.TryGetValue(i, out int rot) && (rot == 90 || rot == 270))
                    (pw, phpt) = (phpt, pw);
                double maxDim = Math.Max(1, Math.Max(pw, phpt));
                double rdW = 2048.0 * pw / maxDim;
                double rdH = 2048.0 * phpt / maxDim;

                // Point size -> render-dim units (matches PlaceTextBox so it exports as real points).
                double fontCanvas = ptSize * rdH / Math.Max(1, phpt);

                string text = fmt.Replace("{n}", (start + i).ToString())
                                 .Replace("{N}", n.ToString());
                if (string.IsNullOrWhiteSpace(text)) continue;

                var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontCanvas, Brushes.Black, ppd);
                double tw = ft.WidthIncludingTrailingWhitespace, th = ft.Height;
                double mx = rdW * 0.05, my = rdH * 0.04;
                double x = posH == 0 ? mx : posH == 2 ? rdW - tw - mx : (rdW - tw) / 2;
                double y = posV == 0 ? my : rdH - th - my;

                var ta = new TextAnnotation
                {
                    PageIndex = i,
                    Position = new Point(x, y),
                    Content = text,
                    FontSize = fontCanvas
                };
                ta.SetColor(Colors.Black);
                if (!_annotations.TryGetValue(i, out var list)) { list = []; _annotations[i] = list; }
                list.Add(ta);
                stamped.Add(i);
            }

            if (stamped.Count == 0) { SetStatus("Nothing to stamp"); return; }

            _undoStack.Push(new UndoEntry(UndoKind.StampBatch, Pages: [.. stamped], WasDirty: wasDirty));
            MarkDirty();

            if (_viewMode == ViewMode.Continuous)
            {
                foreach (int p in stamped)
                    if (_continuousCanvases.ContainsKey(p)) RenderAllAnnotations(p);
            }
            else
            {
                int cur = PageList.SelectedIndex < 0 ? 0 : PageList.SelectedIndex;
                RenderAllAnnotations(cur);
            }
            SetStatus($"Stamped page numbers on {stamped.Count} page(s)");
        }
    }
}
