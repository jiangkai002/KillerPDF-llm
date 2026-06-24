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

            public Dictionary<int, List<PageAnnotation>> Annotations = [];
            public Dictionary<int, (int w, int h)> RenderDims = [];
            public Dictionary<int, int> PageRotations = [];
            public Dictionary<int, string> FormTextValues = [];
            public Dictionary<int, bool> FormCheckValues = [];
            public Dictionary<string, string> FormRadioValues = [];
            public Dictionary<int, double> FormFontSizes = [];
            public Stack<UndoEntry> UndoStack = new();
            public Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects = [];
            public List<int> SearchResultPages = [];

            public string Title =>
                string.IsNullOrEmpty(OriginalFile)
                    ? "Untitled"
                    : System.IO.Path.GetFileNameWithoutExtension(OriginalFile);
        }

        private readonly List<DocumentSession> _sessions = [];
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
            PageImage.Source = null;
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
        // Closes every open document tab except `keep` (each may prompt to save if dirty, like a manual close).
        private void CloseOtherTabs(DocumentSession keep)
        {
            foreach (var s in _sessions.Where(z => !ReferenceEquals(z, keep) && (z.Doc != null || z.DeferredPath != null)).ToList())
                CloseTab(s);
        }

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

        private readonly List<(DocumentSession s, FrameworkElement btn, double natW)> _tabButtonList = [];
        private System.Windows.Threading.DispatcherTimer? _tabReflowTimer;
        private bool _reflowingTabs;

        private void RebuildTabStrip()
        {
            if (TabStrip == null || TabStripBorder == null) return;
            TabStrip.Children.Clear();
            _tabButtonList.Clear();

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
            {
                var btn = BuildTabButton(t);
                // Cache each tab's natural width once (off-tree measure). Reflow uses this instead of a
                // visible measure pass, so tabs never flash out to full width and back when squishing.
                btn.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _tabButtonList.Add((t, btn, btn.DesiredSize.Width));
                TabStrip.Children.Add(btn);
            }

            UpdateTabStripFade();
            // Reflow once laid out: tabs that don't fit roll into a "N more" dropdown at the end.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)ReflowTabs);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)UpdateFooterFade);
        }

        // Debounced from TabStripBorder.SizeChanged so we don't reflow (two layout passes) every resize tick.
        private void ScheduleTabReflow()
        {
            if (_tabReflowTimer is null)
            {
                _tabReflowTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(90) };
                _tabReflowTimer.Tick += (_, _) => { _tabReflowTimer!.Stop(); ReflowTabs(); };
            }
            _tabReflowTimer.Stop();
            _tabReflowTimer.Start();
        }

        // Lays the tabs out to measure them, then keeps as many as fit (left to right) on the strip and
        // moves the rest into a "N more" dropdown button at the end. The active tab is always kept visible.
        private void ReflowTabs()
        {
            if (_reflowingTabs || TabScroll is null || TabStrip is null) return;
            if (_tabButtonList.Count == 0) return;
            _reflowingTabs = true;
            try
            {
                double viewport = TabScroll.ViewportWidth;
                if (viewport <= 1) return;   // not laid out yet

                int n = _tabButtonList.Count;
                var nat = _tabButtonList.Select(t => t.natW).ToList();   // cached natural widths, no measure pass
                double natTotal = nat.Sum();
                const double minTab = 92;   // tab MinWidth (90) + ~margin

                // Case 1: everything fits at natural width.
                if (natTotal <= viewport)
                {
                    TabStrip.Children.Clear();
                    foreach (var (_, btn, _) in _tabButtonList) { btn.Width = double.NaN; TabStrip.Children.Add(btn); }
                    return;
                }

                // Case 2: SQUISH - shrink the tabs so they all still fit, no overflow. Only the wide tabs
                // shrink (toward `target`); narrow ones keep their width and hand their slack over so the
                // strip stays filled. This is what makes overflow a genuine last resort.
                if (n * minTab <= viewport)
                {
                    double target    = viewport / n;
                    double narrowSum = nat.Where(w => w <= target).Sum();
                    int    wideCount = nat.Count(w => w > target);
                    double perWide   = wideCount > 0 ? (viewport - narrowSum) / wideCount : target;
                    TabStrip.Children.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        _tabButtonList[i].btn.Width = nat[i] <= target ? double.NaN : Math.Min(nat[i], perWide);
                        TabStrip.Children.Add(_tabButtonList[i].btn);
                    }
                    return;
                }

                // Case 3: even squished to MinWidth they can't all fit - overflow into the dropdown.
                const double moreW = 40;
                double budget = viewport - moreW;
                int    nFit   = Math.Max(1, (int)(budget / minTab));
                double tabW   = budget / nFit;               // share the strip evenly among the shown tabs

                var visible  = _tabButtonList.Take(nFit).ToList();
                var overflow = _tabButtonList.Skip(nFit).ToList();
                // Active tab must stay on the strip: if it overflowed, swap it for the last visible tab.
                if (_active != null && overflow.Any(t => t.s == _active))
                {
                    var act = overflow.First(t => t.s == _active);
                    overflow.Remove(act);
                    if (visible.Count > 0)
                    {
                        var last = visible[^1];
                        visible.RemoveAt(visible.Count - 1);
                        overflow.Insert(0, last);
                    }
                    visible.Add(act);
                }

                TabStrip.Children.Clear();
                foreach (var (_, btn, _) in visible) { btn.Width = tabW; TabStrip.Children.Add(btn); }
                TabStrip.Children.Add(BuildTabOverflowButton(overflow));
            }
            finally { _reflowingTabs = false; }
        }

        // The "N v" dropdown at the end of the strip. Clicking it lists the overflowed tabs; choosing one
        // switches to it (the next reflow then pulls it onto the strip and pushes another into the menu).
        private FrameworkElement BuildTabOverflowButton(List<(DocumentSession s, FrameworkElement btn, double natW)> overflow)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = overflow.Count.ToString(),
                FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 5, 0),
            });
            content.Children.Add(new TextBlock
            {
                Text = "",   // Segoe MDL2 chevron-down
                FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var btn = new Button
            {
                Content = content,
                Height = 22,
                Margin = new Thickness(2, 3, 0, 0),
                Padding = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = $"{overflow.Count} more tab(s)",
                Style = (Style)FindResource("TabNewButton"),
            };

            var menu = new ContextMenu();
            foreach (var (s, _, _) in overflow)
            {
                var sess = s;
                var item = new MenuItem { Header = (s.IsDirty ? "• " : "") + s.Title };
                item.Click += (_, _) => SwitchToTab(sess);
                menu.Items.Add(item);
            }
            btn.Click += (_, _) => { menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; };
            return btn;
        }

        private FrameworkElement BuildTabButton(DocumentSession s)
        {
            bool active = s == _active;

            var bd = new Border
            {
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Margin = new Thickness(0, 3, 1, 0),
                Padding = new Thickness(12, 4, 5, 5),
                MinWidth = 90,
                MaxWidth = 220,
                Cursor = Cursors.Hand,
                Tag = s,
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

            // DockPanel (not StackPanel): the close button is docked right so it stays pinned and fully
            // visible, while the label fills the remaining space and trims with an ellipsis. With a
            // StackPanel the close button got pushed off the edge and clipped when tabs were squished.
            var sp = new DockPanel { LastChildFill = true };

            var label = new TextBlock
            {
                Text = (s.IsDirty ? "• " : "") + s.Title,
                FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextPrimary" : "TextSecondary");
            // Reserve the BOLD width on EVERY tab so activating one (which bolds its title) never widens
            // the tab and shoves its neighbours. Inactive tabs render normal weight but still claim the
            // bold width via MinWidth; capped so squished tabs still ellipsis-trim instead of clipping.
            double tabDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var boldFt = new FormattedText(label.Text,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                label.FontSize, Brushes.Black, tabDip);
            label.MinWidth = Math.Min(183, Math.Ceiling(boldFt.Width) + 1);
            if (active)
            {
                label.FontWeight = FontWeights.Bold;
                // Clicked tabs otherwise sit a hair low; nudge the active title up 2px.
                label.RenderTransform = new TranslateTransform(0, -2);
                // Drop shadow under the bold title, its strength taken from the theme's HeaderShadowOpacity
                // (same as the settings headers): heavier on Blood/Greed/Cyanotic, off in Light where a
                // dark shadow just muddies the pale tab. Resolved here at build time; the strip is rebuilt
                // on theme change (RebuildTabStrip in OnThemeChanged), so it tracks the theme.
                double titleShadowOpacity = TryFindResource("HeaderShadowOpacity") is double hso ? hso : 0.5;
                label.Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = titleShadowOpacity };
            }

            var close = new Button
            {
                Content = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Width = 16,
                Height = 16,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Close tab",
                Style = (Style)FindResource("TabCloseButton"),
            };
            close.Click += (_, e) => { CloseTab(s); };

            DockPanel.SetDock(close, Dock.Right);
            sp.Children.Add(close);   // docked right first, stays pinned
            sp.Children.Add(label);   // fills the rest, trims with ellipsis

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

            // Middle-click closes the tab (common tabbed-UI convention). Left-click switches on mouse-UP
            // (see the drag handlers below) so a press can begin a drag without first rebuilding the strip.
            bd.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; CloseTab(s); } };

            // Right-click: tab actions.
            bd.MouseRightButtonUp += (_, ev) =>
            {
                var menu = MakeThemedMenu();
                menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CloseTab"), (_, _) => CloseTab(s)));
                var others = MakeMenuItem(Loc("Str_Ctx_CloseOthers"), (_, _) => CloseOtherTabs(s));
                others.IsEnabled = _sessions.Count(z => z.Doc != null || z.DeferredPath != null) > 1;
                menu.Items.Add(others);
                menu.PlacementTarget = bd;
                menu.IsOpen = true;
                ev.Handled = true;
            };

            // Live drag-reorder: arm on press, and once past the threshold slide this tab between its
            // neighbours in real time as the cursor crosses their midpoints (a plain click still switches).
            // The Border is moved within TabStrip.Children directly - no rebuild mid-drag - so the mouse
            // capture survives. A clean RebuildTabStrip on release re-applies widths / overflow / styling.
            bd.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (close.IsMouseOver) return;   // let the close button handle its own click
                _tabDragSession = s;
                _tabDragStart = e.GetPosition(TabStrip);
                _tabGrabDX = e.GetPosition(bd).X;   // cursor offset within the tab, so it tracks under the grab point
                _tabDragging = false;
                bd.CaptureMouse();
                // Own the press entirely so it can't bubble to the title-bar's window-drag handler, and so
                // the mouse capture (not the caption hit-test) drives the drag - making it Y-independent.
                e.Handled = true;
            };
            bd.PreviewMouseMove += (_, e) =>
            {
                if (!bd.IsMouseCaptured || _tabDragSession is null) return;
                double x = e.GetPosition(TabStrip).X;
                if (!_tabDragging && Math.Abs(x - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
                _tabDragging = true;
                // Keep the grabbed tab UNDER the active tab but OVER other inactive tabs, the same in both drag
                // directions (raw child-index stacking would otherwise flip it front/back as it's reordered).
                Panel.SetZIndex(bd, s == _active ? 3 : 1);
                int curIdx = TabStrip.Children.IndexOf(bd);
                if (curIdx < 0) return;
                double slide = bd.ActualWidth + bd.Margin.Left + bd.Margin.Right;
                double rawLeft = x - _tabGrabDX;                 // grabbed tab's content-left, following the cursor
                // Detection uses the dragged tab's ADVANCING edge (unclamped), so even a wide tab reaches a narrow
                // neighbour's slot at the edge. The RENDER position is clamped so it never visually leaves the strip.
                double leftEdge  = rawLeft;                      // leading edge when dragging left
                double rightEdge = rawLeft + bd.ActualWidth;     // leading edge when dragging right
                double maxLeft = Math.Max(0, TabStrip.ActualWidth - slide);
                double renderLeft = Math.Min(Math.Max(0, rawLeft), maxLeft);

                // Swap when the ADVANCING edge crosses a neighbour's LAYOUT-slot midpoint: dragging right, the tab's
                // RIGHT edge past the right neighbour's midpoint; dragging left, its LEFT edge past the left one's.
                // Works for any width and the edge-vs-edge gap gives natural hysteresis (no bounce). Layout slots
                // ignore any in-flight slide transform.
                bool swapped = false;
                if (curIdx + 1 < TabStrip.Children.Count && TabStrip.Children[curIdx + 1] is FrameworkElement right
                    && rightEdge > LayoutMidX(right))
                {
                    TabStrip.Children.RemoveAt(curIdx + 1);
                    TabStrip.Children.Insert(curIdx, right);
                    ResyncSessionsFromTabStrip();
                    AnimateTabSlide(right, slide);    // it jumped left over the dragged tab; glide it in from the right
                    swapped = true;
                }
                else if (curIdx - 1 >= 0 && TabStrip.Children[curIdx - 1] is FrameworkElement left
                    && leftEdge < LayoutMidX(left))
                {
                    TabStrip.Children.RemoveAt(curIdx - 1);
                    TabStrip.Children.Insert(curIdx, left);
                    ResyncSessionsFromTabStrip();
                    AnimateTabSlide(left, -slide);    // it jumped right over the dragged tab; glide it in from the left
                    swapped = true;
                }

                // Pin the grabbed tab at its clamped render position. After a swap its slot moves by a neighbour's
                // width; refresh layout first so the new slot is current, then offset bd back to renderLeft.
                if (swapped) TabStrip.UpdateLayout();
                var bslot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(bd);
                SetTabOffsetX(bd, renderLeft - (bslot.X + bd.Margin.Left));
            };
            bd.PreviewMouseLeftButtonUp += (_, _) =>
            {
                if (!bd.IsMouseCaptured) return;   // close-button press (we never captured) - leave it alone
                bd.ReleaseMouseCapture();
                bool wasDragging = _tabDragging;
                _tabDragSession = null;
                _tabDragging = false;
                if (wasDragging)
                {
                    // Settle the grabbed tab from its dragged offset into its final slot, then rebuild cleanly
                    // (RebuildTabStrip re-applies widths / overflow / active styling and clears the transform).
                    if (bd.RenderTransform is TranslateTransform stt && Math.Abs(stt.X) > 0.5)
                    {
                        var settle = new System.Windows.Media.Animation.DoubleAnimation(0,
                            new Duration(TimeSpan.FromMilliseconds(120)))
                        {
                            EasingFunction = new System.Windows.Media.Animation.CubicEase
                            {
                                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                            },
                        };
                        settle.Completed += (_, _) => RebuildTabStrip();
                        stt.BeginAnimation(TranslateTransform.XProperty, settle);
                    }
                    else RebuildTabStrip();
                }
                else SwitchToTab(s);                  // it was a click, not a drag
            };

            return bd;
        }

        // Live drag-reorder state.
        private Point _tabDragStart;
        private double _tabGrabDX;          // cursor offset within the grabbed tab, to keep it under the grab point
        private DocumentSession? _tabDragSession;
        private bool _tabDragging;

        // Set a tab's horizontal offset immediately (no animation) - used to glue the grabbed tab to the cursor.
        private static void SetTabOffsetX(FrameworkElement tab, double x)
        {
            if (tab.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.XProperty, null);   // drop any prior animation so the set sticks
            tt.X = x;
        }

        // Midpoint X of a tab's LAYOUT slot in TabStrip coordinates (ignores any in-flight slide transform).
        private static double LayoutMidX(FrameworkElement fe)
        {
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
            return slot.X + slot.Width / 2;
        }

        // Slide a just-reordered tab from where it was into its new slot, so a swap reads as a smooth glide
        // instead of an instant jump. Z-order is left to how the strip was built: the active tab stays raised
        // and glides in front of its neighbours; an inactive dragged tab slides under them. A clean
        // RebuildTabStrip on drop clears these transient transforms.
        private static void AnimateTabSlide(FrameworkElement tab, double fromX)
        {
            if (tab.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            var anim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0,
                new Duration(TimeSpan.FromMilliseconds(140)))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
                },
            };
            tt.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        // Rebuild the session order from the tab strip's current visual order (each tab Border's Tag is its
        // session); sessions not currently shown (overflow) keep their order after the visible ones.
        private void ResyncSessionsFromTabStrip()
        {
            var vis = new List<DocumentSession>();
            foreach (var child in TabStrip.Children)
                if (child is FrameworkElement fe && fe.Tag is DocumentSession ds) vis.Add(ds);
            if (vis.Count == 0) return;
            var rest = _sessions.Where(z => !vis.Contains(z)).ToList();
            _sessions.Clear();
            _sessions.AddRange(vis);
            _sessions.AddRange(rest);
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
