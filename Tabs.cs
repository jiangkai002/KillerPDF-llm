using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PdfSharpCore.Pdf;

namespace KillerPDF
{
    // Tabbed document support. KillerPDF keeps one window and one live "working set" of
    // per-document fields (in MainWindow.xaml.cs). Each open PDF is a DocumentSession that
    // owns its own copy of those fields. Switching tabs captures the live fields into the
    // outgoing session and applies the incoming session's fields, then re-renders.
    public partial class MainWindow
    {
        // One open document. Holds the per-document state that the rest of MainWindow reads
        // and writes through its instance fields. The collection references here ARE the live
        // collections while this session is active.
        private sealed class DocumentSession
        {
            public PdfDocument? Doc;
            public string? CurrentFile;
            public string? OriginalFile;
            // Set on a restored tab that hasn't been loaded yet (lazy tabs): Doc stays null until the
            // user first switches to it, so startup doesn't render every reopened PDF.
            public string? DeferredPath;

            public double ZoomLevel = 1.0;
            public double LastRenderZoom = 1.0;
            public FitMode Fit = FitMode.None;
            public ViewMode View = ViewMode.Continuous;
            public EditTool Tool = EditTool.Select;   // active editing tool, remembered per document
            public int PageIndex;
            public bool IsDirty;
            public double ScrollH;
            public double ScrollV;
            public int SearchPageCursor = -1;

            public Dictionary<int, List<PageAnnotation>> Annotations = new();
            public Dictionary<int, (int w, int h)> RenderDims = new();
            public Dictionary<int, int> PageRotations = new();
            public Dictionary<int, string> FormTextValues = new();
            public Dictionary<int, bool> FormCheckValues = new();
            public Dictionary<string, string> FormRadioValues = new();
            public Dictionary<int, double> FormFontSizes = new();
            public Stack<UndoEntry> UndoStack = new();
            public Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects = new();
            public List<int> SearchResultPages = new();

            public string Title =>
                string.IsNullOrEmpty(OriginalFile)
                    ? "Untitled"
                    : System.IO.Path.GetFileNameWithoutExtension(OriginalFile);
        }

        private readonly List<DocumentSession> _sessions = new();
        private DocumentSession? _active;

        // ============================================================
        // Session state capture / apply
        // ============================================================

        // Copy the live working set INTO the session (call before switching away from it).
        private void CaptureSessionState(DocumentSession s)
        {
            s.Doc            = _doc;
            s.CurrentFile    = _currentFile;
            s.OriginalFile   = _originalFile;
            s.ZoomLevel      = _zoomLevel;
            s.LastRenderZoom = _lastRenderZoom;
            s.Fit            = _fitMode;
            s.View           = _viewMode;
            s.Tool           = _currentTool;
            s.IsDirty        = _isDirty;
            s.SearchPageCursor = _searchPageCursor;
            s.PageIndex      = PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : s.PageIndex;
            s.ScrollH        = PagePreviewPanel?.HorizontalOffset ?? 0;
            s.ScrollV        = PagePreviewPanel?.VerticalOffset ?? 0;

            s.Annotations      = _annotations;
            s.RenderDims       = _renderDims;
            s.PageRotations    = _pageRotations;
            s.FormTextValues   = _formTextValues;
            s.FormCheckValues  = _formCheckValues;
            s.FormRadioValues  = _formRadioValues;
            s.FormFontSizes    = _formFontSizes;
            s.UndoStack        = _undoStack;
            s.AllSearchRects   = _allSearchRects;
            s.SearchResultPages = _searchResultPages;
        }

        // Point the live working set AT the session's state. Pure field assignment - no UI.
        private void ApplySessionState(DocumentSession s)
        {
            _doc            = s.Doc;
            _currentFile    = s.CurrentFile;
            _originalFile   = s.OriginalFile;
            _zoomLevel      = s.ZoomLevel;
            _lastRenderZoom = s.LastRenderZoom;
            _fitMode        = s.Fit;
            _viewMode       = s.View;
            _currentTool    = s.Tool;
            _isDirty        = s.IsDirty;
            _searchPageCursor = s.SearchPageCursor;

            _annotations      = s.Annotations;
            _renderDims       = s.RenderDims;
            _pageRotations    = s.PageRotations;
            _formTextValues   = s.FormTextValues;
            _formCheckValues  = s.FormCheckValues;
            _formRadioValues  = s.FormRadioValues;
            _formFontSizes    = s.FormFontSizes;
            _undoStack        = s.UndoStack;
            _allSearchRects   = s.AllSearchRects;
            _searchResultPages = s.SearchResultPages;
        }

        // Make sure there is always at least one session, adopting whatever is currently live.
        private void EnsureInitialSession()
        {
            if (_sessions.Count > 0) return;
            var s = new DocumentSession();
            _sessions.Add(s);
            _active = s;
            CaptureSessionState(s);
        }

        // Commit / cancel any in-progress interaction so it doesn't bleed onto another document.
        private void CancelTransientForSwitch()
        {
            CommitActiveTextBox();
            RemoveTextEditHandles();
            ClearSelection();
            ClearTextSelection();
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
        }

        // ============================================================
        // Rendering the active session
        // ============================================================

        // Re-render whatever document the active session holds (or show the empty drop zone).
        private void RenderActiveSession()
        {
            if (_active == null || _active.Doc == null) { ShowEmptyState(); return; }

            FileNameLabel.Text = System.IO.Path.GetFileName(_active.OriginalFile ?? "");
            _annotationCanvas.Children.Clear();
            MarkDirty(_isDirty);   // sync the Save button color to this tab's dirty state
            BootstrapDocumentView(_active.PageIndex, autoFit: false);
            SetTool(_active.Tool); // restore this document's active editing tool (and its tool bar)

            // Restore the saved scroll position after the Background zoom pass queued inside
            // BootstrapDocumentView has run (ContextIdle is lower priority than Background).
            double sh = _active.ScrollH, sv = _active.ScrollV;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, (Action)(() =>
            {
                try
                {
                    PagePreviewPanel.ScrollToHorizontalOffset(sh);
                    PagePreviewPanel.ScrollToVerticalOffset(sv);
                }
                catch { }
            }));
        }

        // Visual reset to the no-document drop-zone state. Mirrors CloseFile's teardown but
        // does not close the document or touch session bookkeeping (callers handle that).
        private void ShowEmptyState()
        {
            _activeTextBox = null;
            RemoveTextEditHandles();
            _thumbCts?.Cancel();
            PageList.ItemsSource = null;
            if (FindName("PageImage") is System.Windows.Controls.Image img) img.Source = null;
            _annotationCanvas.Children.Clear();
            FileNameLabel.Text = "";
            DropZone.Visibility = Visibility.Visible;
            PopulateRecentFilesList();
            PagePreviewPanel.Visibility = Visibility.Collapsed;
            CloseSearchBar();
            HideDrawSettings();
            HideTextSettings();
            HideSignaturePopup();
            SetTool(EditTool.Select);
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = false;
            _pageJumpBox.IsEnabled = false;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _pageJumpBox.Text = "";
            _pageTotalLabel.Text = "/ –";
            OutlineTree.Items.Clear();
            SidebarOutlinesTab.IsEnabled = false;
            if (_sidebarShowingOutlines) SwitchSidebarToPagesTab();
            MarkDirty(false);
            SetStatus("Ready");
        }

        // ============================================================
        // Opening / switching / closing tabs
        // ============================================================

        // Prepare a tab to receive a document load: capture the current tab, then either reuse
        // the active tab if it's empty or create a new one, and blank the live working set.
        private DocumentSession BeginTabLoad(out DocumentSession? prev, out bool createdNew)
        {
            EnsureInitialSession();
            CommitActiveTextBox();
            CancelTransientForSwitch();
            prev = _active;
            if (_active != null) CaptureSessionState(_active);

            DocumentSession target;
            if (_active != null && _active.Doc == null && _active.DeferredPath == null)
            {
                target = _active;          // reuse the current empty tab (never a deferred one)
                createdNew = false;
            }
            else
            {
                target = new DocumentSession();
                // Inherit the current view mode so a newly opened PDF doesn't snap back to the
                // default (Continuous) when the user prefers Single / Two-Page / Grid.
                if (prev != null) { target.View = prev.View; target.Fit = prev.Fit; }
                _sessions.Add(target);
                createdNew = true;
            }
            _active = target;
            ApplySessionState(target);     // blank live fields (target has no document yet)
            return target;
        }

        // Roll back a failed / cancelled load started by BeginTabLoad.
        private void AbortTabLoad(DocumentSession target, DocumentSession? prev, bool createdNew)
        {
            if (createdNew) _sessions.Remove(target);
            _active = prev;
            if (prev != null) { ApplySessionState(prev); RenderActiveSession(); }
            else { EnsureInitialSession(); RenderActiveSession(); }
            RebuildTabStrip();
        }

        // Returns an open session for the given file path (case-insensitive full-path match), or null.
        private DocumentSession? FindOpenSession(string path)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path); } catch { full = path; }
            return _sessions.FirstOrDefault(s =>
                (s.Doc != null || s.DeferredPath != null) &&
                !string.IsNullOrEmpty(s.OriginalFile) &&
                string.Equals(SafeFullPath(s.OriginalFile!), full, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeFullPath(string p)
        {
            try { return System.IO.Path.GetFullPath(p); } catch { return p; }
        }

        // Open a PDF in its own tab (reusing the current tab if it is empty). If the same file is
        // already open in an unedited tab, switch to that tab instead of opening a duplicate.
        private void OpenInNewTab(string path)
        {
            EnsureInitialSession();
            CommitActiveTextBox();
            if (_active != null) CaptureSessionState(_active);   // keep dirty / path current for the check

            var existing = FindOpenSession(path);
            if (existing != null && !existing.IsDirty)
            {
                SwitchToTab(existing);
                SetStatus($"Already open: {System.IO.Path.GetFileName(path)}");
                return;
            }

            var target = BeginTabLoad(out var prev, out bool createdNew);
            OpenFile(path);
            if (_doc == null)
            {
                // A background open (encryption strip / repair) finalizes this tab itself, so the
                // not-yet-loaded _doc isn't a failure - leave the tab in place.
                if (_asyncOpenPending) return;
                // Open failed, was cancelled, or a password prompt was dismissed.
                AbortTabLoad(target, prev, createdNew);
                return;
            }
            CaptureSessionState(_active!);
            SetTool(_currentTool);   // sync the tool UI to this (new) tab's tool
            RebuildTabStrip();
        }

        // Cycle to the next (dir = +1) or previous (dir = -1) open document tab.
        private void CycleTab(int dir)
        {
            var docTabs = _sessions.Where(t => t.Doc != null || t.DeferredPath != null).ToList();
            if (docTabs.Count < 2 || _active == null) return;
            int i = docTabs.IndexOf(_active);
            if (i < 0) return;
            int next = (i + dir + docTabs.Count) % docTabs.Count;
            SwitchToTab(docTabs[next]);
        }

        // Switch the active tab to an already-loaded session.
        private void SwitchToTab(DocumentSession target)
        {
            if (target == _active) return;
            CommitActiveTextBox();
            CancelTransientForSwitch();
            if (_active != null) CaptureSessionState(_active);
            _active = target;
            ApplySessionState(target);
            if (target.Doc == null && target.DeferredPath != null) MaterializeDeferred(target);
            else RenderActiveSession();
            RebuildTabStrip();
        }

        // Load a restored-but-deferred tab's PDF the first time it is viewed (lazy tabs). The session
        // must already be the live working set (ApplySessionState called) before this runs.
        private void MaterializeDeferred(DocumentSession target)
        {
            var path = target.DeferredPath;
            target.DeferredPath = null;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                RenderActiveSession();        // file vanished since last session - show the empty state
                return;
            }
            OpenFile(path!);                  // loads into the live fields and renders the view
            if (_doc == null)
            {
                if (_asyncOpenPending) return;   // background strip/repair finalizes the tab itself
                RenderActiveSession(); return;
            }
            CaptureSessionState(target);      // persist the now-loaded document back into the session
        }

        // Close a tab. Prompts to save if that tab has unsaved changes, then switches to a
        // neighbouring tab (or the empty state when the last tab closes).
        private void CloseTab(DocumentSession? s)
        {
            EnsureInitialSession();
            if (s == null) return;

            // Make the target the live working set so its dirty flag / document are current.
            if (s != _active)
            {
                CommitActiveTextBox();
                CancelTransientForSwitch();
                if (_active != null) CaptureSessionState(_active);
                _active = s;
                ApplySessionState(s);
                RenderActiveSession();
            }
            else
            {
                CommitActiveTextBox();
                CaptureSessionState(s);
            }

            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    Loc("Str_Dlg_UnsavedClose"),
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) { RebuildTabStrip(); return; }
            }

            try { _doc?.Close(); } catch { }
            _doc = null;

            int idx = _sessions.IndexOf(s);
            _sessions.Remove(s);

            if (_sessions.Count == 0)
            {
                App.RemoveSetting("LastFile");   // a manually emptied window won't reopen on launch
                var blank = new DocumentSession();
                _sessions.Add(blank);
                _active = blank;
                ApplySessionState(blank);
                ShowEmptyState();
            }
            else
            {
                var next = _sessions[Math.Min(idx, _sessions.Count - 1)];
                _active = next;
                ApplySessionState(next);
                if (next.Doc == null && next.DeferredPath != null) MaterializeDeferred(next);
                else RenderActiveSession();
            }
            RebuildTabStrip();
        }

        // External (single-instance) entry points, called from App when a second launch forwards
        // a file path to this already-running instance.
        public void OpenFromExternal(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) OpenInNewTab(path!);
        }

        public void RestoreAndActivate()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Briefly toggle Topmost to pull the window in front without keeping it pinned.
            Topmost = true;
            Topmost = false;
            Focus();
        }

        // ============================================================
        // Tab strip UI (built in code so it follows the active theme)
        // ============================================================

        private void RebuildTabStrip()
        {
            if (TabStrip == null || TabStripBorder == null) return;
            TabStrip.Children.Clear();

            var docTabs = _sessions.Where(t => t.Doc != null || t.DeferredPath != null).ToList();
            // Only show the strip once there's more than one document - a single open PDF
            // doesn't need a tab bar.
            if (docTabs.Count < 2)
            {
                TabStripBorder.Visibility = Visibility.Collapsed;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)UpdateFooterFade);
                return;
            }

            TabStripBorder.Visibility = Visibility.Visible;
            foreach (var t in docTabs)
                TabStrip.Children.Add(BuildTabButton(t));
            TabStrip.Children.Add(BuildNewTabButton());
            UpdateTabStripFade();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)UpdateFooterFade);
        }

        private FrameworkElement BuildTabButton(DocumentSession s)
        {
            bool active = s == _active;

            var bd = new Border
            {
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Margin = new Thickness(0, 3, 1, 0),
                Padding = new Thickness(10, 4, 5, 5),
                MinWidth = 90,
                MaxWidth = 220,
                Cursor = Cursors.Hand,
                ToolTip = s.OriginalFile ?? "Untitled",
            };
            if (active)
            {
                // Active tab takes the document PANE background (BgCanvas, what DocPaneBorder uses)
                // and a themed accent stripe along the TOP edge, so it reads as the front tab
                // without an always-green underline and blends into the pane below it.
                bd.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
                bd.BorderThickness = new Thickness(0, 3, 0, 0);
                bd.SetResourceReference(Border.BorderBrushProperty, "SelectionBg");
                // Raise the active tab above its neighbours with a soft drop shadow on the top and sides.
                bd.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 9, ShadowDepth = 0, Opacity = 0.45,
                };
                Panel.SetZIndex(bd, 2);
            }
            else
            {
                // Opaque (matches the strip) so the shadow gradient behind never shows through the tab.
                bd.SetResourceReference(Border.BackgroundProperty, "BgSidebar");
                // Single beveled divider on the trailing edge: darker at the top fading to a shade
                // just lighter than the document pane (BgCanvas) at the bottom, for a subtle raised look.
                bd.BorderThickness = new Thickness(0, 0, 1, 0);
                bd.BorderBrush = MakeTabDividerBrush();
            }

            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            var label = new TextBlock
            {
                Text = (s.IsDirty ? "• " : "") + s.Title,
                FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 170,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextPrimary" : "TextSecondary");

            var close = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Width = 16,
                Height = 16,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                ToolTip = "Close tab",
                Style = (Style)FindResource("TabCloseButton"),
            };
            close.Click += (_, e) => { CloseTab(s); };

            sp.Children.Add(label);
            sp.Children.Add(close);

            // Every tab is opaque, so paint the strip's grain back onto it to keep the texture, while
            // ensuring the shadow gradient behind the strip never shows ON a tab (only in empty space).
            var content = new Grid();
            var grain = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = FindResource("GrainBrushShared") as Brush,
                // Bleed over the tab padding (10,4,5,5) so the texture reaches the tab edges.
                Margin = new Thickness(-10, -4, -5, -5),
            };
            grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            content.Children.Add(grain);
            content.Children.Add(sp);
            bd.Child = content;

            // The close button consumes its own clicks, so this only fires for the tab body.
            bd.MouseLeftButtonDown += (_, _) => SwitchToTab(s);
            // Middle-click closes the tab (common tabbed-UI convention).
            bd.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; CloseTab(s); } };

            return bd;
        }

        // Tab divider: the document-pane border color (PaneBorder) at the bottom, fading to fully
        // transparent at the top - a subtle separator that matches the doc-pane line on every theme.
        // Rebuilt on theme change (OnThemeChanged).
        private Brush MakeTabDividerBrush()
        {
            Color pane = (FindResource("PaneBorder") as SolidColorBrush)?.Color ?? Color.FromRgb(0x2e, 0x2e, 0x2e);
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, pane.R, pane.G, pane.B), 0.0));    // transparent, top
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, pane.R, pane.G, pane.B), 0.45));   // still transparent through the upper half
            brush.GradientStops.Add(new GradientStop(pane, 1.0));                                         // PaneBorder, bottom
            return brush;
        }

        private FrameworkElement BuildNewTabButton()
        {
            var b = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Width = 24,
                Height = 22,
                Margin = new Thickness(2, 3, 0, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Open another PDF in a new tab",
                Style = (Style)FindResource("TabNewButton"),
            };
            b.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF" };
                if (dlg.ShowDialog(this) == true) OpenInNewTab(dlg.FileName);
            };
            return b;
        }
    }
}
