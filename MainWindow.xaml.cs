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
    public partial class MainWindow : Window
    {
        private PdfDocument? _doc;
        private string? _currentFile;
        private string? _originalFile;  // user's real file path; survives temp swaps from crop/rotate, used by Save
        private Point _dragStartPoint;

        // Zoom
        private double _zoomLevel = 1.0;
        private double _lastRenderZoom = 1.0;
        private int _renderedPrimaryPage = -1;   // primary (spread-left) page currently rasterised
        private const double ZoomMin = 0.05;
        private const double ZoomMax = 5.0;
        private const double ZoomStep = 0.15;
        private enum FitMode { None, Width, Page }
        private FitMode _fitMode = FitMode.None;
        private System.Windows.Threading.DispatcherTimer? _rerenderTimer;
        private System.Threading.CancellationTokenSource? _secondaryRenderCts;
        private enum ViewMode { Single, Continuous, TwoPage, Grid }
        private ViewMode _viewMode = ViewMode.Continuous;
        private readonly StackPanel _continuousPanel = null!;
        private System.Threading.CancellationTokenSource? _continuousRenderCts;
        private readonly List<double> _continuousTops = [];
        private int _gridScrollToPage = -1;   // page to scroll to once its grid tile streams in (-1 = none)
        private int _continuousScrollTarget = -1;  // re-scroll here once its true height is known
        private double _continuousPageW;

        // Editing
        private EditTool _currentTool = EditTool.Select;
        // Per-document state. Not readonly: tab switching swaps these by reference so each
        // open document keeps its own annotations, undo history, form values, and search hits.
        private Dictionary<int, List<PageAnnotation>> _annotations = [];
        private Dictionary<int, (int w, int h)> _renderDims = [];
        // Stores the PDF /Rotate value for each page.  The temp file used by Docnet has
        // rotation stripped to zero so FPDF_GetPageWidth/Height returns MediaBox dims and
        // the content isn't clipped; RotateBitmap is applied at render time instead.
        private Dictionary<int, int> _pageRotations = [];

        // Form filling — text/check keyed by widget object number; radio keyed by field name
        private Dictionary<int, string>    _formTextValues  = [];
        private Dictionary<int, bool>      _formCheckValues = [];
        private Dictionary<string, string> _formRadioValues = [];
        private Dictionary<int, double>    _formFontSizes   = [];   // per-field user font-size override (points)
        // Floating font-size stepper shown while a form text field is focused.
        private Border?  _formSizeBar;
        private TextBox? _activeFormTb;
        private int      _activeFormObj;
        private double   _activeFormScale = 1;
        private const string FormOverlayTag = "FormFieldOverlay";

        // Undo stack — each entry is either an annotation removal or a full document snapshot.
        // AnnotationGroup removes a specific set of annotations in one step (a text edit = cover + text).
        private enum UndoKind { Annotation, Document, StampBatch, ClearAnnotations, AnnotationGroup }
        private readonly record struct UndoEntry(UndoKind Kind, int PageIdx = -1, byte[]? DocBytes = null, bool WasDirty = false, int[]? Pages = null, PageAnnotation? Annot = null, Dictionary<int, List<PageAnnotation>>? AnnotSnapshot = null, List<PageAnnotation>? AnnotGroup = null);
        private Stack<UndoEntry> _undoStack = new();
        private bool _isDrawing;
        private Point _drawStart;
        private UIElement? _activePreview;
        private InkAnnotation? _activeInk;
        private TextBox? _activeTextBox;
        private PageAnnotation? _selectedAnnotation;
        private Border? _selectionBorder;
        // Shift+click multi-selection (Select tool): extra annotations selected alongside the
        // primary _selectedAnnotation. Each gets its own outline. Delete removes the whole set.
        private readonly List<PageAnnotation> _selectedSet = [];
        private readonly List<Border> _selectionOutlines = [];

        // Draw/Highlight settings
        private Color _drawColor = Colors.Red;
        private double _drawWidth = 3;
        private byte _drawOpacity = 255;
        private Color _highlightColor = Color.FromArgb(80, 255, 255, 0);
        // Strikethrough / underline lines: opaque red by default.
        private Color _lineAnnotColor = Color.FromArgb(255, 220, 38, 38);
        private Border? _drawSettingsBar;

        // Text (typewriter) tool settings
        private double _textFontSize = 24;
        // WPF renders a given point size visually ~25% larger than the source PDF text, so scale the
        // detected size down when seeding an existing-text edit. The user can still fine-tune after.
        private const double EditTextSizeCorrection = 0.8;
        private bool _suppressSizeSync;   // guards the slider<->size-box two-way binding from feedback loops
        private TextAnnotation? _reeditOriginal;  // placed-text annotation currently being re-edited
        // The opaque cover dropped when starting an existing-text edit, awaiting its paired text commit.
        // Held so the text commit can group both into one undo, and so cancel/empty removes the cover.
        private CoverAnnotation? _pendingCover;
        // Dirty state captured before the cover was dropped, so undoing the grouped edit restores it.
        private bool _pendingEditWasDirty;
        private Color _textColor = Colors.Black;
        private byte _textOpacity = 255;          // alpha applied to placed text, like the draw tool
        private Color _textFillColor = Color.FromArgb(0, 255, 255, 255);  // text box background fill; A==0 = no fill
        private const double TextBoxDefaultWidth = 220;  // canvas-unit width of a freshly placed text box
        private Border? _textSettingsBar;

        // Signature / image resize
        private bool _isResizingSig;
        private Point _resizeSigStart;
        private double _resizeSigStartScale;
        private PlacedAnnotation? _resizeSigAnnot;
        private TextAnnotation? _resizeTextAnnot;               // text box being width-resized (height auto-fits)
        private HighlightAnnotation? _resizeHlAnnot;            // highlight/line being corner-resized (Bounds)
        private InkAnnotation? _resizeInkAnnot;                 // ink stroke being corner-resized (points scaled)
        private List<Point>? _resizeInkOrigPoints;             // snapshot of ink points at resize start
        private Rect _resizeInkOrigBounds;                      // ink bounding box at resize start
        private readonly List<Rectangle> _resizeHandles = [];   // 4 corner handles for placed annotations
        private string _resizeCorner = "SE";                    // which corner is being dragged
        private Point _resizeAnchor;                            // opposite corner, held fixed during resize

        // Mid-edit resize handles: 4 corners shown around the live editing TextBox so the user can
        // resize the box (and continue typing) without committing and re-selecting first.
        private readonly List<Rectangle> _textEditHandles = [];
        private bool _draggingTextEditHandle;
        private string _tehCorner = "SE";
        private Point _tehAnchor;
        private TextBox? _tehBox;

        // Placed annotation drag-to-move
        private bool _isDraggingAnnot;
        private Point _dragAnnotStart;

        // Middle-mouse / spacebar pan
        private bool _isPanning;
        private bool _spaceHeld;
        private Point _panStart;
        private double _panScrollH;
        private double _panScrollV;
        private Point _dragAnnotOrigPos;
        private PageAnnotation? _dragAnnot;   // placed image/signature OR typewriter text

        // Crop tool
        private Rect _cropCanvasRect;
        private Rectangle? _cropPreviewRect;
        private Rectangle? _cropPreviewRectBorder;  // unused after refactor; kept to avoid null-ref in cleanup
        private readonly List<System.Windows.Shapes.Path> _cropBrackets = []; // L-bracket corner visuals
        private Border? _cropConfirmBar;
        private readonly Button _toolCropBtn = null!;
        private readonly List<Rectangle> _cropHandles = [];
        private string? _activeCropHandleTag; // "NW" | "NE" | "SE" | "SW"
        private Point _cropHandleDragStart;
        private Rect _cropRectAtHandleDrag;
        private int _cropPageIndex = -1;   // page the crop rect was drawn on (grid/two-page aware)
        private TextBox? _cropX1Box, _cropY1Box, _cropX2Box, _cropY2Box;
        private TextBox? _cropRangeBox;
        private bool     _updatingCropInputs;
        private bool     _cropBarDragging;
        private Point    _cropBarDragOffset;

        // PDF link overlays (rendered on top of the annotation canvas)
        private readonly List<Canvas> _linkOverlays = [];

        // Sidebar + multi-page view
        private bool _sidebarCollapsed;
        private bool _sidebarRight;   // false = sidebar on the left (default), true = on the right
        private bool   _sidebarShowingOutlines;
        private bool   _outlinesFitted     = false;
        private double _savedPagesWidth    = 180;
        private double _savedOutlinesWidth = 300;
        private readonly Button _sidebarToggleBtn = null!;
        private readonly Border _sidebarBorder = null!;
        private ColumnDefinition _sidebarCol = null!;   // sized column (left or right per _sidebarRight)
        private readonly WrapPanel _pageContentPanel = null!;

        // Text selection
        private bool _isSelecting;
        private Point _selectStart;
        private Rectangle? _selectRect;
        private Rectangle? _pairedCoverOutline;   // dashed hint over a cover while its paired text is selected
        private string? _selectedText;

        // Search
        private Border? _searchBar;
        private TextBox? _searchBox;
        private TextBlock? _searchStatus;
        private readonly List<Rect> _searchHighlights = [];

        // Signatures
        private readonly SignatureStore _signatureStore = new();
        private SavedSignature? _pendingSignature;
        private Border? _signaturePopup;
        // Guided AcroForm signing: "pick once, reuse" - the chosen signature/initials are remembered
        // and dropped into every matching field. _pendingSignField, when set, routes the next pick from
        // the popup into that field instead of free placement.
        private SavedSignature? _activeSignatureChoice;
        private SavedSignature? _activeInitialsChoice;
        private (bool Initials, int ObjNum, int Page, double X, double Y, double W, double H)? _pendingSignField;
        // Form fields already signed, so re-clicking one offers change/remove instead of re-stamping.
        private readonly Dictionary<int, SignatureAnnotation> _signedFields = [];

        // Manual element refs (XAML codegen doesn't resolve these)
        private readonly Canvas _annotationCanvas = null!;
        // Active annotation surface. Single view: always _annotationCanvas. Continuous view:
        // set on mouse-down to the clicked page's overlay. Shared handlers target this.
        private Canvas _activeCanvas = null!;
        // The page surface a pointer gesture started on, captured on mouse-down. Kept separate
        // from _activeCanvas because RenderAllAnnotations reuses _activeCanvas as its render
        // target; in Grid view tiles stream in asynchronously and each one re-points _activeCanvas
        // mid-gesture, which previously committed annotations to the wrong page and broke
        // select/delete. Mouse-move/up resolve the gesture page and surface from these instead.
        private Canvas? _gestureCanvas;
        private int _gesturePage = -1;
        // Per-page overlay canvases for Continuous view, keyed by page index.
        private readonly Dictionary<int, Canvas> _continuousCanvases = [];
        private readonly Grid _pageContentGrid = null!;
        private readonly Button _toolSelectBtn = null!;
        private readonly Button _toolTextBtn = null!;
        private readonly Button _toolHighlightBtn = null!;
        private readonly Button _toolStrikeBtn = null!;
        private readonly Button _toolUnderlineBtn = null!;
        private readonly Button _toolDrawBtn = null!;
        private readonly Button _toolSignatureBtn = null!;
        private readonly Button _toolImageBtn = null!;
        private readonly Button _saveAsBtnRef = null!;
        private readonly Button _closeFileBtnRef = null!;
        private readonly ComboBox _zoomBox = null!;
        private readonly StackPanel _portableBadge = null!;
        private readonly TextBox _pageJumpBox = null!;
        private readonly TextBlock _pageTotalLabel = null!;

        // Dirty / unsaved-change tracking
        private bool _isDirty = false;

        // Whole-document search results (PDF-space rects per page)
        private Dictionary<int, List<(double left, double bottom, double right, double top)>> _allSearchRects = [];
        private List<int> _searchResultPages = [];
        private int _searchPageCursor = -1;

        public MainWindow()
        {
            InitializeComponent();
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            _annotationCanvas = (Canvas)FindName("AnnotationCanvas")!;
            _activeCanvas = _annotationCanvas;
            _pageContentGrid = (Grid)FindName("PageContentGrid")!;
            _toolSelectBtn = (Button)FindName("ToolSelectBtn")!;
            _toolTextBtn = (Button)FindName("ToolTextBtn")!;
            _toolHighlightBtn = (Button)FindName("ToolHighlightBtn")!;
            _toolStrikeBtn = (Button)FindName("ToolStrikeBtn")!;
            _toolUnderlineBtn = (Button)FindName("ToolUnderlineBtn")!;
            _toolDrawBtn = (Button)FindName("ToolDrawBtn")!;
            _toolSignatureBtn = (Button)FindName("ToolSignatureBtn")!;
            _toolImageBtn = (Button)FindName("ToolImageBtn")!;
            _toolCropBtn = (Button)FindName("ToolCropBtn")!;
            _sidebarToggleBtn = (Button)FindName("SidebarToggleBtn")!;
            _sidebarBorder = (Border)FindName("SidebarBorder")!;
            _sidebarCol = (ColumnDefinition)FindName("SidebarCol")!;
            _pageContentPanel = (WrapPanel)FindName("PageContentPanel")!;
            _saveAsBtnRef = (Button)FindName("SaveAsBtn")!;
            _closeFileBtnRef = (Button)FindName("CloseFileBtn")!;
            _zoomBox = (ComboBox)FindName("ZoomBox")!;
            // Read-only editable combo: hide its text-selection highlight so the displayed % never
            // looks like selected text after a pick.
            _zoomBox.Loaded += (_, _) =>
            {
                if (_zoomBox.Template?.FindName("PART_EditableTextBox", _zoomBox) is TextBox etb)
                    etb.SelectionBrush = System.Windows.Media.Brushes.Transparent;
            };
            _portableBadge = (StackPanel)FindName("PortableBadge")!;
            _pageJumpBox = (TextBox)FindName("PageJumpBox")!;
            _pageTotalLabel = (TextBlock)FindName("PageTotalLabel")!;
            _continuousPanel = (StackPanel)FindName("ContinuousPanel")!;
            PagePreviewPanel.ScrollChanged += PagePreviewPanel_ScrollChanged;
            PreviewMouseDown += SettingsDismiss_PreviewMouseDown;   // non-modal Settings: close on outside click
            // Keep the (bottom-anchored) Settings panel sized to the document area while the window
            // resizes, so it tracks smoothly instead of snapping on the next open.
            MainContentGrid.SizeChanged += (_, _) =>
            {
                if (SettingsOverlay.Visibility == Visibility.Visible) PositionSettingsPanel();
                UpdateTabStripFade();
            };
            // The sidebar column resizes via the splitter / collapse; track its width so the tab-strip
            // shadow gradient stays clipped to the document column.
            if (FindName("SidebarOuterGrid") is FrameworkElement sidebarOuter)
                sidebarOuter.SizeChanged += (_, _) => UpdateTabStripFade();
            // The footer shadow tracks the document pane's actual position; re-anchor when it (or the
            // tab strip, which shifts the document) changes size.
            DocPaneBorder.SizeChanged += (_, _) => UpdateFooterFade();
            TabStripBorder.SizeChanged += (_, _) => UpdateTabStripFade();
            // After a sidebar-splitter drag, snap fully closed if dragged too narrow, else save the width.
            SidebarSplitter.PreviewMouseLeftButtonUp += (_, _) => OnSidebarResized();
            // Grabbing the splitter while collapsed reveals the page list so it can be pulled open.
            SidebarSplitter.PreviewMouseLeftButtonDown += (_, _) => { _sidebarWantClose = false; OnSidebarSplitterPress(); };
            // Pull the splitter well past the minimum mid-drag to close (the column itself can't clip).
            SidebarSplitter.PreviewMouseMove += OnSidebarSplitterMove;
            // If a drag is interrupted (alt-tab, focus loss, taking a screenshot), finalize it so the
            // sidebar can't get stuck half-open with its content hidden.
            SidebarSplitter.LostMouseCapture += (_, _) => OnSidebarResized();
            if (Enum.TryParse<ViewMode>(App.GetSetting("ViewMode"), out var savedVm))
                _viewMode = savedVm;
            if (Enum.TryParse<ToolbarStyle>(App.GetSetting("ToolbarStyle"), out var savedTb))
                _toolbarStyle = savedTb;
            if (string.Equals(App.GetSetting("SidebarSide"), "Right", StringComparison.OrdinalIgnoreCase))
                _sidebarRight = true;
            IndexToolbarButtons();
            OutlineTree.SelectedItemChanged += OutlineTree_SelectedItemChanged;
            LoadSignatures();
            BuildContextMenu();
            SetTool(EditTool.Select);
            ApplyGrainTexture();
            ApplyToolNumberTooltips();   // append the 1-9 toolbar positions to the tool tooltips
            SourceInitialized += MainWindow_SourceInitialized;
            Closed += (_, _) => { _continuousRenderCts?.Cancel(); _doc?.Close(); App.CleanupSessionTemps(); };

            // Open a file passed via command-line / file association (e.g. double-clicking a .pdf)
            // Also show the portable badge when running outside the install location.
            bool contentRevealed = false;
            ContentRendered += (_, _) =>
            {
                Services.ThemeManager.RefreshIcons();
                // Final pass once the layout has real widths. The tab-strip / footer shadow gradients
                // were intermittently blank at startup (their feather mask + margin were computed
                // before the sidebar column had measured), and only a manual sidebar tweak forced a
                // correct re-layout. Re-running it here reproduces that fix automatically.
                UpdateTabStripFade();
                // The content is held invisible (RootClipGrid.Opacity=0 in XAML) until this final
                // positioning pass has run; fade it in once so the brief unpositioned first frame
                // (the "load deform" - shadows/toolbars snapping into place) is never visible.
                if (!contentRevealed)
                {
                    contentRevealed = true;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        var reveal = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                            new Duration(TimeSpan.FromMilliseconds(140)));
                        RootClipGrid.BeginAnimation(OpacityProperty, reveal);
                    }));
                }
            };
            Services.ThemeManager.ThemeChanged += OnThemeChanged;

            Loaded += (_, _) =>
            {
                RestoreWindowSettings();
                ApplySidebarSide();   // place the sidebar on the saved side (default left)
                if (_toolbarStyle != ToolbarStyle.SmallIcons) ApplyToolbarAppearance();

                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && System.IO.File.Exists(args[1]))
                {
                    OpenInNewTab(args[1]);
                }
                else
                {
                    // Reopen every tab from the last session (falls back to the single LastFile for
                    // settings written before multi-tab restore existed).
                    var saved = App.GetSetting("OpenTabs");
                    string[] paths = !string.IsNullOrEmpty(saved)
                        ? saved!.Split('|')
                        : (App.GetSetting("LastFile") is { Length: > 0 } lf ? [lf] : []);
                    // Lazy restore: create a placeholder tab for each saved file but load only the
                    // focused one. The rest materialize (load + render) the first time they're clicked,
                    // so startup cost no longer scales with how many tabs were open last session.
                    _sessions.Clear();
                    bool openedAny = false;
                    foreach (var f in paths)
                        if (!string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                        {
                            _sessions.Add(new DocumentSession { OriginalFile = f, CurrentFile = f, DeferredPath = f });
                            openedAny = true;
                        }
                    if (!openedAny)
                    {
                        _active = null;
                        PopulateRecentFilesList();   // empty state: show the recent list
                        EnsureInitialSession();
                        RebuildTabStrip();
                    }
                    else
                    {
                        var wantActive = App.GetSetting("ActiveTab");
                        var activeTarget = (!string.IsNullOrEmpty(wantActive)
                                ? _sessions.FirstOrDefault(ss => string.Equals(ss.OriginalFile, wantActive, StringComparison.OrdinalIgnoreCase))
                                : null)
                            ?? _sessions[0];
                        _active = activeTarget;
                        ApplySessionState(activeTarget);
                        MaterializeDeferred(activeTarget);   // load + render only the focused tab
                        RebuildTabStrip();
                    }
                }

                if (App.IsPortable())
                    _portableBadge.Visibility = Visibility.Visible;
            };
        }

        // ============================================================
        // Maximize-respects-taskbar fix (WindowStyle=None needs WM_GETMINMAXINFO)
        // ============================================================

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
            ThemeManager.ApplyDwm(hwnd);
            // Snapping moves the window without changing WindowState, so re-evaluate the rounded vs
            // squared chrome on every move (and once now that the handle exists).
            LocationChanged += OnWindowLocationChanged;
            UpdateWindowChrome();
        }

        // ============================================================
        // Settings panel
        // ============================================================

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Toggle: clicking the gear while the panel is open closes it.
            if (SettingsOverlay.Visibility == Visibility.Visible) { SlideSettingsClosed(); return; }
            // Sync radio buttons to current theme before showing
            var cur = ThemeManager.Current;
            ThemeDarkRadio.IsChecked  = cur == Theme.Dark;
            ThemeLightRadio.IsChecked = cur == Theme.Light;
            ThemeHCRadio.IsChecked    = cur == Theme.Black;
            ThemeBloodRadio.IsChecked = cur == Theme.Blood;
            ThemeGreedRadio.IsChecked    = cur == Theme.Greed;
            ThemeCyanoticRadio.IsChecked = cur == Theme.Cyanotic;
            ThemeCurrentLabel.Text       = ThemeDisplayName(cur);
            UpdateAccentDotSelection();
            UpdateAccentRowsVisibility(animate: false);
            // Sync language picker
            var curLoc = KillerPDF.Services.LocaleManager.Current;
            LangEnRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.EnUS;
            LangEsRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Es;
            LangZhTWRadio.IsChecked = curLoc == KillerPDF.Services.Locale.ZhTW;
            LangZhCNRadio.IsChecked = curLoc == KillerPDF.Services.Locale.ZhCN;
            LangBnRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.Bn;
            LangTrRadio.IsChecked   = curLoc == KillerPDF.Services.Locale.TrTR;
            LangCurrentLabel.Text   = LangDisplayName(curLoc);
            // Sync view mode radios
            ViewSingleRadio.IsChecked     = _viewMode == ViewMode.Single;
            ViewContinuousRadio.IsChecked = _viewMode == ViewMode.Continuous;
            ViewTwoPageRadio.IsChecked    = _viewMode == ViewMode.TwoPage;
            ViewGridRadio.IsChecked       = _viewMode == ViewMode.Grid;
            ViewCurrentLabel.Text         = ViewModeDisplayName(_viewMode);
            // Sync toolbar style picker
            ToolbarSmallRadio.IsChecked  = _toolbarStyle == ToolbarStyle.SmallIcons;
            ToolbarLargeRadio.IsChecked  = _toolbarStyle == ToolbarStyle.LargeIcons;
            ToolbarBesideRadio.IsChecked = _toolbarStyle == ToolbarStyle.TextBeside;
            ToolbarUnderRadio.IsChecked  = _toolbarStyle == ToolbarStyle.TextUnder;
            ToolbarOnlyRadio.IsChecked   = _toolbarStyle == ToolbarStyle.TextOnly;
            ToolbarCurrentLabel.Text     = ToolbarStyleName(_toolbarStyle);
            // Sync sidebar-side picker
            SidebarLeftRadio.IsChecked   = !_sidebarRight;
            SidebarRightRadio.IsChecked  = _sidebarRight;
            SidebarCurrentLabel.Text     = _sidebarRight ? "Right" : "Left";
            PositionSettingsPanel();
            SettingsOverlay.Visibility = Visibility.Visible;
            SlideSettingsOpen();
        }

        private const double SettingsPanelWidth = 228;

        // Expands the panel out of the sidebar (Width grows from the flush left edge). Clipped while
        // animating so it reveals left-to-right; clip is dropped at the end so the drop shadow shows.
        private void SlideSettingsOpen()
        {
            SettingsPanel.ClipToBounds = true;
            var anim = new DoubleAnimation(0, SettingsPanelWidth, new Duration(TimeSpan.FromMilliseconds(160)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) =>
            {
                SettingsPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                SettingsPanel.Width = SettingsPanelWidth;
                SettingsPanel.ClipToBounds = false;   // reveal the right/bottom drop shadow
            };
            SettingsPanel.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        // Shrinks it back into the sidebar, then hides the overlay.
        private void SlideSettingsClosed()
        {
            if (SettingsOverlay.Visibility != Visibility.Visible) return;
            SettingsPanel.ClipToBounds = true;
            double from = SettingsPanel.ActualWidth > 0 ? SettingsPanel.ActualWidth : SettingsPanelWidth;
            var anim = new DoubleAnimation(from, 0, new Duration(TimeSpan.FromMilliseconds(140)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                SettingsOverlay.Visibility = Visibility.Collapsed;
                SettingsPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                SettingsPanel.Width = SettingsPanelWidth;
                SettingsPanel.ClipToBounds = false;
            };
            SettingsPanel.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        // ── Settings submenus: inline accordion sections that expand in place below their row.
        // Sections are independent: opening one does NOT collapse the others, so the user can keep
        // several expanded at once. Their open/closed state persists while the app runs.
        private void SettingsMenu_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Primitives.ToggleButton btn) return;
            var panel = SubmenuFor(btn);
            if (panel != null)
                panel.Visibility = btn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private System.Windows.Controls.StackPanel? SubmenuFor(System.Windows.Controls.Primitives.ToggleButton btn)
        {
            if (btn == LangMenuButton)    return LangSubmenu;
            if (btn == ThemeMenuButton)   return ThemeSubmenu;
            if (btn == ToolbarMenuButton) return ToolbarSubmenu;
            if (btn == ViewMenuButton)    return ViewSubmenu;
            if (btn == SidebarMenuButton) return SidebarSubmenu;
            return null;
        }

        // Non-modal Settings: a mouse-down anywhere outside the panel dismisses it WITHOUT swallowing the
        // click (it still reaches its target). The title bar is excluded so dragging the window keeps the
        // panel open; the gear is excluded so it can toggle itself closed.
        private void SettingsDismiss_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (SettingsOverlay.Visibility != Visibility.Visible) return;
            if (e.OriginalSource is not DependencyObject src) return;
            if (IsDescendantOf(src, SettingsPanel)) return;
            if (TitleBarBorder != null && IsDescendantOf(src, TitleBarBorder)) return;
            if (SettingsBtn != null && IsDescendantOf(src, SettingsBtn)) return;
            SlideSettingsClosed();
        }

        /// <summary>
        /// Pins the Settings panel's left edge flush against the sidebar's right edge (just past the
        /// splitter), bottom-anchored above the footer. Not draggable; tracks the sidebar's collapsed
        /// width and window resizes automatically.
        /// </summary>
        private void PositionSettingsPanel()
        {
            double edge = (_sidebarCol?.ActualWidth ?? 180) + 6;   // sidebar column + 6px splitter
            // Anchor the panel against the sidebar's inner edge so it opens toward the document,
            // on whichever side the sidebar currently sits.
            SettingsPanel.Margin = _sidebarRight
                ? new Thickness(0, 0, edge, 36)
                : new Thickness(edge, 0, 0, 36);
            // Never grow taller than the document area; the inner ScrollViewer scrolls instead.
            SettingsPanel.MaxHeight = Math.Max(240, MainContentGrid.ActualHeight - 8);
        }

        // ── Quick fade in/out for the full-window overlay panels (Settings/Shortcuts/About) ──
        private static void FadeOverlayIn(UIElement el)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            el.Visibility = Visibility.Visible;
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        private static void FadeOverlayOut(UIElement el)
        {
            if (el.Visibility != Visibility.Visible) return;
            var anim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(90)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                el.Visibility = Visibility.Collapsed;
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
            };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Fades an annotate (draw/text) settings bar out over ~90ms, then removes it from its parent -
        // so the bar dissolves when its tool is deselected and crossfades when switching tools, matching
        // the About/Settings overlays.
        private static void FadeOutAndRemoveBar(Border? bar)
        {
            if (bar is null) return;
            var anim = new DoubleAnimation(bar.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(90)))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) =>
            {
                bar.BeginAnimation(UIElement.OpacityProperty, null);
                (bar.Parent as Panel)?.Children.Remove(bar);
            };
            bar.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Collapses the visible annotate bar to a thin peek strip, or expands it back. Triggered by
        // re-clicking the already-active tool, so a second click tucks the bar away instead of the
        // old behaviour of rebuilding it (which flickered).
        private void ToggleAnnotBarMinimized()
        {
            var bar = _textSettingsBar ?? _drawSettingsBar;
            if (bar is null) return;
            _annotBarMinimized = !_annotBarMinimized;
            bar.ClipToBounds = true;
            const double peek = 13;   // thin strip, just enough for the grip dots
            if (_annotBarMinimized)
            {
                // Freeze the current width so collapsing the content can't shrink the bar to the dots and
                // slide it to the corner - it stays a same-width strip in place.
                bar.Width = bar.ActualWidth;
                bar.Effect = null;   // minimized strips never carry a drop shadow
                _annotBarFullHeight = bar.ActualHeight > 0 ? bar.ActualHeight : bar.DesiredSize.Height;
                var anim = new DoubleAnimation(_annotBarFullHeight, peek, new Duration(TimeSpan.FromMilliseconds(120)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
                anim.Completed += (_, _) =>
                {
                    if (_annotBarContent is not null) _annotBarContent.Visibility = Visibility.Collapsed;
                    if (_annotBarDots is not null) _annotBarDots.Visibility = Visibility.Visible;
                    bar.ClipToBounds = false;   // content is hidden now, nothing to clip
                };
                bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
            else
            {
                // Show the full content again before growing back, and let the width track content again.
                bar.Width = double.NaN;
                bar.Effect = AnnotBarShadow();   // restore the drop shadow on the expanded bar
                if (_annotBarContent is not null) _annotBarContent.Visibility = Visibility.Visible;
                if (_annotBarDots is not null) _annotBarDots.Visibility = Visibility.Collapsed;
                double full = _annotBarFullHeight > 0 ? _annotBarFullHeight : bar.ActualHeight;
                var anim = new DoubleAnimation(peek, full, new Duration(TimeSpan.FromMilliseconds(120)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                anim.Completed += (_, _) =>
                {
                    bar.BeginAnimation(FrameworkElement.HeightProperty, null);
                    bar.Height = double.NaN;   // back to auto so it tracks its content again
                    bar.ClipToBounds = false;
                };
                bar.BeginAnimation(FrameworkElement.HeightProperty, anim);
            }
        }

        private void SettingsOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => SlideSettingsClosed();

        // While Settings is open the full-window overlay catches input. Let the wheel pass through to
        // the content behind it (document or sidebar under the cursor) so the user can keep reading
        // without the panel closing - only a click closes it.
        private void SettingsOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var fwd = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = this
            };
            Point sp = e.GetPosition(_sidebarBorder);
            bool overSidebar = _sidebarBorder is { IsVisible: true }
                               && sp.X >= 0 && sp.X <= _sidebarBorder.ActualWidth
                               && sp.Y >= 0 && sp.Y <= _sidebarBorder.ActualHeight;
            if (overSidebar) PageList.RaiseEvent(fwd);
            else            PagePreviewPanel.RaiseEvent(fwd);
        }

        private void SettingsOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => e.Handled = true;

        private void SettingsOverlayClose_Click(object sender, RoutedEventArgs e)
            => SlideSettingsClosed();

        private void OnThemeChanged()
        {
            // Refresh snapshot FindResource calls that were set as local values.
            // SetResourceReference bindings update automatically; sidebar tabs and
            // active tool button background still need an explicit refresh.
            SetTool(_currentTool);
            if (_sidebarShowingOutlines)
                SwitchSidebarToOutlinesTab();
            else
                SwitchSidebarToPagesTab();
            RefreshSelectionAccent();
            RebuildTabStrip();   // tab divider bevel is derived from BgCanvas; refresh for the new theme
            // The signature popup is built from snapshot (FindResource) colors, so rebuild it in place
            // if it's open so it picks up the new theme without the user having to close and reopen it.
            if (_signaturePopup is not null) ShowSignaturePopup();
        }

        private void ThemeDarkRadio_Checked(object sender, RoutedEventArgs e)     => SelectTheme(Theme.Dark);
        private void ThemeLightRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Light);
        private void ThemeHCRadio_Checked(object sender, RoutedEventArgs e)       => SelectTheme(Theme.Black);
        private void ThemeBloodRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Blood);
        private void ThemeGreedRadio_Checked(object sender, RoutedEventArgs e)    => SelectTheme(Theme.Greed);
        private void ThemeCyanoticRadio_Checked(object sender, RoutedEventArgs e) => SelectTheme(Theme.Cyanotic);

        private void SelectTheme(Theme theme)
        {
            ThemeManager.Apply(theme);
            if (ThemeCurrentLabel is not null) ThemeCurrentLabel.Text = ThemeDisplayName(theme);
            UpdateAccentDotSelection();
            UpdateAccentRowsVisibility(animate: true);
            // Intentionally leave the flyout open so the user can try another theme right away
            // without reopening the submenu.
        }

        // Each theme family has its own picker row beneath its radio. Clicking a swatch sets that
        // family's accent (independently remembered). Switching themes animates the rows' heights so
        // the picker slides to the selected theme while the total menu height stays fixed.
        private void AccentDot_Click(object sender, MouseButtonEventArgs e)      => HandleAccentDot(sender, Theme.Dark);
        private void AccentDotLight_Click(object sender, MouseButtonEventArgs e) => HandleAccentDot(sender, Theme.Light);
        private void AccentDotBlack_Click(object sender, MouseButtonEventArgs e) => HandleAccentDot(sender, Theme.Black);

        private void HandleAccentDot(object sender, Theme family)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
            if (!Enum.TryParse<DarkAccent>(tag, out var accent)) return;
            ThemeManager.ApplyAccent(family, accent);   // persists for that family; reapplies if active
            UpdateAccentDotSelection();
        }

        // Ring each family's own selected swatch (Dark, Light, and Black remember independently).
        private void UpdateAccentDotSelection()
        {
            if (DarkAccentRow is null) return;
            var ring = (System.Windows.Media.Brush)FindResource("TextPrimary");
            void RingRow(Border[] dots, DarkAccent chosen)
            {
                foreach (var dot in dots)
                {
                    bool sel = dot.Tag is string t && Enum.TryParse<DarkAccent>(t, out var a) && a == chosen;
                    dot.BorderBrush = sel ? ring : System.Windows.Media.Brushes.Transparent;
                }
            }
            RingRow([AccentDotRed, AccentDotOrange, AccentDotGreen, AccentDotTeal, AccentDotBlue, AccentDotPurple], ThemeManager.DarkAccentChoice);
            RingRow([AccentDotLightRed, AccentDotLightOrange, AccentDotLightGreen, AccentDotLightTeal, AccentDotLightBlue, AccentDotLightPurple], ThemeManager.LightAccentChoice);
            RingRow([AccentDotBlackRed, AccentDotBlackOrange, AccentDotBlackGreen, AccentDotBlackTeal, AccentDotBlackBlue, AccentDotBlackPurple], ThemeManager.BlackAccentChoice);
        }

        // Slide the picker to the active theme. Each row animates its height; because the outgoing row
        // shrinks by the same amount the incoming one grows, the combined height is constant - so the
        // menu doesn't change height, the picker just slides into place under the selected theme.
        private void UpdateAccentRowsVisibility(bool animate)
        {
            var cur = ThemeManager.Current;
            SlideRow(DarkAccentRow,  cur == Theme.Dark,         animate);
            SlideRow(LightAccentRow, cur == Theme.Light,        animate);
            SlideRow(BlackAccentRow, cur == Theme.Black, animate);
        }

        private const double AccentRowHeight = 26;   // 18px swatch + 8px breathing room

        // Slides the picker row open/closed by animating its Height. Each call clears any in-flight
        // height animation first so rapid theme clicking can't leave a held animation that strands
        // the wrong row visible under the wrong heading.
        private static void SlideRow(FrameworkElement? row, bool show, bool animate)
        {
            if (row is null) return;
            row.BeginAnimation(HeightProperty, null);   // drop any leftover/held animation
            if (show)
            {
                row.Visibility = Visibility.Visible;
                if (animate)
                {
                    row.Height = 0;
                    row.BeginAnimation(HeightProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(0, AccentRowHeight, TimeSpan.FromMilliseconds(170)));
                }
                else row.Height = AccentRowHeight;
            }
            else if (animate && row.Visibility == Visibility.Visible && row.ActualHeight > 0.5)
            {
                var h = new System.Windows.Media.Animation.DoubleAnimation(AccentRowHeight, 0, TimeSpan.FromMilliseconds(150));
                h.Completed += (_, __) => { row.BeginAnimation(HeightProperty, null); row.Height = 0; row.Visibility = Visibility.Collapsed; };
                row.BeginAnimation(HeightProperty, h);
            }
            else
            {
                row.Height = 0;
                row.Visibility = Visibility.Collapsed;
            }
        }

        // Localized display name for each theme, shown on the picker row.
        private string ThemeDisplayName(Theme t) => t switch
        {
            Theme.Light        => Loc("Str_Theme_Light"),
            Theme.Black        => Loc("Str_Theme_Black"),
            Theme.Blood        => Loc("Str_Theme_Blood"),
            Theme.Greed        => Loc("Str_Theme_Greed"),
            Theme.Cyanotic     => Loc("Str_Theme_Cyanotic"),
            _                  => Loc("Str_Theme_Dark"),
        };

        private void LangEnRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.EnUS);
        private void LangEsRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.Es);
        private void LangZhTWRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(KillerPDF.Services.Locale.ZhTW);
        private void LangZhCNRadio_Checked(object sender, RoutedEventArgs e) => SelectLocale(KillerPDF.Services.Locale.ZhCN);
        private void LangBnRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.Bn);
        private void LangTrRadio_Checked(object sender, RoutedEventArgs e)   => SelectLocale(KillerPDF.Services.Locale.TrTR);

        private void SelectLocale(KillerPDF.Services.Locale loc)
        {
            KillerPDF.Services.LocaleManager.Apply(loc);
            ApplyToolNumberTooltips();   // re-append the numbers to the now-localized tool tooltips
            LangCurrentLabel.Text = LangDisplayName(loc);
            // The Theme and Toolbar picker labels are set imperatively (not DynamicResource), so the
            // language switch happening while the panel is open would leave them in the old language.
            if (ThemeCurrentLabel is not null)   ThemeCurrentLabel.Text   = ThemeDisplayName(ThemeManager.Current);
            if (ToolbarCurrentLabel is not null) ToolbarCurrentLabel.Text = ToolbarStyleName(_toolbarStyle);
            if (ViewCurrentLabel is not null)    ViewCurrentLabel.Text    = ViewModeDisplayName(_viewMode);
            LangMenuButton.IsChecked = false;   // collapses the inline language section after a pick

            // The status bar text is a formatted string (not a DynamicResource), so it keeps the
            // language it was last set in. Re-set it in the new locale instead of leaving it stale.
            if (_doc is not null && PageList.SelectedIndex >= 0)
                SetStatus(string.Format(Loc("Str_PageOf"), PageList.SelectedIndex + 1, _doc.PageCount));
            else
                SetStatus(Loc("Str_Ready"));

            // The canvas right-click menu is built once with Loc() values captured at build time,
            // so rebuild it in the new language. (The sidebar menu is rebuilt on each open.)
            BuildContextMenu();

            // Toolbar captions are built with Loc() at apply time (they don't auto-update like a
            // DynamicResource), so rebuild the toolbar on every language change. Harmless for the
            // icon-only modes; refreshes the captions for Text-beside / Text-under / Text-only.
            ApplyToolbarAppearance();
        }

        // Native name (autonym) for each language, shown in the picker regardless of UI locale.
        private static string LangDisplayName(KillerPDF.Services.Locale loc) => loc switch
        {
            KillerPDF.Services.Locale.Es   => "Español",
            KillerPDF.Services.Locale.ZhTW => "中文 (繁體)",
            KillerPDF.Services.Locale.ZhCN => "中文 (简体)",
            KillerPDF.Services.Locale.Bn   => "বাংলা",
            KillerPDF.Services.Locale.TrTR => "Türkçe",
            _                              => "English",
        };

        private void ViewContinuousRadio_Checked(object sender, RoutedEventArgs e) => SelectViewMode(ViewMode.Continuous);
        private void ViewSingleRadio_Checked(object sender, RoutedEventArgs e)     => SelectViewMode(ViewMode.Single);
        private void ViewTwoPageRadio_Checked(object sender, RoutedEventArgs e)    => SelectViewMode(ViewMode.TwoPage);
        private void ViewGridRadio_Checked(object sender, RoutedEventArgs e)       => SelectViewMode(ViewMode.Grid);

        private void SelectViewMode(ViewMode mode)
        {
            SetViewMode(mode);
            if (ViewCurrentLabel is not null) ViewCurrentLabel.Text = ViewModeDisplayName(mode);
            // Leave the flyout and Settings panel open so the user can try view modes back to back.
        }

        private string ViewModeDisplayName(ViewMode mode) => mode switch
        {
            ViewMode.Single  => Loc("Str_View_Single"),
            ViewMode.TwoPage => Loc("Str_View_TwoPage"),
            ViewMode.Grid    => Loc("Str_View_Grid"),
            _                => Loc("Str_View_Continuous"),
        };

        // ── Toolbar appearance (display-mode picker) ──────────────────────
        // Icon size and whether captions show, picked as one exclusive mode. Hover tooltips stay on
        // in every mode, so the text modes are about preference, not discoverability.
        private enum ToolbarStyle { SmallIcons, LargeIcons, TextBeside, TextUnder, TextOnly }
        private ToolbarStyle _toolbarStyle = ToolbarStyle.SmallIcons;   // default for new installs

        // Each toolbar icon button paired with its glyph and label-resource key, built once so the
        // appearance can be rebuilt without re-walking the tree.
        private readonly List<(Button btn, string glyph, string labelKey)> _toolbarButtons = [];

        // Maps each toolbar glyph (Segoe MDL2 Assets code point) to its caption string key. Buttons
        // whose glyph isn't listed keep their icon with no caption.
        private static readonly Dictionary<string, string> _toolbarLabelKeys = new()
        {
            [""] = "Str_Lbl_New",      [""] = "Str_Lbl_Open",      [""] = "Str_Lbl_Close",
            [""] = "Str_Lbl_Save",     [""] = "Str_Lbl_Flatten",   [""] = "Str_Lbl_Print",
            [""] = "Str_Lbl_Merge",    [""] = "Str_Lbl_Extract",   [""] = "Str_Lbl_Delete",
            [""] = "Str_Lbl_MoveUp",   [""] = "Str_Lbl_MoveDown",
            [""] = "Str_Lbl_Select",   [""] = "Str_Lbl_Text",      [""] = "Str_Lbl_Highlight",
            [""] = "Str_Lbl_Strike",   [""] = "Str_Lbl_Underline", [""] = "Str_Lbl_Draw",
            [""] = "Str_Lbl_Crop",     [""] = "Str_Lbl_Image",     [""] = "Str_Lbl_Signature",
            [""] = "Str_Lbl_Undo",     [""] = "Str_Lbl_Clear",
            [""] = "Str_Lbl_ZoomOut",  [""] = "Str_Lbl_ZoomIn",
            [""] = "Str_Lbl_Highlight",   // current highlighter glyph (see ToolHighlightBtn)
        };

        // Walks LeftBar + RightBar once and records each icon button with its glyph + label key.
        private void IndexToolbarButtons()
        {
            _toolbarButtons.Clear();
            foreach (Panel? bar in new Panel?[] { LeftBar, RightBar })
            {
                if (bar is null) continue;
                foreach (var btn in DescendantButtons(bar))
                    if (btn.Content is string g && g.Length > 0 && _toolbarLabelKeys.TryGetValue(g, out var key))
                        _toolbarButtons.Add((btn, g, key));
            }
        }

        private static IEnumerable<Button> DescendantButtons(DependencyObject root)
        {
            foreach (var obj in LogicalTreeHelper.GetChildren(root))
            {
                if (obj is Button b) yield return b;
                if (obj is DependencyObject d)
                    foreach (var nested in DescendantButtons(d)) yield return nested;
            }
        }

        // Rebuilds one toolbar button's content and size for the current mode. withLabel=false forces
        // icon-only (used when Text-beside has to shed captions to fit a narrow window). Deliberately
        // never touches Foreground/Background, so theme accents, the dirty-save tint, and the
        // active-tool highlight survive (the caption TextBlocks inherit the button's foreground and
        // the template's drop shadow).
        private void SetToolbarButton(Button btn, string glyph, string key, bool withLabel)
        {
            var mode = _toolbarStyle;
            bool large = mode == ToolbarStyle.LargeIcons;
            bool beside = mode == ToolbarStyle.TextBeside;
            bool under = mode == ToolbarStyle.TextUnder;
            bool textOnly = mode == ToolbarStyle.TextOnly;
            double glyphSize = (large || under) ? 20 : (beside ? 16 : 14);
            btn.FontSize = glyphSize;

            // Text only: caption, no icon (nothing to shed - there'd be nothing left).
            if (textOnly)
            {
                btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 34; btn.Padding = new Thickness(8, 5, 8, 5);
                btn.Content = new TextBlock
                {
                    Text = Loc(key),
                    FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                return;
            }

            // Text beside the icon, while it still fits.
            if (beside && withLabel)
            {
                btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 34; btn.Padding = new Thickness(8, 5, 8, 5);
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = glyphSize,
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Children.Add(new TextBlock
                {
                    Text = Loc(key),
                    FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                    FontSize = 12,
                    Margin = new Thickness(7, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                btn.Content = row;
                return;
            }

            // Text under the icon: a large icon stacked over a small caption, while it still fits.
            if (under && withLabel)
            {
                btn.Width = double.NaN; btn.MinWidth = 0; btn.Height = 52; btn.Padding = new Thickness(6, 4, 6, 4);
                var col = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
                col.Children.Add(new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = glyphSize,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                col.Children.Add(new TextBlock
                {
                    Text = Loc(key),
                    FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                    FontSize = 10,
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                });
                btn.Content = col;
                return;
            }

            // Icon only: the icon modes, or Text-beside / Text-under after a caption was shed.
            btn.Width = (large || under) ? 46 : (beside ? 40 : 36);
            btn.MinWidth = 0;
            btn.Height = under ? 52 : (beside ? 34 : (large ? 42 : 32));
            btn.Padding = (beside || under) ? new Thickness(8, 5, 8, 5) : new Thickness(10, 6, 10, 6);
            btn.Content = glyph;
        }

        // Order in which Text-beside buttons shed their captions when the bar runs short of room:
        // lowest rank sheds first. Zoom and Select go first (their icons are obvious); the annotation
        // tools keep their captions longest because that is where the labels earn their space.
        private static int LabelStripRank(string key) => key switch
        {
            "Str_Lbl_ZoomOut" or "Str_Lbl_ZoomIn" => 0,
            "Str_Lbl_Select" => 1,
            "Str_Lbl_Undo" or "Str_Lbl_Clear" => 2,
            "Str_Lbl_New" or "Str_Lbl_Open" or "Str_Lbl_Close"
                or "Str_Lbl_Save" or "Str_Lbl_Flatten" or "Str_Lbl_Print" => 3,
            "Str_Lbl_MoveUp" or "Str_Lbl_MoveDown" or "Str_Lbl_Delete"
                or "Str_Lbl_Merge" or "Str_Lbl_Extract" => 4,
            _ => 5,   // annotation tools keep their labels longest
        };

        // Rebuilds every toolbar button for the current mode (captions on where applicable), then
        // lets ReflowToolbar shed captions and/or collapse groups to fit the current width.
        private void ApplyToolbarAppearance()
        {
            if (_toolbarButtons.Count == 0) return;
            foreach (var (btn, glyph, key) in _toolbarButtons)
                SetToolbarButton(btn, glyph, key, withLabel: true);
            // Open/Save are split buttons whose dropdown chevron overlaps the icon (-6) for the connected
            // split look in icon modes. With a caption the button widens, so the chevron must sit clear of
            // the text instead of over its last letter; the main half also drops its hover inset (only
            // needed when the chevron overlaps).
            bool textMode = _toolbarStyle is ToolbarStyle.TextBeside or ToolbarStyle.TextUnder or ToolbarStyle.TextOnly;
            var chevMargin = textMode ? new Thickness(1, 0, 0, 0) : new Thickness(-6, 0, 0, 0);
            if (OpenRecentBtn is not null) OpenRecentBtn.Margin = chevMargin;
            if (SaveMenuBtn   is not null) SaveMenuBtn.Margin   = chevMargin;
            if (OpenFileBtn is not null)
                OpenFileBtn.Style = (Style)FindResource(textMode ? "ToolbarButton" : "ToolbarSplitMain");
            if (SaveAsBtn is not null)
                SaveAsBtn.Style = (Style)FindResource(textMode ? "ToolbarButtonAccent" : "ToolbarSplitMainAccent");
            ReflowToolbar();
        }

        private void ToolbarSmallRadio_Checked(object sender, RoutedEventArgs e)  => SelectToolbarStyle(ToolbarStyle.SmallIcons);
        private void ToolbarLargeRadio_Checked(object sender, RoutedEventArgs e)  => SelectToolbarStyle(ToolbarStyle.LargeIcons);
        private void ToolbarBesideRadio_Checked(object sender, RoutedEventArgs e) => SelectToolbarStyle(ToolbarStyle.TextBeside);
        private void ToolbarUnderRadio_Checked(object sender, RoutedEventArgs e)  => SelectToolbarStyle(ToolbarStyle.TextUnder);
        private void ToolbarOnlyRadio_Checked(object sender, RoutedEventArgs e)   => SelectToolbarStyle(ToolbarStyle.TextOnly);

        private void SelectToolbarStyle(ToolbarStyle style)
        {
            _toolbarStyle = style;
            App.SetSetting("ToolbarStyle", style.ToString());
            if (ToolbarCurrentLabel is not null) ToolbarCurrentLabel.Text = ToolbarStyleName(style);
            ApplyToolbarAppearance();
            // Leave the flyout open so the user can compare modes without reopening it.
        }

        private string ToolbarStyleName(ToolbarStyle style) => style switch
        {
            ToolbarStyle.LargeIcons => Loc("Str_Toolbar_LargeIcons"),
            ToolbarStyle.TextBeside => Loc("Str_Toolbar_TextBeside"),
            ToolbarStyle.TextUnder  => Loc("Str_Toolbar_TextUnder"),
            ToolbarStyle.TextOnly   => Loc("Str_Toolbar_TextOnly"),
            _                       => Loc("Str_Toolbar_SmallIcons"),
        };

        // ── Responsive toolbar overflow ───────────────────────────────────
        private bool _reflowingToolbar;
        private void ToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ReflowToolbar();

        // Collapses lower-priority button groups into the overflow popup when the toolbar runs
        // out of room, and restores them when there is space again. Keeps the left/right layout.
        private void ReflowToolbar()
        {
            if (_reflowingToolbar || ToolbarGrid is null || LeftBar is null || RightContainer is null) return;
            _reflowingToolbar = true;
            try
            {
                // Order in which buttons move to the overflow menu as the bar narrows: FIRST entry
                // goes first. Lowest-value / most-redundant first - page move/delete and merge/extract
                // (all reachable from the sidebar right-click), then signature/image/crop, then
                // undo-clear, with the text-markup tools (draw, strike, underline, highlight, text)
                // kept on the bar the longest. Zoom, Select, and the file basics never collapse here;
                // they only shed their captions later (see LabelStripRank). Edit this list to retune.
                var order = new (UIElement bar, UIElement[] items)[]
                {
                    (GrpPageEdit,       new UIElement[] { MiDelete, MiMoveUp, MiMoveDown }),
                    (GrpPageOps,        new UIElement[] { MiMerge, MiExtract }),
                    (GrpSignature,      new UIElement[] { MiSignature }),
                    (ToolImageBtn,      new UIElement[] { MiImage }),
                    (ToolCropBtn,       new UIElement[] { MiCrop }),
                    (GrpUndo,           new UIElement[] { MiUndo, MiClear }),
                    (ToolDrawBtn,       new UIElement[] { MiDraw }),
                    (ToolStrikeBtn,     new UIElement[] { MiStrike }),
                    (ToolUnderlineBtn,  new UIElement[] { MiUnderline }),
                    (ToolHighlightBtn,  new UIElement[] { MiHighlight }),
                    (ToolTextBtn,       new UIElement[] { MiText }),
                };

                // Start fully expanded (everything in the bar, nothing in the popup).
                foreach (var (grp, items) in order)
                {
                    grp.Visibility = Visibility.Visible;
                    foreach (var it in items) it.Visibility = Visibility.Collapsed;
                }
                ToolbarGrid.UpdateLayout();

                double avail = ToolbarGrid.ActualWidth;

                // Text-beside / Text-under: each pass starts with ALL captions on, so widening the
                // window always restores them. Captions are only shed much later, as a last resort.
                bool textCaptions = _toolbarStyle is ToolbarStyle.TextBeside or ToolbarStyle.TextUnder;
                if (textCaptions && _toolbarButtons.Count > 0)
                {
                    foreach (var (btn, glyph, key) in _toolbarButtons)
                        SetToolbarButton(btn, glyph, key, withLabel: true);
                    ToolbarGrid.UpdateLayout();
                }

                // First defence against a narrow bar (and the long-standing behavior): collapse whole
                // low-priority groups into the overflow menu, KEEPING captions on whatever stays. This
                // is what runs at normal widths - captions stay, extras move to the chevron.
                if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width > avail)
                {
                    foreach (var (grp, items) in order)
                    {
                        grp.Visibility = Visibility.Collapsed;          // pull this group out of the bar
                        foreach (var it in items) it.Visibility = Visibility.Visible;  // ...into the popup
                        ToolbarGrid.UpdateLayout();
                        if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width + 30 <= avail) break;
                    }
                }

                // Last resort, ONLY at the ultra-narrow width where everything collapsible is already
                // in the overflow menu and the remaining captioned buttons still overlap: shed captions
                // to icon-only in priority order (zoom and Select first, annotation tools last). Until
                // this point the toolbar keeps its full captions, exactly as it looked before.
                if (textCaptions && LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width > avail)
                {
                    foreach (var (btn, glyph, key) in _toolbarButtons.OrderBy(x => LabelStripRank(x.labelKey)))
                    {
                        if (LeftBar.DesiredSize.Width + RightContainer.DesiredSize.Width <= avail) break;
                        if (!btn.IsVisible) continue;   // already collapsed into the overflow menu
                        SetToolbarButton(btn, glyph, key, withLabel: false);
                        ToolbarGrid.UpdateLayout();
                    }
                }

                bool anyCollapsed = order.Any(o => o.bar.Visibility != Visibility.Visible);
                OverflowChevron.Visibility = anyCollapsed ? Visibility.Visible : Visibility.Collapsed;
                if (!anyCollapsed) OverflowChevron.IsChecked = false;
            }
            finally { _reflowingToolbar = false; }
        }

        private void OverflowItem_Click(object sender, RoutedEventArgs e)
        {
            OverflowChevron.IsChecked = false;   // close the flyout after a choice is made
        }

        private const int  WM_GETMINMAXINFO   = 0x0024;
        private const int  WM_DPICHANGED      = 0x02E0;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const uint SWP_NOZORDER       = 0x0004;
        private const uint SWP_NOACTIVATE     = 0x0010;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                WmGetMinMaxInfo(hwnd, lParam);
                handled = true;
            }
            else if (msg == WM_DPICHANGED)
            {
                // Apply Windows' suggested rect so the window's apparent size is preserved
                // on the new monitor. handled stays false so WPF's HwndSource also processes
                // the message — updating its internal DPI scale and firing Window.DpiChanged.
                var r = Marshal.PtrToStructure<RECT>(lParam);
                SetWindowPos(hwnd, IntPtr.Zero, r.left, r.top,
                             r.right - r.left, r.bottom - r.top,
                             SWP_NOZORDER | SWP_NOACTIVATE);
                // Re-render at the new DPI. DispatcherPriority.Loaded fires after WPF has
                // finished its own DPI update, so VisualTreeHelper.GetDpi already reflects
                // the new scale factor when RenderPage calls it.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() =>
                    {
                        if (_doc is null) return;
                        if (_viewMode == ViewMode.Grid)
                        {
                            // Grid's primary tile (and the page-width basis the column math uses) is
                            // ALWAYS page 0 - rendering the selected page here would corrupt that basis
                            // and could collapse the grid to one column. Re-render page 0, then re-fit the
                            // columns to the new DPI/size so the grid is preserved across the monitor move.
                            RenderPage(0);
                            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                                (Action)ReapplyGridOrFit);
                            return;
                        }
                        int idx = PageList.SelectedIndex;
                        if (idx >= 0) RenderPage(idx);
                    }));
            }
            else if (msg == WM_NCHITTEST && WindowState == WindowState.Normal)
            {
                int ht = WmNcHitTest(hwnd, lParam);
                if (ht != 0) { handled = true; return new IntPtr(ht); }
            }
            return IntPtr.Zero;
        }

        private int WmNcHitTest(IntPtr hwnd, IntPtr lParam)
        {
            // lParam is screen coords: lo-word = X, hi-word = Y.
            // Cast through short to preserve sign (handles negative coords on left/above primary monitor).
            long lp  = lParam.ToInt64();
            int  mx  = unchecked((short)(lp & 0xFFFF));
            int  my  = unchecked((short)((lp >> 16) & 0xFFFF));

            if (!GetWindowRect(hwnd, out RECT rc)) return 0;

            bool onLeft   = mx < rc.left   + ResizeBorder;
            bool onRight  = mx >= rc.right  - ResizeBorder;
            bool onTop    = my < rc.top    + ResizeBorder;
            bool onBottom = my >= rc.bottom - ResizeBorder;

            // Never hijack a scrollbar for window resizing. The vertical scrollbar sits flush
            // against the window's right edge, so the resize border used to swallow it - the
            // cursor showed the resize arrow and dragging resized the window instead of moving
            // the thumb. If a ScrollBar is under the cursor, report client area so it stays
            // grabbable (Issue #75 follow-up).
            if ((onLeft || onRight || onTop || onBottom) && IsOverScrollBar(mx, my))
                return HTCLIENT;

            if (onTop    && onLeft)  return HTTOPLEFT;
            if (onTop    && onRight) return HTTOPRIGHT;
            if (onBottom && onLeft)  return HTBOTTOMLEFT;
            if (onBottom && onRight) return HTBOTTOMRIGHT;
            if (onLeft)              return HTLEFT;
            if (onRight)             return HTRIGHT;
            if (onTop)               return HTTOP;
            if (onBottom)            return HTBOTTOM;

            return 0;
        }

        // Hit-tests the visual tree at a screen point (physical pixels from WM_NCHITTEST)
        // and reports whether a ScrollBar sits under the cursor.
        private bool IsOverScrollBar(int screenX, int screenY)
        {
            try
            {
                var pt  = PointFromScreen(new Point(screenX, screenY));
                var res = VisualTreeHelper.HitTest(this, pt);
                DependencyObject? hit = res?.VisualHit;
                while (hit != null)
                {
                    if (hit is System.Windows.Controls.Primitives.ScrollBar) return true;
                    hit = VisualTreeHelper.GetParent(hit);
                }
            }
            catch { /* best-effort; fall through to normal resize handling */ }
            return false;
        }

        private void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                GetMonitorInfo(monitor, ref info);
                RECT work = info.rcWork;
                RECT mon = info.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(work.left - mon.left);
                mmi.ptMaxPosition.y = Math.Abs(work.top - mon.top);
                mmi.ptMaxSize.x = Math.Abs(work.right - work.left);
                mmi.ptMaxSize.y = Math.Abs(work.bottom - work.top);
                mmi.ptMaxTrackSize.x = mmi.ptMaxSize.x;
                mmi.ptMaxTrackSize.y = mmi.ptMaxSize.y;
                // Enforce the window's MinWidth/MinHeight during user resize. The custom chrome
                // marks WM_GETMINMAXINFO handled, so WPF's own minimum enforcement is bypassed.
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    if (MinWidth  > 0 && !double.IsInfinity(MinWidth))  mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth  * dpi.DpiScaleX);
                    if (MinHeight > 0 && !double.IsInfinity(MinHeight)) mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);
                }
                catch { /* DPI not available yet; skip min enforcement for this pass */ }
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCHITTEST     = 0x0084;
        private const int HTCLIENT         = 1;
        private const int HTCAPTION        = 2;
        private const int HTLEFT           = 10;
        private const int HTRIGHT          = 11;
        private const int HTTOP            = 12;
        private const int HTTOPLEFT        = 13;
        private const int HTTOPRIGHT       = 14;
        private const int HTBOTTOM         = 15;
        private const int HTBOTTOMLEFT     = 16;
        private const int HTBOTTOMRIGHT    = 17;
        private const int ResizeBorder     = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ============================================================
        // Window chrome
        // ============================================================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                MaximizeBtn_Click(sender, e);
                return;
            }
            // Delegate drag to Windows via WM_NCLBUTTONDOWN(HTCAPTION).
            // This gives native restore-from-maximized-and-drag behavior:
            // if the window is maximized, Windows restores it and follows the cursor
            // exactly as a native title bar would.
            e.Handled = true;
            var hwnd = new WindowInteropHelper(this).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            var res = KillerDialog.Show(this,
                Loc("Str_Dlg_InstallMsg"),
                Loc("Str_Dlg_InstallTitle"), MessageBoxButton.OKCancel);
            if (res != MessageBoxResult.OK) return;

            // Hide the badge immediately so it doesn't flash if relaunch is slow
            _portableBadge.Visibility = Visibility.Collapsed;

            App.InstallAndRelaunch(_currentFile, wantDesktop: true);
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // Rounded window corners look right only when floating; a maximized OR snapped window must
        // square off or the rounded corners reveal the desktop / adjacent window behind them.
        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            UpdateWindowChrome();
            RepositionAnnotationBars();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            UpdateWindowChrome();
            KeepSettingsPanelInWindow();
            RepositionAnnotationBars();
        }

        // Keeps the (draggable) Settings panel fully inside the window when the window is resized,
        // so shrinking the window can't leave it clipped or stranded off-edge.
        private void KeepSettingsPanelInWindow()
        {
            if (SettingsOverlay is null || SettingsPanel is null) return;
            if (SettingsOverlay.Visibility != Visibility.Visible) return;
            PositionSettingsPanel();   // re-anchor bottom-left (handles sidebar collapse / resize)
        }

        // Re-applies the saved placement to every visible annotation bar. Called synchronously from the
        // same window events that keep the Settings panel in-window (resize, maximize/restore, move), so
        // the bar tracks its anchored edge and stays fully on-screen through all of them.
        private void RepositionAnnotationBars()
        {
            if (PagePreviewPanel?.Parent is not Grid area) return;
            foreach (var bar in new[] { _drawSettingsBar, _textSettingsBar })
                if (bar is not null && bar.Visibility == Visibility.Visible)
                    PositionAnnotationBar(bar, area);
        }

        // Anchors a bar to whichever edge it sits nearer and clamps it fully inside the document area:
        // the gap from the anchored edge is honoured when there's room, otherwise reduced so the bar
        // never crosses the opposite edge. No-op until the bar has a measured width (PlaceAnnotationBar's
        // deferred pass positions it once laid out).
        private void PositionAnnotationBar(Border bar, Grid area)
        {
            double w = bar.ActualWidth;
            if (w <= 0) return;
            // The document's vertical scrollbar lives on the right edge of the area. Keep the bar clear
            // of it when it's showing; when it isn't, the bar can use the full edge.
            double sb = VerticalScrollBarInset();
            double maxLeft = Math.Max(0, area.ActualWidth - w);
            if (_annotBarCenterFrac is double frac)
            {
                // Parked away from both edges: keep the same fraction of the width so it scales smoothly
                // with the window instead of lurching toward an edge. Clamp so it never slides under the
                // scrollbar on the right.
                double maxLeftCentered = Math.Max(0, maxLeft - sb);
                double left = Math.Max(0, Math.Min(maxLeftCentered, frac * area.ActualWidth - w / 2));
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                bar.Margin = new Thickness(left, bar.Margin.Top, 0, 0);
                SetBarDockedBorder(bar, dockedLeft: false, dockedRight: false);
            }
            else if (_annotBarAnchorRight)
            {
                // Sit the bar against the scrollbar's left edge when it's present (gap + scrollbar width),
                // otherwise honour the plain gap right up to the pane edge.
                double g = Math.Min(maxLeft, (_annotBarGap ?? 8) + sb);
                bar.HorizontalAlignment = HorizontalAlignment.Right;
                bar.Margin = new Thickness(0, bar.Margin.Top, g, 0);
                // Only merge with the pane's edge line when nothing (no scrollbar) sits between them.
                SetBarDockedBorder(bar, dockedLeft: false, dockedRight: sb <= 0 && g <= 0.5);
            }
            else
            {
                double g = Math.Min(maxLeft, _annotBarGap ?? 8);
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                bar.Margin = new Thickness(g, bar.Margin.Top, 0, 0);
                SetBarDockedBorder(bar, dockedLeft: g <= 0.5, dockedRight: false);
            }
        }

        // Width reserved by the document pane's vertical scrollbar (matches the ScrollBar style's fixed
        // 12px in MainWindow.xaml). Zero when the scrollbar isn't currently shown, so a docked bar can
        // reach the pane edge; otherwise the bar stops at the scrollbar's left edge.
        private const double DocScrollBarWidth = 12;
        private double VerticalScrollBarInset() =>
            PagePreviewPanel?.ComputedVerticalScrollBarVisibility == Visibility.Visible ? DocScrollBarWidth : 0;

        // When the bar is docked flush against a side, drop its own 1px border on that side and swap it
        // for 1px of padding. The document pane's border (same brush) then serves as the single shared
        // edge line - no 2px double border, and no size or position change (so nothing jumps).
        private static void SetBarDockedBorder(Border bar, bool dockedLeft, bool dockedRight)
        {
            bar.BorderThickness = new Thickness(dockedLeft ? 0 : 1, 0, dockedRight ? 0 : 1, 1);
            bar.Padding = new Thickness(dockedLeft ? 5 : 4, 4, dockedRight ? 5 : 4, 4);
        }

        // Snapping changes the window's position/size but NOT its WindowState (it stays Normal), so
        // re-evaluate the chrome on move too - otherwise a window snapped to a screen half keeps its
        // rounded corners. (Hooked once in the constructor.)
        private void OnWindowLocationChanged(object? sender, EventArgs e)
        {
            UpdateWindowChrome();
            KeepSettingsPanelInWindow();
            RepositionAnnotationBars();
        }

        // Applies corner rounding, the frame border, and the content clip for the current window
        // layout: floating = rounded; maximized or snapped = squared.
        private void UpdateWindowChrome()
        {
            bool max     = WindowState == WindowState.Maximized;
            bool squared = max || IsSnapped();
            int  radius  = squared ? 0 : 7;
            if (RootBorder != null)     RootBorder.CornerRadius     = new CornerRadius(radius);
            if (TitleBarBorder != null) TitleBarBorder.CornerRadius = new CornerRadius(radius, radius, 0, 0);
            if (FooterBorder != null)   FooterBorder.CornerRadius   = new CornerRadius(0, 0, radius, radius);
            // Close button's hover fill follows the same top-right corner as the title bar.
            Resources["ChromeCloseCorner"] = new CornerRadius(0, radius, 0, 0);
            // Only a maximized window drops the 1px floating frame (it's flush to every screen edge).
            // A snapped window keeps the border so it still reads against the window beside it.
            if (RootBorder != null)     RootBorder.BorderThickness  = new Thickness(max ? 0 : 1);
            UpdateRootClip(squared);
        }

        // Clips all window content to the rounded corners. A Border's CornerRadius rounds only its
        // own background/border, not its children, so without this the title bar, footer, and grain
        // paint square corners over the rounded frame. Squared off when maximized or snapped.
        private void UpdateRootClip(bool squared)
        {
            if (RootClipGrid is null) return;
            if (squared)
            {
                RootClipGrid.Clip = null;
                return;
            }
            RootClipGrid.Clip = new RectangleGeometry(
                new Rect(0, 0, RootClipGrid.ActualWidth, RootClipGrid.ActualHeight), 6, 6);
        }

        // True when the window is Aero-Snapped (half/quarter screen). Snapping leaves WindowState
        // == Normal, so it's detected by comparing the window rect to the monitor work area: a
        // snapped window is flush to a work-area edge and smaller than the full work area.
        private bool IsSnapped()
        {
            if (WindowState != WindowState.Normal) return false;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT w)) return false;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref info)) return false;
            RECT a = info.rcWork;

            const int tol = 2; // device-pixel tolerance for "flush to edge"
            bool flushLeft   = Math.Abs(w.left   - a.left)   <= tol;
            bool flushRight  = Math.Abs(w.right  - a.right)  <= tol;
            bool flushTop    = Math.Abs(w.top    - a.top)    <= tol;
            bool flushBottom = Math.Abs(w.bottom - a.bottom) <= tol;
            bool fillsWidth  = Math.Abs((w.right - w.left) - (a.right - a.left)) <= tol;
            bool fillsHeight = Math.Abs((w.bottom - w.top) - (a.bottom - a.top)) <= tol;

            // Exactly the work area (sized full but not maximized) is not a snap.
            if (fillsWidth && fillsHeight) return false;
            // Left/right half: full height, flush to one vertical edge, narrower than the work area.
            if (flushTop && flushBottom && (flushLeft || flushRight) && !fillsWidth) return true;
            // Quarter snap: flush into a corner and smaller than the work area in at least one axis.
            if ((flushLeft || flushRight) && (flushTop || flushBottom) && (!fillsWidth || !fillsHeight))
                return true;
            return false;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Fold the live (active-tab) dirty flag back into its session, then prompt once if
            // any open tab has unsaved changes.
            if (_active != null) CaptureSessionState(_active);
            bool anyDirty = _isDirty || _sessions.Any(s => s.IsDirty);
            if (anyDirty)
            {
                var res = KillerDialog.Show(this,
                    Loc("Str_Dlg_UnsavedExit"),
                    Loc("Str_Dlg_AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            SaveWindowSettings();
            base.OnClosing(e);
        }

        // ============================================================
        // Settings persistence (window size, zoom, last file)
        // ============================================================

        private void SaveWindowSettings()
        {
            try
            {
                App.SetSetting("WindowState", WindowState.ToString());
                if (WindowState == WindowState.Normal)
                {
                    App.SetSetting("WindowWidth",  ((int)ActualWidth).ToString());
                    App.SetSetting("WindowHeight", ((int)ActualHeight).ToString());
                    App.SetSetting("WindowTop",  ((int)Top).ToString());
                    App.SetSetting("WindowLeft", ((int)Left).ToString());
                }
                App.SetSetting("FitMode",   _fitMode.ToString());
                App.SetSetting("ZoomLevel", _zoomLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (_currentFile is not null)
                    App.SetSetting("LastFile", _currentFile);
                else
                    App.RemoveSetting("LastFile");
                // Remember every open tab so the whole session restores next launch. Manually-closed
                // tabs are already gone from _sessions, so they won't come back (Issue #75 still holds).
                var openFiles = _sessions
                    .Select(ss => ss.OriginalFile)
                    .Where(f => !string.IsNullOrEmpty(f) && System.IO.File.Exists(f))
                    .Distinct()
                    .ToList();
                if (openFiles.Count > 0)
                    App.SetSetting("OpenTabs", string.Join("|", openFiles!));
                else
                    App.RemoveSetting("OpenTabs");
                if (_active?.OriginalFile is { Length: > 0 } af && System.IO.File.Exists(af))
                    App.SetSetting("ActiveTab", af);
                else
                    App.RemoveSetting("ActiveTab");
            }
            catch { /* best-effort */ }
        }

        private void RestoreWindowSettings()
        {
            try
            {
                if (int.TryParse(App.GetSetting("WindowWidth"),  out int w) &&
                    int.TryParse(App.GetSetting("WindowHeight"), out int h) && w > 200 && h > 200)
                {
                    Width  = w;
                    Height = h;
                }
                if (int.TryParse(App.GetSetting("WindowTop"),  out int savedTop) &&
                    int.TryParse(App.GetSetting("WindowLeft"), out int savedLeft))
                {
                    // Verify the saved position is visible on the virtual desktop
                    // (covers all monitors). Falls back to CenterScreen (XAML default)
                    // if the monitor it was on is no longer connected.
                    double vLeft   = SystemParameters.VirtualScreenLeft;
                    double vTop    = SystemParameters.VirtualScreenTop;
                    double vRight  = vLeft + SystemParameters.VirtualScreenWidth;
                    double vBottom = vTop  + SystemParameters.VirtualScreenHeight;
                    bool onScreen  = savedLeft + 100 < vRight  && savedLeft + Width  > vLeft
                                  && savedTop  + 50  < vBottom && savedTop  + Height > vTop;
                    if (onScreen)
                    {
                        Left = savedLeft;
                        Top  = savedTop;
                    }
                }
                if (Enum.TryParse<WindowState>(App.GetSetting("WindowState"), out var ws) &&
                    ws == WindowState.Maximized)
                {
                    WindowState = WindowState.Maximized;
                }
                if (double.TryParse(App.GetSetting("ZoomLevel"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double z) && z > 0)
                {
                    _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, z));
                }
                if (Enum.TryParse<FitMode>(App.GetSetting("FitMode"), out var fm))
                    _fitMode = fm;
            }
            catch { /* best-effort */ }
        }

        // ============================================================
        // Context menu
        // ============================================================

        private void ApplyGrainTexture()
        {
            // Film grain with a mix of bright AND dark specks so the texture reads
            // on any background - bright specks show on dark themes, dark specks
            // show on light themes. Denser and a touch stronger than the first pass.
            const int size = 256;
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4]; // start fully transparent
            var rng = new Random(1337);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (rng.Next(3) != 0) continue;       // ~33% pixel density
                bool bright = rng.Next(2) == 0;        // half bright, half dark
                byte v = bright ? (byte)rng.Next(190, 255) : (byte)rng.Next(0, 50);
                byte a = (byte)rng.Next(35, 95);       // alpha for subtlety
                pixels[i]     = v;
                pixels[i + 1] = v;
                pixels[i + 2] = v;
                pixels[i + 3] = a;
            }
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            GrainBrush.ImageSource = bmp;
            if (SidebarGrainBrush != null) SidebarGrainBrush.ImageSource = bmp;
            if (ToggleGrainBrush != null) ToggleGrainBrush.ImageSource = bmp;
            if (Resources["GrainBrushShared"] is ImageBrush sharedGrain) sharedGrain.ImageSource = bmp;
        }

        /// <summary>Generated film-grain tile, exposed so secondary windows (e.g. the
        /// print preview) can paint the same texture over their document area.</summary>
        public ImageSource? GrainTexture => GrainBrush?.ImageSource;

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);

            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_CopyText"), (s, e) => CopySelectedText(), "Ctrl+C"));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_Print"), (s, e) => Print_Click(s!, e), "Ctrl+P"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_SelectTool"), (s, e) => SetTool(EditTool.Select)));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_TextTool"), (s, e) => SetTool(EditTool.Text)));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_HighlightTool"), (s, e) => SetTool(EditTool.Highlight)));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DrawTool"), (s, e) => SetTool(EditTool.Draw)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RotateCW"),  (s, e) => RotatePages_Click(90)));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RotateCCW"), (s, e) => RotatePages_Click(-90)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_StampNumbers"), (s, e) => StampPageNumbers()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DeleteSel"), (s, e) => DeleteSelected(), "Delete"));
            // Unpair a selected text/cover pair (shown only when a paired item is selected).
            _ctxUnpairItem = MakeMenuItem("Unpair text + cover", (s, e) => UnpairSelected());
            menu.Items.Add(_ctxUnpairItem);
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_UndoLast"), (s, e) => Undo_Click(s!, e), "Ctrl+Z"));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_ClearPage"), (s, e) => ClearAnnotations_Click(s!, e)));

            // Toggle context-dependent items each time the menu opens (works for both the auto right-click
            // on the primary canvas and the per-tile overlays that open this same menu programmatically).
            menu.Opened += (s, e) =>
            {
                if (_ctxUnpairItem is not null)
                    _ctxUnpairItem.Visibility = SelectedPaired() is not null ? Visibility.Visible : Visibility.Collapsed;
            };

            _annotationCanvas.ContextMenu = menu;
        }

        private MenuItem? _ctxUnpairItem;

        // The selected annotation (primary or multi-select) that belongs to a text/cover pair, if any.
        private PageAnnotation? SelectedPaired()
        {
            if (_selectedAnnotation is not null && _selectedAnnotation.PairId.Length > 0) return _selectedAnnotation;
            foreach (var a in _selectedSet) if (a.PairId.Length > 0) return a;
            return null;
        }

        // Break a text/cover pairing: clear the shared id on both halves so they become independent (the
        // cover stops rendering dashed and no longer shows when its former partner is selected).
        private void UnpairSelected()
        {
            var a = SelectedPaired();
            if (a is null) return;
            string pid = a.PairId;
            int pg = a.PageIndex;
            if (_annotations.TryGetValue(pg, out var list))
                foreach (var x in list) if (x.PairId == pid) x.PairId = "";
            if (_pairedCoverOutline is not null)
            {
                (_pairedCoverOutline.Parent as Canvas)?.Children.Remove(_pairedCoverOutline);
                _pairedCoverOutline = null;
            }
            RenderAllAnnotations(pg);
            ReattachSelectionVisuals();   // keep the current box's selection chrome after the repaint
            MarkDirty();
            SetStatus("Unpaired - the text and cover are now separate");
        }

        private void PageList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_InsertBlank"), (s, ev) => InsertBlankPage_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RotateCWShort"),  (s, ev) => RotatePages_Click(90)));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_RotateCCWShort"), (s, ev) => RotatePages_Click(-90)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_MoveUp"),   (s, ev) => MoveUp_Click(s!, ev)));
            menu.Items.Add(MakeMenuItem(Loc("Str_Lbl_MoveDown"), (s, ev) => MoveDown_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_ExtractPages"), (s, ev) => Split_Click(s!, ev)));
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_DeletePages"), (s, ev) => Delete_Click(s!, ev)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem(Loc("Str_Ctx_StampNumbers"), (s, ev) => StampPageNumbers()));
            menu.PlacementTarget = PageList;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void RotatePages_Click(int delta)
        {
            if (_doc is null) return;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                foreach (var idx in indices)
                    _doc.Pages[idx].Rotate = ((_doc.Pages[idx].Rotate + delta) % 360 + 360) % 360;
                int restoreIdx = PageList.SelectedIndex;
                SaveTempAndReload();
                PageList.SelectedIndex = Math.Min(restoreIdx, PageList.Items.Count - 1);
                // After a rotation the page aspect ratio changes; always fit-to-page so the
                // full rotated page is visible regardless of the previous zoom level.
                FitToPage();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() => FitToPage()));
                SetStatus(string.Format(Loc("Str_Rotated"), indices.Count));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_RotateFailed"), ex.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static MenuItem MakeMenuItem(string header, RoutedEventHandler click, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            if (gesture != null)
                item.InputGestureText = gesture;
            return item;
        }

        // ============================================================
        // File operations
        // ============================================================

        // True while an open is finishing on a background thread (encryption strip / repair). The
        // synchronous open callers check this so they don't treat the not-yet-loaded _doc as a failure;
        // the background path finalizes the tab itself via FinalizeAsyncOpen.
        private bool _asyncOpenPending;

        private void OpenFile(string path)
        {
            // Record real user files in the recent list (skips blank/new docs, which don't open a path).
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) App.AddRecentFile(path);

            // Files on UNC / network shares - notably the WSL \\wsl$ 9P filesystem - can hand
            // back partial reads, making the PDF parser see a truncated file ("Unexpected EOF").
            // Copy such files to a local temp via File.ReadAllBytes (which reads to EOF) and open
            // from there. `path` stays the user's real path for display and Save.
            string srcPath = path;
            if (IsNetworkPath(path))
            {
                try
                {
                    var localCopy = App.MakeTempFile("netopen");
                    File.WriteAllBytes(localCopy, File.ReadAllBytes(path));
                    srcPath = localCopy;
                }
                catch { srcPath = path; }
            }

            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.Modify);
                // PdfSharp cannot save modified encrypted PDFs — it copies unmodified encrypted
                // stream bytes verbatim but fails when it has to re-serialize a dirty object.
                // Strip encryption silently at open time via Import so all edits work correctly.
                if (PdfFileHasEncryption(srcPath))
                {
                    // PdfSharp can read encrypted PDFs but cannot re-save them once modified, so the
                    // encryption is stripped (PDFium, lossless; Import fallback). That strip is CPU-heavy,
                    // so it runs off-thread behind the busy overlay instead of freezing the window. The
                    // background path finalizes the tab itself, so the flag tells the synchronous caller
                    // not to treat the not-yet-set _doc as a failed open.
                    _asyncOpenPending = true;
                    StripEncryptionAndOpen(srcPath, path, busyMessage: "Opening protected PDF...");
                    return;
                }
                _currentFile = srcPath;
                FinishOpenFile(path, srcPath);
            }
            catch (Exception ex) when (IsOwnerPasswordException(ex))
            {
                // PDF has owner/permissions restrictions but no open password —
                // open read-only so the user can still view and print it.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnly"), System.IO.Path.GetFileName(path), _doc.PageCount));
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (IsPasswordException(ex))
            {
                string? pw = PromptForPassword(path);
                if (pw is null) return;
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, pw, PdfDocumentOpenMode.Modify);
                    // Save a decrypted temp copy so Docnet can render without needing the password
                    var tempDec = App.MakeTempFile("dec");
                    _doc.Save(tempDec);
                    _doc.Close();
                    _doc = PdfReader.Open(tempDec, PdfDocumentOpenMode.Modify);
                    _currentFile = tempDec;
                    FinishOpenFile(path, tempDec);
                }
                catch (Exception ex2)
                {
                    KillerDialog.Show(this, string.Format(Loc("Str_Dlg_FailedOpen"), ex2.Message), Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex) when (IsXRefException(ex))
            {
                // Some PDFs have malformed or non-standard XRef tables that PdfSharp can't
                // open in Modify mode. Fall back to ReadOnly; if that also fails, offer repair.
                try
                {
                    if (_doc is not null) { _doc.Close(); _doc = null; }
                    _doc = PdfReader.Open(srcPath, PdfDocumentOpenMode.ReadOnly);
                    _currentFile = srcPath;
                    FinishOpenFile(path, srcPath);
                    SetStatus(string.Format(Loc("Str_OpenedReadOnlyXRef"), System.IO.Path.GetFileName(path), _doc.PageCount));
                    KillerDialog.Show(this,
                        $"\"{System.IO.Path.GetFileName(path)}\" has a non-standard structure and was opened read-only.\n\nEditing, saving, and some other features may not work correctly.",
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch
                {
                    // ReadOnly also failed — offer to repair.
                    var result = KillerDialog.Show(this,
                        $"This PDF has a damaged structure and couldn't be opened.\n\nWould you like KillerPDF to attempt a repair? A repaired copy will be created - the original file will not be changed.\n\nNote: repaired files may be missing bookmarks, forms, and other interactive features.",
                        "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                        TryRepairAndOpen(srcPath);
                }
            }
            catch (Exception ex) when (IsEofParseException(ex))
            {
                // PdfSharpCore rejects some structurally-valid PDFs with "Unexpected EOF" even though
                // PDFium (and every common viewer) reads them fine. Re-save losslessly through PDFium on
                // a background thread (so the window doesn't freeze). The recovered copy is content-
                // equivalent, so it opens clean without nagging to save (markDirty: false).
                _asyncOpenPending = true;
                StripEncryptionAndOpen(srcPath, path, markDirty: false);
            }
            catch (Exception)
            {
                // Any other open failure (truncated file, malformed objects, an out-of-range parse, etc.):
                // we can't classify the damage, but the PDFium-based repair often recovers it anyway, so
                // offer the repair rather than just failing outright.
                var result = KillerDialog.Show(this,
                    "This PDF couldn't be opened - its structure may be damaged.\n\nWould you like KillerPDF to attempt a repair? A repaired copy will be created - the original file will not be changed.\n\nNote: repaired files may be missing bookmarks, forms, and other interactive features.",
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Yes)
                    TryRepairAndOpen(srcPath);   // sets _asyncOpenPending and finalizes the tab itself
            }
        }

        // PdfSharpCore throws on some structurally-valid PDFs that PDFium opens fine - most
        // often "Unexpected EOF" from SharpZipLib's Flate inflater while reading a FlateDecode
        // cross-reference stream (multi-revision PDFs with incremental updates / dangling xref
        // entries that tolerant parsers ignore). Match by message AND exception type across the
        // whole inner-exception chain so a wrapped SharpZipBaseException is still recovered.
        private static bool IsEofParseException(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                string msg  = e.Message ?? string.Empty;
                string type = e.GetType().FullName ?? string.Empty;
                if (msg.IndexOf("EOF", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("end of file", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("Inflater", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("FlateDecode", StringComparison.OrdinalIgnoreCase) >= 0
                    || type.IndexOf("SharpZip", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsXRefException(Exception ex) =>
            ex.Message.IndexOf("XRef", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("cross-reference", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Invalid PDF file", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("startxref", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("Unexpected token", StringComparison.OrdinalIgnoreCase) >= 0;

        // True for UNC paths (\\server\share, \\wsl$\..., \\wsl.localhost\...) and mapped
        // network drives. Such files are copied locally before opening to avoid 9P short reads.
        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            try
            {
                var root = System.IO.Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && root!.Length >= 2 && root[1] == ':')
                    return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Imports pages from <paramref name="sourcePath"/> into a fresh PdfDocument and saves it
        /// to <paramref name="destPath"/>. Returns true on success, false on failure.
        /// Unlike TryRepairAndOpen this has no UI side-effects and can be used mid-operation.
        /// </summary>
        /// <param name="stripRotations">
        // ── PDFium P/Invoke ──────────────────────────────────────────────────────────
        // PDFium (pdfium.dll) is already shipped with Docnet. We use it here to strip
        // encryption from PDFs that PdfSharpCore can read but cannot re-save when modified.

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadDocument(
            [MarshalAs(UnmanagedType.LPStr)] string filePath,
            [MarshalAs(UnmanagedType.LPStr)] string? password);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_CloseDocument(IntPtr document);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDF_SaveWithVersion(
            IntPtr document, ref FPDF_FILEWRITE fileWrite, uint flags, int fileVersion);

        [StructLayout(LayoutKind.Sequential)]
        private struct FPDF_FILEWRITE
        {
            public int version;          // must be 1
            public IntPtr WriteBlock;    // cdecl: int WriteBlock(FPDF_FILEWRITE*, const void*, unsigned long)
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PdfWriteBlockDelegate(IntPtr pThis, IntPtr pData, uint size);

        private const uint FPDF_REMOVE_SECURITY = 3;

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int FPDF_GetPageCount(IntPtr document);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr FPDF_LoadPage(IntPtr document, int page_index);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDF_ClosePage(IntPtr page);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void FPDFPage_SetRotation(IntPtr page, int rotation);

        [DllImport("pdfium.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool FPDFPage_GenerateContent(IntPtr page);

        /// <summary>
        /// Returns true if the PDF file has an /Encrypt entry in its trailer.
        /// Scans the last 2 KB so it's fast; works regardless of how PdfSharp
        /// reports security state after authenticating with an empty password.
        /// </summary>
        private static bool PdfFileHasEncryption(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                long scan = Math.Min(2048, fs.Length);
                fs.Seek(-scan, SeekOrigin.End);
                var buf = new byte[scan];
                _ = fs.Read(buf, 0, buf.Length);
                // Look for /Encrypt in the raw bytes (Latin-1 safe)
                var text = System.Text.Encoding.GetEncoding(1252).GetString(buf);
                return text.Contains("/Encrypt");
            }
            catch { return false; }
        }

        /// <summary>
        /// Uses PDFium to save a copy of <paramref name="sourcePath"/> with all security/encryption
        /// removed. Returns true on success. Falls back gracefully if PDFium is unavailable.
        /// PDFium is already initialised by Docnet; no separate init call is needed.
        /// </summary>
        private static bool TryPdfiumStripEncryption(string sourcePath, string destPath)
        {
            try
            {
                // Ensure PDFium is initialised — Docnet does this lazily on first use,
                // so force it now before we call PDFium P/Invoke directly.
                try { _ = DocLib.Instance; } catch { }

                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero) return false;
                try
                {
                    using var ms = new MemoryStream();
                    PdfWriteBlockDelegate cb = (_, pData, size) =>
                    {
                        var buf = new byte[size];
                        Marshal.Copy(pData, buf, 0, (int)size);
                        ms.Write(buf, 0, (int)size);
                        return 1;
                    };
                    var gch = GCHandle.Alloc(cb);
                    try
                    {
                        var fw = new FPDF_FILEWRITE
                        {
                            version = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(cb)
                        };
                        if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                            return false;
                    }
                    finally { gch.Free(); }
                    File.WriteAllBytes(destPath, ms.ToArray());
                    return true;
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch { return false; }
        }

        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Uses PDFium to load <paramref name="sourcePath"/>, zero-out all page /Rotate values,
        /// strip encryption, and save to <paramref name="destPath"/>. Returns true on success.
        /// Called from SaveTempAndReload's xref-error fallback — PDFium is guaranteed to be
        /// initialised by then because the page preview has already rendered via Docnet.
        /// </summary>
        private static bool TryPdfiumSaveWithZeroRotations(string sourcePath, string destPath)
        {
            try
            {
                var doc = FPDF_LoadDocument(sourcePath, null);
                if (doc == IntPtr.Zero)
                {
                    try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "killerpdf_pdfium_debug.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FPDF_LoadDocument returned null for: {sourcePath}\n\n"); } catch { }
                    return false;
                }
                try
                {
                    int pageCount = FPDF_GetPageCount(doc);
                    for (int i = 0; i < pageCount; i++)
                    {
                        var page = FPDF_LoadPage(doc, i);
                        if (page == IntPtr.Zero) continue;
                        try
                        {
                            FPDFPage_SetRotation(page, 0);   // strip /Rotate so Docnet renders cleanly
                            FPDFPage_GenerateContent(page);
                        }
                        finally { FPDF_ClosePage(page); }
                    }

                    using var ms = new MemoryStream();
                    PdfWriteBlockDelegate cb = (_, pData, size) =>
                    {
                        var buf = new byte[size];
                        Marshal.Copy(pData, buf, 0, (int)size);
                        ms.Write(buf, 0, (int)size);
                        return 1;
                    };
                    var gch = GCHandle.Alloc(cb);
                    try
                    {
                        var fw = new FPDF_FILEWRITE
                        {
                            version = 1,
                            WriteBlock = Marshal.GetFunctionPointerForDelegate(cb)
                        };
                        if (!FPDF_SaveWithVersion(doc, ref fw, FPDF_REMOVE_SECURITY, 0))
                            return false;
                    }
                    finally { gch.Free(); }

                    File.WriteAllBytes(destPath, ms.ToArray());
                    return true;
                }
                finally { FPDF_CloseDocument(doc); }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "killerpdf_pdfium_debug.txt"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] TryPdfiumSaveWithZeroRotations failed\n" +
                        $"  source: {sourcePath}\n" +
                        $"  type:   {ex.GetType().FullName}\n" +
                        $"  msg:    {ex.Message}\n" +
                        $"  stack:  {ex.StackTrace}\n\n");
                }
                catch { /* log failure is non-fatal */ }
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────

        /// <param name="stripRotations">
        /// Pass true when called from SaveTempAndReload (rotations already stripped in source).
        /// Pass false for open-time repair so original page rotations are preserved.
        /// </param>
        private static bool TryImportRepairToPath(string sourcePath, string destPath, bool stripRotations = false)
        {
            try
            {
                using var importDoc = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Import);
                var cleanDoc = new PdfDocument();
                for (int i = 0; i < importDoc.PageCount; i++)
                    cleanDoc.Pages.Add(importDoc.Pages[i]);
                if (stripRotations)
                    for (int i = 0; i < cleanDoc.PageCount; i++)
                        cleanDoc.Pages[i].Rotate = 0;
                cleanDoc.Save(destPath);
                cleanDoc.Close();
                return true;
            }
            catch { return false; }
        }

        private async void TryRepairAndOpen(string path)
        {
            // Repair is CPU/IO heavy, so it runs on a background thread behind a spinner overlay -
            // otherwise the window froze (hourglass, no feedback) for the whole repair. Only the
            // file production runs off-thread; opening/rendering the result stays on the UI thread.
            _asyncOpenPending = true;   // the synchronous open caller defers tab finalization to here
            var busy = ShowBusyOverlay("Repairing PDF...");
            try
            {
                // Release any open document before the worker reads the source file.
                if (_doc is not null) { _doc.Close(); _doc = null; }

                string? repairedPath = null;
                bool raster = false;

                // Strategy 1: PdfSharpCore Import mode - page-copy, more lenient than Modify/ReadOnly.
                // Works when the XRef is partially corrupt but the object data is intact. (Returns
                // null on failure rather than throwing.)
                repairedPath = await System.Threading.Tasks.Task.Run(() => RepairViaImportToFile(path));

                // Strategy 2: PDFium rasterize. PDFium's internal XRef recovery handles damage
                // PdfSharpCore cannot; each page is rendered to a bitmap and rebuilt into a clean PDF.
                // Text won't be selectable in the result, but the file will open and print.
                if (repairedPath is null)
                {
                    repairedPath = await System.Threading.Tasks.Task.Run(() => RepairViaDocnetRasterizeToFile(path));
                    raster = repairedPath is not null;
                }

                if (repairedPath is null)
                {
                    HideBusyOverlay(busy);
                    _asyncOpenPending = false;
                    KillerDialog.Show(this,
                        "Repair failed - the file is too severely damaged to recover.\n\nTry opening the original in a different application (Adobe Acrobat, browsers) which may have additional recovery options.",
                        "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Open and render the repaired copy on the UI thread.
                _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                _currentFile = repairedPath;
                FinishOpenFile(path, repairedPath);
                MarkDirty(true); // repaired copy lives in temp - user must Save As
                SetStatus(string.Format(Loc(raster ? "Str_OpenedRasterRepair" : "Str_OpenedRepaired"),
                                        System.IO.Path.GetFileName(path), _doc.PageCount));
                HideBusyOverlay(busy);
                FinalizeAsyncOpen();
                KillerDialog.Show(this,
                    raster
                        ? $"\"{System.IO.Path.GetFileName(path)}\" was repaired by rasterizing through PDFium.\n\nText is not selectable in the repaired copy. Use Save As to write it to a new location."
                        : $"\"{System.IO.Path.GetFileName(path)}\" was repaired successfully.\n\nBookmarks, forms, and other interactive features may have been lost. Use Save As to write the repaired file to a new location.",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.None);
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                _asyncOpenPending = false;
                KillerDialog.Show(this, $"Repair failed:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Strips a PDF's encryption on a background thread (so the window doesn't freeze), then opens the
        // clean copy. Mirrors TryRepairAndOpen; finalizes the tab via FinalizeAsyncOpen.
        private async void StripEncryptionAndOpen(string srcPath, string displayPath, bool markDirty = true, string busyMessage = "Opening PDF...")
        {
            _asyncOpenPending = true;
            var busy = ShowBusyOverlay(busyMessage);
            try
            {
                if (_doc is not null) { _doc.Close(); _doc = null; }
                var repairedPath = App.MakeTempFile("repaired");
                bool ok = await System.Threading.Tasks.Task.Run(() =>
                    TryPdfiumStripEncryption(srcPath, repairedPath) || TryImportRepairToPath(srcPath, repairedPath));
                if (!ok)
                {
                    HideBusyOverlay(busy);
                    TryRepairAndOpen(srcPath);   // keeps _asyncOpenPending set; repair finalizes the tab
                    return;
                }
                _doc = PdfReader.Open(repairedPath, PdfDocumentOpenMode.Modify);
                _currentFile = repairedPath;
                FinishOpenFile(displayPath, repairedPath);
                if (markDirty) MarkDirty(true);   // stripped copy lives in temp - user must Save As to keep it
                HideBusyOverlay(busy);
                FinalizeAsyncOpen();
            }
            catch (Exception ex)
            {
                HideBusyOverlay(busy);
                _asyncOpenPending = false;
                KillerDialog.Show(this, $"Could not open the protected PDF:\n{ex.Message}",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Finalizes a background open (encryption strip / repair) once the document is loaded on the UI
        // thread: stores it into the active tab session and refreshes the tool + tab strip. Mirrors the
        // tail of OpenInNewTab, which is skipped while _asyncOpenPending is set.
        private void FinalizeAsyncOpen()
        {
            _asyncOpenPending = false;
            if (_active != null) CaptureSessionState(_active);
            SetTool(_currentTool);
            RebuildTabStrip();
        }

        /// <summary>
        /// Strategy 1 worker (background-safe, no UI/_doc access): page-copies the source through
        /// PdfSharpCore Import mode into a clean temp PDF and returns its path.
        /// </summary>
        private static string? RepairViaImportToFile(string path)
        {
            // Returns null (never throws) so a failed strategy falls through cleanly to the next one
            // and doesn't surface as a debugger "user-unhandled" break during the awaited Task.
            try
            {
                PdfDocument repairedDoc;
                using (var importDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import))
                {
                    repairedDoc = new PdfDocument();
                    for (int i = 0; i < importDoc.PageCount; i++)
                        repairedDoc.Pages.Add(importDoc.Pages[i]);
                }
                var repairedPath = App.MakeTempFile("repaired");
                repairedDoc.Save(repairedPath);
                repairedDoc.Close();
                return repairedPath;
            }
            catch { return null; }
        }

        /// <summary>
        /// Strategy 2 worker (background-safe, no UI/_doc access): uses PDFium (Docnet) to render
        /// each page to a bitmap, rebuilds a clean PdfSharpCore document from those bitmaps, and
        /// returns its temp path. Mirrors the flatten path, which also encodes off the UI thread.
        /// </summary>
        private static string? RepairViaDocnetRasterizeToFile(string path)
        {
            // Returns null (never throws) so the caller can show a clean "repair failed" message
            // without a debugger break on the awaited Task.
            try
            {
                const int RenderPx = 2048;

                using var docReader = DocLib.Instance.GetDocReader(path, new PageDimensions(RenderPx, RenderPx));
                int pageCount = docReader.GetPageCount();
                if (pageCount <= 0) return null;

                var newDoc = new PdfDocument();

                for (int i = 0; i < pageCount; i++)
                {
                    using var pr = docReader.GetPageReader(i);
                    int bw = pr.GetPageWidth();
                    int bh = pr.GetPageHeight();
                    if (bw <= 0 || bh <= 0) continue;

                    var raw = pr.GetImage();
                    if (raw is null || raw.Length == 0) continue;

                    var wb = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
                    wb.WritePixels(new Int32Rect(0, 0, bw, bh), raw, bw * 4, 0);
                    wb.Freeze();

                    byte[] pngBytes;
                    using (var ms = new System.IO.MemoryStream())
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(wb));
                        enc.Save(ms);
                        pngBytes = ms.ToArray();
                    }

                    // Build the page at correct aspect ratio scaled to A4-ish width.
                    double pageW = 595.28;
                    double pageH = pageW * bh / bw;

                    var page = newDoc.AddPage();
                    page.Width  = XUnit.FromPoint(pageW);
                    page.Height = XUnit.FromPoint(pageH);

                    using var gfx = XGraphics.FromPdfPage(page);
                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(pngBytes));
                    gfx.DrawImage(xImg, 0, 0, pageW, pageH);
                }

                if (newDoc.PageCount == 0) return null;

                var repairedPath = App.MakeTempFile("repaired");
                newDoc.Save(repairedPath);
                newDoc.Close();
                return repairedPath;
            }
            catch { return null; }
        }

        private static bool IsOwnerPasswordException(Exception ex) =>
            ex.Message.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0;

        private void FinishOpenFile(string displayPath, string workingPath)
        {
            _currentFile = workingPath;
            _originalFile = displayPath;
            FileNameLabel.Text = System.IO.Path.GetFileName(displayPath);
            _annotations.Clear();
            _undoStack.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formFontSizes.Clear();
            _formRadioValues.Clear();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            _gridScrollToPage = -1;
            MarkDirty(false);
            BootstrapDocumentView(0, autoFit: true);
            SetStatus(string.Format(Loc("Str_Opened"), System.IO.Path.GetFileName(displayPath), _doc!.PageCount));
        }

        // Renders the currently loaded document into the page list, outline tree, and preview
        // surface. Shared by the open pipeline (FinishOpenFile) and by tab switching. autoFit
        // fits a freshly opened file to the page; tab restores pass false to keep the saved zoom.
        private void BootstrapDocumentView(int initialPage, bool autoFit)
        {
            ClearSecondaryPages();
            ClearSelection();
            RefreshPageList();
            LoadOutlines();
            DropZone.Visibility = Visibility.Collapsed;
            PagePreviewPanel.Visibility = Visibility.Visible;
            if (_closeFileBtnRef != null) _closeFileBtnRef.IsEnabled = true;
            _pageJumpBox.IsEnabled = true;
            _pageTotalLabel.Text = $"/ {_doc!.PageCount}";
            if (_doc!.PageCount > 0)
            {
                int page = Math.Max(0, Math.Min(initialPage, _doc.PageCount - 1));
                PageList.SelectedIndex = page;
                // If Continuous mode is persisted from a previous session, SelectionChanged
                // returns early (no RenderPage call), so we have to bootstrap the panels here.
                if (_viewMode == ViewMode.Continuous)
                {
                    _pageContentPanel.Visibility = Visibility.Collapsed;
                    _continuousPanel.Visibility  = Visibility.Visible;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        () => SetupContinuousView(page));
                }
                // Fit / zoom once the first page has rendered and layout has settled.
                // DispatcherPriority.Background is lower than Loaded, so this fires after
                // all pending RenderPage / RefreshPageView callbacks have completed.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() =>
                    {
                        if (autoFit)
                        {
                            // Grid opens to its 3-across default; other modes fit to width.
                            if (_viewMode == ViewMode.Grid)
                                SetZoom(GridZoomForN(Math.Min(_doc?.PageCount ?? 1, 3)));
                            else
                                FitToPage();
                        }
                        else
                        {
                            // Tab restore: keep the document's saved zoom level.
                            SetZoom(_zoomLevel);
                        }
                    }));
            }
        }

        private static bool IsPasswordException(Exception ex) =>
            ex.Message.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("protected", StringComparison.OrdinalIgnoreCase) >= 0 ||
            ex.Message.IndexOf("encrypted", StringComparison.OrdinalIgnoreCase) >= 0;

        private string? PromptForPassword(string filename)
        {
            string? result = null;
            var win = new Window
            {
                Title = "Password Required",
                Width = 360,
                Height = 165,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))
            };
            var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            sp.Children.Add(new TextBlock
            {
                Text = $"\"{System.IO.Path.GetFileName(filename)}\" is password protected.",
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
            var pwBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(pwBox);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var okBtn = new Button { Content = "Open", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
            var cancelBtn = new Button { Content = "Cancel", Width = 76 };
            okBtn.Click += (s, ev) => { result = pwBox.Password; win.DialogResult = true; };
            cancelBtn.Click += (s, ev) => { win.DialogResult = false; };
            pwBox.KeyDown += (s, ev) => { if (ev.Key == Key.Enter) { result = pwBox.Password; win.DialogResult = true; } };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            sp.Children.Add(btnRow);
            win.Content = sp;
            return win.ShowDialog() == true ? result : null;
        }

        // Cancels the previous thumbnail background load when the file changes.
        private System.Threading.CancellationTokenSource? _thumbCts;

        private void RefreshPageList()
        {
            // Cancel any in-flight thumbnail load for the previous file.
            _thumbCts?.Cancel();
            _thumbCts = new System.Threading.CancellationTokenSource();
            var ct = _thumbCts.Token;

            if (_doc is null || _currentFile is null)
            {
                PageList.ItemsSource = null;
                return;
            }

            int    pageCount = _doc.PageCount;
            string filePath  = _currentFile;

            // Snapshot rotations on the UI thread before going to background.
            var rotSnap = new Dictionary<int, int>(_pageRotations);

            // Carry forward any existing thumbnails so the list never flashes blank
            // during reload (e.g. after a rotation).  New thumbnails replace them as
            // the background loader finishes each page.
            var oldItems = PageList.ItemsSource is PageThumbnailVm[] oi ? oi : null;

            var items = new PageThumbnailVm[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                rotSnap.TryGetValue(i, out int rot);
                items[i] = new PageThumbnailVm(i, filePath, rot);
                // Seed with stale thumbnail — better than blank while reloading
                if (oldItems != null && i < oldItems.Length)
                {
                    var prev = oldItems[i].Thumbnail;
                    if (prev != null) items[i].SetThumbnailDirect(prev);
                }
            }
            PageList.ItemsSource = items;

            // Load thumbnails sequentially on a background thread via a single doc reader.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(128, 256));
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            using var pr  = docReader.GetPageReader(i);
                            int tw  = pr.GetPageWidth();
                            int th  = pr.GetPageHeight();
                            var raw = pr.GetImage();
                            if (tw <= 0 || th <= 0 || raw == null || raw.Length < tw * th * 4)
                                continue;
                            rotSnap.TryGetValue(i, out int rot);
                            if (rot != 0)
                                (raw, tw, th) = RotateBitmap(raw, tw, th, rot);
                            var src = PageThumbnailVm.BuildThumbFromRaw(raw, tw, th);
                            if (src != null && !ct.IsCancellationRequested)
                                items[i].SetThumbnail(src);
                        }
                        catch { /* skip failed thumbnail; item shows label-only */ }
                    }
                }
                catch { /* docReader open failed; all items remain label-only */ }
            }, ct);
        }

        private void RenderPage(int pageIndex)
        {
            if (_currentFile is null || _doc is null) return;
            // Two-page spreads pair (0,1),(2,3),...; render the pair's left (even) page as primary so
            // selecting the right page of a pair still shows the whole spread, not a lone page.
            if (_viewMode == ViewMode.TwoPage) pageIndex -= pageIndex % 2;
            try
            {
                // Scale render resolution to match display DPI AND current zoom so the
                // bitmap stays sharp when zoomed in.  Base 2048 means Fit Width on a
                // wide monitor stays crisp; zoom factor ensures 1:1 pixels at 2× zoom.
                // Capped at 6144 to keep memory manageable.
                var dpiInfo = VisualTreeHelper.GetDpi(this);
                double dpiScaleX = dpiInfo.DpiScaleX;
                double dpiScaleY = dpiInfo.DpiScaleY;
                int scaledMax = (int)Math.Min(6144,
                    2048 * Math.Max(dpiScaleX, dpiScaleY) * Math.Max(1.0, _zoomLevel));
                _lastRenderZoom = _zoomLevel;

                using var docReader = DocLib.Instance.GetDocReader(_currentFile, new PageDimensions(scaledMax, scaledMax));
                using var pageReader = docReader.GetPageReader(pageIndex);

                int width = pageReader.GetPageWidth();
                int height = pageReader.GetPageHeight();
                var rawBytes = pageReader.GetImage();

                // Apply rotation: the temp file has /Rotate stripped so Docnet renders
                // unrotated (no clipping); rotate the pixel buffer to match the visual.
                if (_pageRotations.TryGetValue(pageIndex, out int pgRot) && pgRot != 0)
                    (rawBytes, width, height) = RotateBitmap(rawBytes, width, height, pgRot);

                if (width <= 0 || height <= 0 || rawBytes == null || rawBytes.Length == 0)
                {
                    PageImage.Source = null;
                    SetStatus(string.Format(Loc("Str_PageRenderError"), pageIndex + 1));
                    return;
                }

                // Convert pixel dimensions to WPF DIPs so the annotation canvas and
                // link overlays are sized in the same coordinate space that WPF uses for
                // layout.  Divide by the zoom factor so the canvas size (and therefore the
                // coordinate map used by DrawAnnotationsOnDocument) stays stable across
                // zoom re-renders — the bitmap just gets more pixels per DIP.
                // LayoutTransform handles the visual zoom, not the canvas dimensions.
                double zoomFactor = Math.Max(1.0, _zoomLevel);
                int dipW = (int)Math.Round(width  / dpiScaleX / zoomFactor);
                int dipH = (int)Math.Round(height / dpiScaleY / zoomFactor);
                _renderDims[pageIndex] = (dipW, dipH);

                // Scale bitmap DPI up so the extra pixels display within the same DIP area.
                double bitmapDpiX = 96.0 * width  / dipW;
                double bitmapDpiY = 96.0 * height / dipH;
                var bitmap = new WriteableBitmap(width, height, bitmapDpiX, bitmapDpiY, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, width, height), rawBytes, width * 4, 0);

                PageImage.Source = bitmap;
                _annotationCanvas.Width  = dipW;
                _annotationCanvas.Height = dipH;
                _annotationCanvas.Tag    = pageIndex;   // so clicks on the primary page resolve to the
                                                        // page actually shown (page 0 in grid), not the
                                                        // selected index - otherwise annotations on it
                                                        // are unhittable and clicks "do nothing".
                ClearSelection();
                ClearSecondaryPages();
                RenderAllAnnotations(pageIndex);
                SetStatus(string.Format(Loc("Str_PageOf"), pageIndex + 1, _doc!.PageCount));
                // Defer additional pages until layout has settled so ActualWidth is valid.
                // RenderPageLinks runs AFTER RenderAdditionalPages so ClearSecondaryPages
                // inside RenderAdditionalPages doesn't wipe the overlays we just added.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    RenderAdditionalPages(pageIndex);
                    RenderPageLinks(pageIndex, dipW, dipH);
                });
                _renderedPrimaryPage = pageIndex;
            }
            catch (Exception ex)
            {
                PageImage.Source = null;
                SetStatus(string.Format(Loc("Str_RenderError"), ex.Message));
            }
        }

        /// <summary>
        /// Clears all dynamically-added secondary page borders from the panel,
        /// leaving only the first child (the primary page border).
        /// </summary>
        // Removes secondary tiles whose page is no longer shown (keeps the primary at index 0 and any
        // tile still in range so it can be reused in place). Keeps the tile map in sync.
        private void RemoveSecondaryTilesNotIn(HashSet<int> keep)
        {
            if (_pageContentPanel is null) return;
            var stale = new List<int>();
            foreach (var k in _continuousCanvases.Keys)
                if (!keep.Contains(k)) stale.Add(k);
            foreach (var pg in stale)
            {
                if (_continuousCanvases.TryGetValue(pg, out var ov) && ov.Parent is Grid g && g.Parent is Border tile)
                {
                    foreach (var gc in g.Children) if (gc is Image im) im.Source = null;
                    _pageContentPanel.Children.Remove(tile);
                }
                _continuousCanvases.Remove(pg);
            }
        }

        private void ClearSecondaryPages()
        {
            if (_pageContentPanel is null) return;
            // Explicitly null out Image sources before removing so the GC can
            // reclaim the WriteableBitmap backing arrays promptly.
            while (_pageContentPanel.Children.Count > 1)
            {
                var child = _pageContentPanel.Children[^1];
                if (child is Border b && b.Child is Grid g)
                {
                    foreach (var gc in g.Children)
                        if (gc is Image img) img.Source = null;
                }
                _pageContentPanel.Children.RemoveAt(_pageContentPanel.Children.Count - 1);
            }
            // NOTE: do NOT reset _pageContentPanel.Width here.  Width is managed exclusively
            // by RenderAdditionalPages (which runs only via Dispatcher) so that no synchronous
            // call to ClearSecondaryPages triggers an intermediate layout pass that would cause
            // the primary page to flash centered and then jerk back to left-aligned.
            // Clear any link overlays from the annotation canvas.
            foreach (var lo in _linkOverlays)
                _annotationCanvas.Children.Remove(lo);
            _linkOverlays.Clear();
            _continuousCanvases.Clear();   // keep the page->tile map in sync with the visible tiles
        }

        /// <summary>
        /// Renders secondary pages as a grid. Panel-width setup is synchronous so layout
        /// is correct immediately; Docnet pixel rendering runs on a background thread so
        /// the UI stays responsive. WPF element creation returns to the UI thread.
        /// </summary>
        private async void RenderAdditionalPages(int primaryPageIdx)
        {
            if (_currentFile is null || _doc is null) return;
            // Grid is a stable overview anchored at page 0 (independent of the selected page), so it
            // always shows the whole document instead of only the selected page onward.
            if (_viewMode == ViewMode.Grid) primaryPageIdx = 0;

            double viewportW = PagePreviewPanel.ActualWidth;
            if (viewportW <= 0 || _doc.PageCount <= 1)
            {
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                return;
            }

            // Snap the WrapPanel width to a whole number of page-width slots.
            double primaryPageW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;
            double pageSlotW = primaryPageW + 12;
            double availablePreZoom = (viewportW - 24) / _zoomLevel;
            // +1e-6: same floating-point underflow guard as GridZoomStep, so a zoom set for n columns
            // actually lays out n (not n-1) when the division lands a hair under the integer.
            int pagesPerRow = _viewMode == ViewMode.TwoPage ? 2 : Math.Max(1, (int)(availablePreZoom / pageSlotW + 1e-6));
            if (_viewMode == ViewMode.Grid) _gridColumns = pagesPerRow;   // remember it for resize column-holding
            double panelW = pagesPerRow * pageSlotW;
            if (panelW > 0) _pageContentPanel.Width = panelW;

            // Cancel any previously running secondary render.
            _secondaryRenderCts?.Cancel();
            _secondaryRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _secondaryRenderCts;

            // Secondary pages: 1536 px base, scaled up for high-DPI displays so grid / two-page text
            // stays crisp on 150%/200% screens (capped at 3072 to keep memory in check). Stays 1536
            // at 100% DPI, so standard displays are unaffected.
            int SecondaryMax = (int)Math.Min(3072, 1536 * Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX));
            // Grid shows the whole document; Two-Page shows one secondary; other modes peek ahead.
            int limit = _viewMode == ViewMode.Grid
                ? _doc.PageCount
                : Math.Min(_doc.PageCount, primaryPageIdx + 1 + (_viewMode == ViewMode.TwoPage ? 1 : 25));
            if (limit <= primaryPageIdx + 1) { ClearSecondaryPages(); return; }

            // Per-tile reuse: drop tiles for pages that left the view, keep the rest. Pages that already
            // have a tile get their bitmap swapped in place (AddSecondaryTile); only genuinely new pages
            // are built. Stays smooth even mid-stream on a large doc, where the tile set is only partly
            // built. (Navigation clears everything via RenderPage first, so it rebuilds.)
            var keepPages = new HashSet<int>();
            for (int i = primaryPageIdx + 1; i < limit; i++) keepPages.Add(i);
            RemoveSecondaryTilesNotIn(keepPages);

            string currentFile = _currentFile;

            // Collect rotations on the UI thread before the background task.
            var secRotations = new Dictionary<int, int>();
            for (int i = primaryPageIdx + 1; i < limit; i++)
                if (_pageRotations.TryGetValue(i, out int r) && r != 0)
                    secRotations[i] = r;

            // Capture the primary page width and reset the tile map on the UI thread before
            // streaming tiles in from the background render.
            double primaryDipW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 595;

            // Render pixels on a background thread and attach each page tile to the UI as soon
            // as it is ready, so large documents fill in progressively instead of blocking
            // until every page has been rendered.
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using var docReader = DocLib.Instance.GetDocReader(currentFile, new PageDimensions(SecondaryMax, SecondaryMax));
                    for (int i = primaryPageIdx + 1; i < limit; i++)
                    {
                        if (cts.IsCancellationRequested) break;
                        using var pageReader = docReader.GetPageReader(i);
                        int w = pageReader.GetPageWidth();
                        int h = pageReader.GetPageHeight();
                        var rawBytes = pageReader.GetImage();
                        if (w <= 0 || h <= 0 || rawBytes is null) continue;
                        if (secRotations.TryGetValue(i, out int rot))
                            (rawBytes, w, h) = RotateBitmap(rawBytes, w, h, rot);

                        int pi = i, pw = w, ph = h;
                        byte[] bytes = rawBytes;
                        try
                        {
                            Dispatcher.Invoke(() =>
                            {
                                if (cts.IsCancellationRequested || _doc is null) return;
                                if (_viewMode != ViewMode.Grid && _viewMode != ViewMode.TwoPage) return;
                                AddSecondaryTile(pi, pw, ph, bytes, primaryDipW);
                            });
                        }
                        // Dispatcher.Invoke throws when the dispatcher is shutting down (app closing) or
                        // the render was cancelled; stop rendering cleanly instead of crashing.
                        catch (System.Threading.Tasks.TaskCanceledException) { break; }
                        catch (OperationCanceledException) { break; }
                    }
                }, cts.Token);
            }
            catch { return; }
        }

        /// <summary>
        /// Builds one secondary-page tile (image + annotation overlay + links) and appends it
        /// to the page content panel. Must run on the UI thread.
        /// </summary>
        private void AddSecondaryTile(int pi, int w, int h, byte[] rawBytes, double primaryDipW)
        {
            int pageDipW = (int)Math.Round(primaryDipW);
            int pageDipH = (int)Math.Round(primaryDipW * h / w);
            double bitmapDpiX = 96.0 * w / pageDipW;
            double bitmapDpiY = 96.0 * h / pageDipH;

            var bitmap = new WriteableBitmap(w, h, bitmapDpiX, bitmapDpiY, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, w, h), rawBytes, w * 4, 0);

            // This page already has a tile: swap just the bitmap (same logical size, crisper pixels).
            // No clear, no reflow - so the grid/spread never jumps or blinks.
            if (_continuousCanvases.TryGetValue(pi, out var exOverlay)
                && exOverlay.Parent is Grid exGrid && exGrid.Children.Count > 0 && exGrid.Children[0] is Image exImg)
            {
                exImg.Source = bitmap;
                return;
            }

            // Do NOT overwrite _renderDims if the page was already rendered as primary -
            // its annotation coordinate mapping must stay intact.
            if (!_renderDims.ContainsKey(pi))
                _renderDims[pi] = (pageDipW, pageDipH);

            var img = new Image { Source = bitmap, Stretch = Stretch.None };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            var overlay = new Canvas
            {
                Width = pageDipW, Height = pageDipH,
                Background = Brushes.Transparent,
                Cursor = CursorForTool(_currentTool),
                Tag = pi,
                ToolTip = $"Page {pi + 1}"
            };
            int capturedPi = pi;
            overlay.PreviewMouseLeftButtonDown += (s, ev) =>
            {
                if (_currentTool == EditTool.Select)
                {
                    var hit = ev.GetPosition((Canvas)s);
                    bool onAnnot = (_annotations.TryGetValue(capturedPi, out var list)
                                    && list.Any(a => HitTestAnnotation(a, hit, out _)))
                                   || _selectedAnnotation?.PageIndex == capturedPi;
                    // Forward to the canvas handler when the click is on an annotation, OR on any
                    // double-click: double-click starts text editing on raw PDF text, which is not an
                    // annotation yet, so it must reach Canvas_MouseLeftButtonDown (EditTextAtPosition)
                    // even on an "empty" tile. Without the ClickCount check, editing fresh text was
                    // impossible on secondary tiles (all grid pages, the 2nd two-page page).
                    if (onAnnot || ev.ClickCount == 2) Canvas_MouseLeftButtonDown(s, ev);
                    // Selecting the page reflows the Two-Page pair (looks like the doc "advances"),
                    // so don't change the selection on an empty click of a secondary tile there.
                    else if (_viewMode != ViewMode.TwoPage) PageList.SelectedIndex = capturedPi;
                }
                else Canvas_MouseLeftButtonDown(s, ev);
            };
            overlay.MouseMove                += Canvas_MouseMove;
            overlay.PreviewMouseLeftButtonUp += Canvas_MouseLeftButtonUp;
            // Right-click on a tile selects that page and opens the same context menu the primary
            // page uses (per-page overlays don't inherit _annotationCanvas's ContextMenu).
            overlay.PreviewMouseRightButtonUp += (s, ev) =>
            {
                // Don't change the selected page in Two-Page view - it would reflow the pair and
                // drop to a single page. Grid is anchored at page 0 and Continuous just scrolls.
                if (_viewMode != ViewMode.TwoPage) PageList.SelectedIndex = capturedPi;
                if (_annotationCanvas.ContextMenu is ContextMenu cm)
                {
                    cm.PlacementTarget = (UIElement)s;
                    cm.IsOpen = true;
                    ev.Handled = true;
                }
            };
            _continuousCanvases[pi] = overlay;

            var pageGrid = new Grid();
            pageGrid.Children.Add(img);
            pageGrid.Children.Add(overlay);
            AddSecondaryPageLinks(pi, pageGrid, pageDipW, pageDipH);

            var tile = new Border
            {
                Background = Brushes.White,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 12, 12),
                Child = pageGrid
            };
            _pageContentPanel.Children.Add(tile);
            RenderAllAnnotations(pi);

            // Grid tiles render asynchronously, so a "scroll to page N" requested when entering grid
            // can't run until page N's tile exists. Do it the moment that tile streams in.
            if (pi == _gridScrollToPage)
            {
                _gridScrollToPage = -1;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() =>
                    {
                        if (_viewMode != ViewMode.Grid) return;
                        try
                        {
                            // Top-align the page's row in the viewport (accounts for the zoom transform).
                            if (PagePreviewPanel.Content is FrameworkElement content)
                                PagePreviewPanel.ScrollToVerticalOffset(
                                    tile.TransformToVisual(content).Transform(new Point(0, 0)).Y);
                            else
                                tile.BringIntoView();
                        }
                        catch { tile.BringIntoView(); }
                    }));
            }
        }

        /// <summary>Look up a localized string. Falls back to the key name if missing.</summary>
        private string Loc(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

                private void SetStatus(string text)
        {
            StatusText.Text = text;
            CrashReporter.PushStatusMessage(text);
        }

        private void VersionLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowAboutOverlay();
        }

        /// <summary>
        /// Re-renders secondary pages and then link overlays for the current page.
        /// Must be called via Dispatcher so layout is settled before RenderAdditionalPages
        /// reads ActualWidth. All zoom-change and sidebar-toggle dispatch sites use this
        /// instead of a bare RenderAdditionalPages call so link overlays are never left
        /// cleared without being re-added.
        /// </summary>
        private void RefreshPageView(int pageIndex)
        {
            if (_viewMode == ViewMode.Continuous)
                return; // continuous mode manages its own rendering
            if (_viewMode == ViewMode.TwoPage) pageIndex -= pageIndex % 2;   // snap to the spread's left page

            // Grid fits its columns to the viewport, so it never needs a horizontal scrollbar.
            // Leaving it on Auto shows a stray (green) thumb across the bottom when the tile panel
            // overflows by the vertical scrollbar's width. Disable it for grid, Auto elsewhere.
            PagePreviewPanel.HorizontalScrollBarVisibility =
                _viewMode == ViewMode.Grid ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            // Reserve the vertical scrollbar in Grid so its appearing/disappearing can't change the
            // viewport width mid-resize and feed a width change back into the layout (the loop the grid
            // used to guard against). A stable width lets the column-holding resize stay stable too.
            PagePreviewPanel.VerticalScrollBarVisibility =
                _viewMode == ViewMode.Grid ? ScrollBarVisibility.Visible : ScrollBarVisibility.Auto;
            // Single page is centered; drop the right/bottom tile-gap margin that grid/two-page
            // need for spacing (it would otherwise push the lone page a few px left of center).
            if (_pageContentPanel is not null && _pageContentPanel.Children.Count > 0
                && _pageContentPanel.Children[0] is Border primaryBorder)
                primaryBorder.Margin = _viewMode == ViewMode.Single
                    ? new Thickness(0) : new Thickness(0, 0, 12, 12);
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.TwoPage)
                RenderAdditionalPages(pageIndex);
            else
            {
                ClearSecondaryPages();
                if (_pageContentPanel is not null)
                    _pageContentPanel.Width = double.NaN;
            }
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderPageLinks(pageIndex, dims.w, dims.h);
        }



        // ============================================================
        // PDF Link Annotation Overlays
        // ============================================================

        private readonly record struct LinkInfo(double Cx, double Cy, double Cw, double Ch, object Tag, string Tip, int AnnotIndex);

        /// <summary>
        /// Carries the link target (page index or URI string) plus the annotation's location in
        /// the PDF so the overlay can be used to remove the native annotation on demand.
        /// </summary>
        private sealed class LinkAnnotInfo(object target, int pageIndex, int annotIndex)
        {
            public object   Target     { get; } = target;      // int pageIndex or string URI
            public int      PageIndex  { get; } = pageIndex;   // 0-based page in _doc
            public int      AnnotIndex { get; } = annotIndex;  // index inside page /Annots array
        }

        /// <summary>
        /// Parses all link annotations from a PDF page and converts them to canvas-space
        /// rectangles. Works for both primary and secondary page renders.
        /// </summary>
        private List<LinkInfo> GetPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            var links = new List<LinkInfo>();
            if (_doc is null) return links;
            try
            {
                var pdfPage = _doc.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return links;

                double pageWidthPt  = pdfPage.Width.Point;
                double pageHeightPt = pdfPage.Height.Point;
                if (pageWidthPt  <= 0) pageWidthPt  = 595.28;
                if (pageHeightPt <= 0) pageHeightPt = 841.89;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Link")) continue;

                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
                    if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

                    double cx = rx1 / pageWidthPt  * bitmapW;
                    double cy = (pageHeightPt - ry2) / pageHeightPt * bitmapH;
                    double cw = (rx2 - rx1) / pageWidthPt  * bitmapW;
                    double ch = (ry2 - ry1) / pageHeightPt * bitmapH;
                    if (cw < 1 || ch < 1) continue;

                    int? targetPage = null;
                    string? uri = null;

                    var actionDict = ann.Elements.GetDictionary("/A");
                    if (actionDict != null)
                    {
                        var s = actionDict.Elements["/S"]?.ToString() ?? "";
                        if (s.Contains("GoTo"))
                            targetPage = ResolveDest(actionDict.Elements["/D"]);
                        else if (s.Contains("URI"))
                            uri = actionDict.Elements.GetString("/URI");
                    }
                    else
                    {
                        targetPage = ResolveDest(ann.Elements["/Dest"]);
                    }

                    if (targetPage is null && uri is null) continue;

                    object tag = targetPage.HasValue ? (object)targetPage.Value : uri!;
                    string tip = targetPage.HasValue ? $"Go to page {targetPage.Value + 1}" : uri!;
                    links.Add(new LinkInfo(cx, cy, cw, ch, tag, tip, i));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageLinks: {ex}"); }
            return links;
        }

        /// <summary>
        /// Renders link overlays for the primary page onto the annotation canvas.
        /// Uses a manual bounds-check in Canvas_MouseLeftButtonDown for hit detection
        /// (transparent Canvas children are unreliable for WPF hit-testing alone).
        /// </summary>
        private void RenderPageLinks(int pageIndex, int bitmapW, int bitmapH)
        {
            if (_doc is null || _currentFile is null) return;

            var links = GetPageLinks(pageIndex, bitmapW, bitmapH);
            foreach (var lnk in links)
            {
                var info = new LinkAnnotInfo(lnk.Tag, pageIndex, lnk.AnnotIndex);
                var overlay = new Canvas
                {
                    Width            = lnk.Cw,
                    Height           = lnk.Ch,
                    Background       = Brushes.Transparent,
                    Cursor           = Cursors.Hand,
                    ToolTip          = lnk.Tip,
                    Tag              = info,
                    IsHitTestVisible = true,
                };
                Canvas.SetLeft(overlay, lnk.Cx);
                Canvas.SetTop(overlay, lnk.Cy);

                // Right-click context menu: remove the native PDF annotation or copy the URL.
                var cm = new ContextMenu();
                TextOptions.SetTextFormattingMode(cm, TextFormattingMode.Display);
                TextOptions.SetTextRenderingMode(cm, TextRenderingMode.Grayscale);
                if (lnk.Tag is string uriTag && uriTag.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    cm.Items.Add(MakeMenuItem("Copy Email Address", (_, _) =>
                        Clipboard.SetText(uriTag["mailto:".Length..])));
                else if (lnk.Tag is string httpTag)
                    cm.Items.Add(MakeMenuItem("Copy URL", (_, _) => Clipboard.SetText(httpTag)));
                cm.Items.Add(MakeMenuItem("Remove Link from PDF", (_, _) =>
                    RemoveLinkAnnotation(info.PageIndex, info.AnnotIndex)));
                overlay.ContextMenu = cm;

                _annotationCanvas.Children.Add(overlay);
                _linkOverlays.Add(overlay);
            }

            if (links.Count > 0)
                SetStatus(string.Format(Loc("Str_PageOfLinks"), pageIndex + 1, _doc.PageCount, links.Count));
        }

        /// <summary>
        /// Removes a native PDF link annotation from the page /Annots array and persists the change.
        /// Called from the "Remove Link from PDF" context-menu item on link overlays.
        /// </summary>
        private void RemoveLinkAnnotation(int pageIndex, int annotIndex)
        {
            if (_doc is null || pageIndex >= _doc.PageCount) return;
            try
            {
                var pdfPage = _doc.Pages[pageIndex];
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null || annotIndex >= annotsArr.Elements.Count) return;

                // Neutralize the annotation object before removing the /Annots reference.
                // If PdfSharpCore writes the orphaned indirect object to the output file,
                // aggressive PDF viewers that scan cross-reference tables directly (rather
                // than following /Annots) would still trigger the link without this step.
                PdfItem? elem = annotsArr.Elements[annotIndex];
                PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                if (ann != null)
                {
                    ann.Elements.Remove("/A");
                    ann.Elements.Remove("/Dest");
                    ann.Elements.Remove("/Subtype");
                }

                annotsArr.Elements.RemoveAt(annotIndex);
                MarkDirty();
                SaveTempAndReload();
                // Refresh the current page view so the overlay disappears.
                int sel = PageList.SelectedIndex;
                PageList.SelectedIndex = -1;
                PageList.SelectedIndex = sel;
                SetStatus(Loc("Str_LinkRemoved"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Remove link failed:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Strips visual styling (border, color, appearance stream) from all Link annotations
        /// in the document so they render as invisible clickable areas rather than colored
        /// rectangles that can look like strikethroughs in other PDF viewers.
        /// </summary>
        private static void StripLinkAnnotationBorders(PdfDocument doc)
        {
            foreach (var pdfPage in doc.Pages)
            {
                var annotsArr = pdfPage.Elements.GetArray("/Annots");
                if (annotsArr is null) continue;
                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    // Dereference subtype in case it is an indirect name.
                    var subtypeItem = ann.Elements["/Subtype"];
                    var subtype = (subtypeItem as PdfDictionary ?? DerefItem(subtypeItem) as PdfDictionary) is null
                        ? subtypeItem?.ToString() ?? ""
                        : "";
                    if (!subtype.Contains("Link")) continue;

                    // Remove appearance stream and color.
                    ann.Elements.Remove("/AP");
                    ann.Elements.Remove("/C");

                    // /BS (border style dict) takes precedence over /Border in PDF spec;
                    // set W=0 explicitly.  Also set /Border [0 0 0] for older viewers.
                    var bs = new PdfDictionary();
                    bs.Elements["/W"] = new PdfInteger(0);
                    ann.Elements["/BS"] = bs;

                    var borderArr = new PdfArray();
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    borderArr.Elements.Add(new PdfInteger(0));
                    ann.Elements["/Border"] = borderArr;
                }
            }
        }

        /// <summary>
        /// Adds link overlays to a secondary-page Grid so PDF links within that page are
        /// clickable even when the page is visible only in the multi-page grid view.
        ///
        /// Canvas.SetLeft/Top attached properties ONLY take effect when the element's
        /// direct parent is a Canvas.  Adding link elements straight into the Grid (as
        /// siblings of the page-nav overlay) would leave them all at (0,0), causing every
        /// click to hit the wrong element.  Instead we create a transparent Canvas
        /// container the same size as the page and use it as the coordinate space.
        // ============================================================
        // PDF Form Field Overlays
        // ============================================================

        private readonly record struct FormFieldInfo(
            int    ObjNum,        // widget annotation object number (used as key)
            string FieldType,     // /Tx, /Btn, /Ch
            bool   IsCheckBox,
            bool   IsRadio,
            bool   IsMultiLine,   // /Tx with Multiline flag (bit 12)
            string FieldName,
            string CurrentValue,
            string OnValue,       // radio/checkbox on-state value (e.g. "/Yes")
            bool   IsReadOnly,
            double Cx, double Cy, double Cw, double Ch,
            List<string> Options,
            double DaFontPt,   // font size from the field's /DA (points); 0 = auto-size
            double Scale);     // canvas units per PDF point, for converting DaFontPt to canvas size

        /// <summary>
        /// Scans the current page's /Annots for Widget subtypes and overlays interactive
        /// WPF controls on the annotation canvas so the user can fill in form fields.
        /// </summary>
        private void RenderFormFields(int pageIndex, int canvasW, int canvasH)
        {
            if (_doc is null || _currentFile is null) return;
            if (pageIndex >= _doc.PageCount) return;

            // Render onto the page's OWN surface: the per-page overlay used by continuous / grid /
            // two-page views, or the single-page canvas otherwise. Previously this always used the
            // single-page canvas, so interactive fields only appeared in Single Page view.
            var canvas = _continuousCanvases.TryGetValue(pageIndex, out var pageCanvas) ? pageCanvas : _annotationCanvas;

            // Remove stale overlays without wiping the entire canvas.
            for (int i = canvas.Children.Count - 1; i >= 0; i--)
                if (canvas.Children[i] is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    canvas.Children.RemoveAt(i);

            var fields = GetPageFormFields(pageIndex, canvasW, canvasH);
            if (fields.Count == 0) return;

            // Focus highlight (accent). Fields are NOT outlined at rest - the page's own field boxes
            // already show where to type - so we only tint a faint fill and show the accent on focus,
            // matching how Chrome/Brave render fields instead of drawing a green line around each one.
            var fieldBorder = new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88)); // faint gray, check/radio only
            var darkBrush   = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            var fieldBg     = new SolidColorBrush(Color.FromArgb(200, 255, 253, 231));

            // Collect radio buttons per group so we can wire mutual exclusion after the loop.
            var radioGroups = new Dictionary<string, List<(Ellipse dot, string onVal)>>();

            bool anyField = false;
            foreach (var f in fields)
            {
                UIElement? ctrl = null;

                // ── Text field ────────────────────────────────────────────────────
                var fillRole = ClassifyFormField(f);
                if (fillRole == FormFillRole.Signature || fillRole == FormFillRole.Initials)
                {
                    ctrl = BuildSignZone(f, fillRole == FormFillRole.Initials, pageIndex);
                }
                else if (!f.IsCheckBox && !f.IsRadio && f.FieldType != "/Ch")
                {
                    string cur     = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    // Size text the way the field intends: use its /DA font size when one is given;
                    // otherwise auto-size - single-line fits the box height (capped so a tall field
                    // isn't giant), multi-line uses a steady readable size rather than shrinking with
                    // the box. This replaces the old box-height guess that made fields huge or tiny.
                    double fontSize;
                    if (_formFontSizes.TryGetValue(f.ObjNum, out var userPt) && userPt > 0 && f.Scale > 0)
                        fontSize = userPt * f.Scale;          // user override (the new per-field size control)
                    else if (f.DaFontPt > 0.5 && f.Scale > 0)
                        fontSize = f.DaFontPt * f.Scale;
                    else if (f.IsMultiLine)
                        fontSize = f.Scale > 0 ? 11.5 * f.Scale : Math.Max(11, Math.Min(f.Cw, f.Ch) * 0.5);
                    else
                        fontSize = f.Scale > 0 ? Math.Min(f.Ch * 0.62, 15 * f.Scale) : f.Ch * 0.62;
                    fontSize = Math.Max(9, Math.Min(fontSize, 400));
                    var tb = new TextBox
                    {
                        Tag              = FormOverlayTag,
                        Width            = f.Cw,
                        Height           = f.Ch,
                        Text             = cur,
                        IsReadOnly       = f.IsReadOnly,
                        AcceptsReturn    = f.IsMultiLine,
                        TextWrapping     = f.IsMultiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        VerticalScrollBarVisibility = f.IsMultiLine
                            ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                        Background       = fieldBg,
                        Foreground       = Brushes.Black,
                        CaretBrush       = Brushes.Black,
                        SelectionBrush   = (System.Windows.Media.Brush)FindResource("HeaderAccent"),
                        Style            = (Style)FindResource("FormFieldTextBox"),
                        BorderBrush      = Brushes.Transparent,
                        BorderThickness  = new Thickness(1),
                        FontSize         = fontSize,
                        Padding          = new Thickness(3, 0, 3, 0),
                        VerticalContentAlignment = f.IsMultiLine
                            ? VerticalAlignment.Top : VerticalAlignment.Center,
                        ToolTip          = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    // No outline at rest (the page already shows the field box); accent only on focus.
                    // Focus also raises the per-field font-size stepper (and hides it on blur).
                    int    capturedKey   = f.ObjNum;
                    double capturedScale = f.Scale;
                    tb.GotFocus  += (_, _) => { tb.SetResourceReference(Control.BorderBrushProperty, "HeaderAccent"); ShowFormSizeBar(tb, capturedKey, capturedScale); };
                    tb.LostFocus += (_, _) => { tb.BorderBrush = Brushes.Transparent; HideFormSizeBar(); };
                    tb.TextChanged += (_, _) => { _formTextValues[capturedKey] = tb.Text; MarkDirty(true); };
                    ctrl = tb;
                }

                // ── Dropdown / choice ─────────────────────────────────────────────
                else if (f.FieldType == "/Ch" && f.Options.Count > 0)
                {
                    string cur = _formTextValues.TryGetValue(f.ObjNum, out var tv) ? tv : f.CurrentValue;
                    var combo = new ComboBox
                    {
                        Tag       = FormOverlayTag,
                        Width     = f.Cw,
                        Height    = f.Ch,
                        IsEnabled = !f.IsReadOnly,
                        Foreground = Brushes.Black,
                        FontSize  = f.DaFontPt > 0.5 && f.Scale > 0
                            ? f.DaFontPt * f.Scale
                            : Math.Min(Math.Max(10, f.Ch * 0.55), 16),
                        ToolTip   = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    foreach (var opt in f.Options) combo.Items.Add(opt);
                    combo.SelectedItem = cur;
                    int capturedKey = f.ObjNum;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is string s) { _formTextValues[capturedKey] = s; MarkDirty(true); }
                    };
                    ctrl = combo;
                }

                // ── Checkbox ──────────────────────────────────────────────────────
                else if (f.IsCheckBox)
                {
                    bool isChecked = _formCheckValues.TryGetValue(f.ObjNum, out var cv) ? cv
                        : !string.IsNullOrEmpty(f.CurrentValue)
                          && f.CurrentValue != "/Off" && f.CurrentValue != "Off";

                    // Custom border-based checkbox — WPF's built-in CheckBox indicator
                    // doesn't scale with Width/Height, so we draw it ourselves.
                    double checkFs = Math.Min(f.Cw, f.Ch) * 0.72;
                    var checkMark = new TextBlock
                    {
                        Text       = "✓",
                        FontSize   = checkFs,
                        FontWeight = FontWeights.Bold,
                        Foreground = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var box = new Border
                    {
                        Tag             = FormOverlayTag,
                        Width           = f.Cw,
                        Height          = f.Ch,
                        Background      = fieldBg,
                        BorderBrush     = fieldBorder,
                        BorderThickness = new Thickness(1.5),
                        CornerRadius    = new CornerRadius(2),
                        Cursor          = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child           = checkMark,
                        ToolTip         = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };
                    if (!f.IsReadOnly)
                    {
                        int capturedKey = f.ObjNum;
                        box.MouseLeftButtonDown += (_, e) =>
                        {
                            bool now = !(_formCheckValues.TryGetValue(capturedKey, out var v) ? v : isChecked);
                            _formCheckValues[capturedKey] = now;
                            checkMark.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty(true);
                            e.Handled = true;
                        };
                    }
                    ctrl = box;
                }

                // ── Radio button ──────────────────────────────────────────────────
                else if (f.IsRadio)
                {
                    string groupSelected = _formRadioValues.TryGetValue(f.FieldName, out var rv) ? rv
                        : f.CurrentValue; // CurrentValue = parent /V = currently selected on-value
                    bool isSelected = groupSelected == f.OnValue;

                    double size  = Math.Min(f.Cw, f.Ch) * 0.88;
                    double inner = size * 0.52;

                    var dot = new Ellipse
                    {
                        Width      = inner,
                        Height     = inner,
                        Fill       = darkBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                        Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                    };
                    var ring = new Ellipse
                    {
                        Width           = size,
                        Height          = size,
                        Stroke          = fieldBorder,
                        StrokeThickness = 1.5,
                        Fill            = fieldBg,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center,
                    };
                    var grid = new Grid { Width = f.Cw, Height = f.Ch };
                    grid.Children.Add(ring);
                    grid.Children.Add(dot);

                    var radioBorder = new Border
                    {
                        Tag    = FormOverlayTag,
                        Width  = f.Cw,
                        Height = f.Ch,
                        Background = Brushes.Transparent,
                        Cursor = f.IsReadOnly ? Cursors.Arrow : Cursors.Hand,
                        Child  = grid,
                        ToolTip = string.IsNullOrEmpty(f.FieldName) ? null : f.FieldName,
                    };

                    // Register dot for mutual-exclusion wiring after the loop.
                    if (!radioGroups.TryGetValue(f.FieldName, out var groupList))
                        radioGroups[f.FieldName] = groupList = [];
                    groupList.Add((dot, f.OnValue));

                    if (!f.IsReadOnly)
                    {
                        string capturedGroup = f.FieldName;
                        string capturedOn    = f.OnValue;
                        radioBorder.MouseLeftButtonDown += (_, e) =>
                        {
                            _formRadioValues[capturedGroup] = capturedOn;
                            // Deselect all in group, then select this one.
                            if (radioGroups.TryGetValue(capturedGroup, out var gl))
                                foreach (var (d, ov) in gl)
                                    d.Visibility = ov == capturedOn ? Visibility.Visible : Visibility.Collapsed;
                            MarkDirty(true);
                            e.Handled = true;
                        };
                    }
                    ctrl = radioBorder;
                }

                if (ctrl is null) continue;
                Canvas.SetLeft(ctrl, f.Cx);
                Canvas.SetTop(ctrl, f.Cy);
                canvas.Children.Add(ctrl);
                anyField = true;
            }

            if (anyField)
                SetStatus(string.Format(Loc("Str_PageFormFields"), pageIndex + 1, _doc.PageCount));
        }

        /// <summary>
        /// Parses Widget annotations from the given page into field descriptors with canvas coordinates.
        /// Walks the parent chain for each widget to resolve inherited /FT, /T, /V, and /Ff.
        /// </summary>
        private List<FormFieldInfo> GetPageFormFields(int pageIndex, int canvasW, int canvasH)
        {
            var result = new List<FormFieldInfo>();
            if (_doc is null || pageIndex >= _doc.PageCount) return result;

            var page = _doc.Pages[pageIndex];
            // PDFium renders the CropBox (falling back to the MediaBox when there is no crop), so
            // field /Rect coordinates must be mapped relative to THAT box's origin and size - not
            // assumed to start at (0,0) with MediaBox dimensions. Pages whose box origin is offset,
            // or whose CropBox is inset from the MediaBox, otherwise shift every field a little;
            // mapping to the rendered box's own origin lines them up the way Acrobat/Chrome do.
            var mediaBox = page.MediaBox;
            var cropBox  = page.CropBox;
            var box      = (cropBox.Width > 1 && cropBox.Height > 1) ? cropBox : mediaBox;
            double boxX  = box.X1;   // box lower-left origin in PDF user space
            double boxY  = box.Y1;
            double pageW = box.Width  > 0 ? box.Width  : 595.28;
            double pageH = box.Height > 0 ? box.Height : 841.89;
            int rotation = ((page.Rotate % 360) + 360) % 360;

            try
            {
                var annotsArr = page.Elements.GetArray("/Annots");
                if (annotsArr is null || annotsArr.Elements.Count == 0) return result;

                for (int i = 0; i < annotsArr.Elements.Count; i++)
                {
                    PdfItem? elem   = annotsArr.Elements[i];
                    PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                    if (ann is null) continue;

                    var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                    if (!subtype.Contains("Widget")) continue;

                    // Get rect
                    var rectArr = ann.Elements.GetArray("/Rect");
                    if (rectArr is null || rectArr.Elements.Count < 4) continue;
                    double rx1 = rectArr.Elements.GetReal(0);
                    double ry1 = rectArr.Elements.GetReal(1);
                    double rx2 = rectArr.Elements.GetReal(2);
                    double ry2 = rectArr.Elements.GetReal(3);
                    if (rx1 > rx2) (rx1, rx2) = (rx2, rx1);
                    if (ry1 > ry2) (ry1, ry2) = (ry2, ry1);

                    // Field rect relative to the rendered box's lower-left origin, so an offset
                    // MediaBox/CropBox doesn't push the field off its drawn box.
                    double fx1 = rx1 - boxX, fy1 = ry1 - boxY;
                    double fx2 = rx2 - boxX, fy2 = ry2 - boxY;

                    // Map PDF rect (bottom-left origin, unrotated) to canvas coords.
                    // The canvas matches the Docnet-rendered bitmap which has already applied
                    // the page rotation, so we must transform accordingly.
                    double cx, cy, cw, ch;
                    switch (rotation)
                    {
                        case 90: // 90° CW: bottom→left, left→top; canvas is pageH-wide × pageW-tall
                            // (px,py) → canvas (py, px)
                            cx = fy1             / pageH * canvasW;
                            cy = fx1             / pageW * canvasH;
                            cw = (fy2 - fy1)     / pageH * canvasW;
                            ch = (fx2 - fx1)     / pageW * canvasH;
                            break;
                        case 180: // 180°: both axes flipped
                            // (px,py) → canvas (pageW-px, py)
                            cx = (pageW - fx2)   / pageW * canvasW;
                            cy = fy1             / pageH * canvasH;
                            cw = (fx2 - fx1)     / pageW * canvasW;
                            ch = (fy2 - fy1)     / pageH * canvasH;
                            break;
                        case 270: // 270° CW (= 90° CCW): bottom→right, right→top; canvas is pageH-wide × pageW-tall
                            // (px,py) → canvas (pageH-py, pageW-px)
                            cx = (pageH - fy2)   / pageH * canvasW;
                            cy = (pageW - fx2)   / pageW * canvasH;
                            cw = (fy2 - fy1)     / pageH * canvasW;
                            ch = (fx2 - fx1)     / pageW * canvasH;
                            break;
                        default: // 0° — standard bottom-left PDF → top-left canvas
                            cx = fx1             / pageW * canvasW;
                            cy = (pageH - fy2)   / pageH * canvasH;
                            cw = (fx2 - fx1)     / pageW * canvasW;
                            ch = (fy2 - fy1)     / pageH * canvasH;
                            break;
                    }
                    if (cw < 2 || ch < 2) continue;

                    // Walk the parent chain to resolve inherited attributes
                    string ft     = "";
                    string name   = "";
                    string curVal = "";
                    string da     = "";   // default appearance string (holds the field's font size)
                    int    flags  = 0;
                    var    options = new List<string>();

                    PdfDictionary? node = ann;
                    while (node is not null)
                    {
                        if (string.IsNullOrEmpty(ft)   && node.Elements["/FT"] is not null)
                            ft = node.Elements["/FT"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(name) && node.Elements["/T"] is PdfString ts)
                            name = ts.Value;
                        if (string.IsNullOrEmpty(curVal) && node.Elements["/V"] is not null)
                        {
                            var vElem = node.Elements["/V"];
                            curVal = vElem is PdfString vs ? vs.Value : vElem?.ToString() ?? "";
                        }
                        if (string.IsNullOrEmpty(da) && node.Elements["/DA"] is PdfString das)
                            da = das.Value;
                        if (flags == 0 && node.Elements["/Ff"] is PdfInteger fi)
                            flags = fi.Value;
                        if (options.Count == 0 && node.Elements.GetArray("/Opt") is PdfArray optArr)
                        {
                            for (int j = 0; j < optArr.Elements.Count; j++)
                            {
                                var o = optArr.Elements[j];
                                if (o is PdfString ps2) options.Add(ps2.Value);
                                else if (o is PdfArray pa2 && pa2.Elements.Count >= 2)
                                    options.Add((pa2.Elements[1] as PdfString)?.Value ?? "");
                            }
                        }

                        // Move to parent
                        var parentItem = node.Elements["/Parent"];
                        if (parentItem is null) break;
                        node = parentItem as PdfDictionary ?? DerefItem(parentItem) as PdfDictionary;
                    }

                    // No resolvable field type (directly or inherited) means this Widget is not a fillable
                    // field (just a bare annotation widget). Skip it rather than guessing it's a text box.
                    if (string.IsNullOrEmpty(ft)) continue;

                    bool isReadOnly  = (flags & 1) != 0;
                    bool isMultiLine = ft.Contains("Tx") && (flags & 4096) != 0;
                    bool isPushBtn   = ft.Contains("Btn") && (flags & (1 << 16)) != 0;
                    bool isRadio     = ft.Contains("Btn") && !isPushBtn && (flags & (1 << 15)) != 0;
                    bool isCheckBox  = ft.Contains("Btn") && !isPushBtn && !isRadio;

                    // A button widget that fires an action (navigation /GoTo, /URI, JavaScript, ...) is a
                    // pushbutton/link, not a fillable control. Some PDFs - e.g. manuals with a clickable
                    // page index down one side - omit the pushbutton flag, which would otherwise make every
                    // one of those render as a spurious checkbox. Treat any actioned button as a pushbutton.
                    // (A real checkbox/radio always carries an /AS appearance state; a pushbutton does not.)
                    if (ft.Contains("Btn") && (isPushBtn || ann.Elements["/A"] is not null || ann.Elements["/AS"] is null))
                        continue;

                    // Extract the "on" value for this widget (radio/checkbox selected state).
                    // Found in /AP /N as the key that is NOT /Off.
                    string onValue = "/Yes";
                    try
                    {
                        var apDict = ann.Elements.GetDictionary("/AP");
                        var nDict  = apDict?.Elements.GetDictionary("/N");
                        if (nDict is not null)
                            foreach (var k in nDict.Elements.Keys)
                                if (k != "/Off") { onValue = k; break; }
                    }
                    catch { }

                    int objNum = GetObjectNumber(elem);
                    if (objNum < 0)
                        objNum = -(pageIndex * 10000 + i); // synthetic key for inline dicts

                    // Font size the field asks for (points) and the page's render scale, so the
                    // overlay can size text the way the form intends rather than guessing from the
                    // box height (which made tall fields huge and others shrink).
                    double daFontPt = ParseDaFontSize(da);
                    double fScale   = (rotation == 90 || rotation == 270)
                        ? canvasH / pageW : canvasH / pageH;

                    result.Add(new FormFieldInfo(objNum, ft, isCheckBox, isRadio, isMultiLine,
                        name, curVal, onValue, isReadOnly, cx, cy, cw, ch, options, daFontPt, fScale));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetPageFormFields: {ex}"); }

            return result;
        }

        // Parses the font size (points) from a PDF /DA default-appearance string, e.g.
        // "/Helv 11 Tf 0 g" -> 11. Returns 0 when the size is "auto" (0) or there's no Tf operator.
        private static double ParseDaFontSize(string da)
        {
            if (string.IsNullOrWhiteSpace(da)) return 0;
            var t = da.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < t.Length; i++)
                if (t[i] == "Tf" && double.TryParse(t[i - 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double sz) && sz > 0)
                    return sz;
            return 0;
        }

        /// <summary>
        /// Writes all filled form values back into the PDF document's AcroForm field dictionaries.
        /// Called just before saving so values are persisted in the output file.
        /// </summary>
        private void WriteFormValuesToDocument()
        {
            if (_doc is null) return;
            if (_formTextValues.Count == 0 && _formCheckValues.Count == 0 && _formRadioValues.Count == 0) return;

            try
            {
                for (int p = 0; p < _doc.PageCount; p++)
                {
                    var page = _doc.Pages[p];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int i = 0; i < annotsArr.Elements.Count; i++)
                    {
                        PdfItem? elem = annotsArr.Elements[i];
                        PdfDictionary? ann = elem as PdfDictionary ?? DerefItem(elem) as PdfDictionary;
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Widget")) continue;

                        int objNum = GetObjectNumber(elem);
                        if (objNum < 0) objNum = -(p * 10000 + i);

                        // Walk parent chain to find the canonical field dict (owns /FT)
                        PdfDictionary? fieldDict = ann;
                        PdfDictionary? node = ann;
                        while (node is not null)
                        {
                            if (node.Elements["/FT"] is not null) { fieldDict = node; break; }
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        // Gather field rect for AP stream sizing
                        var rectArr = ann.Elements.GetArray("/Rect");
                        double fieldW = 100, fieldH = 20;
                        if (rectArr?.Elements.Count >= 4)
                        {
                            double rx1 = rectArr.Elements.GetReal(0), ry1 = rectArr.Elements.GetReal(1);
                            double rx2 = rectArr.Elements.GetReal(2), ry2 = rectArr.Elements.GetReal(3);
                            fieldW = Math.Abs(rx2 - rx1);
                            fieldH = Math.Abs(ry2 - ry1);
                        }

                        // Resolve /DA for font name/size (walk parent chain)
                        string? daStr = null;
                        node = ann;
                        while (node is not null && daStr is null)
                        {
                            if (node.Elements["/DA"] is PdfString ds) daStr = ds.Value;
                            var pi = node.Elements["/Parent"];
                            if (pi is null) break;
                            node = pi as PdfDictionary ?? DerefItem(pi) as PdfDictionary;
                        }

                        if (_formTextValues.TryGetValue(objNum, out var textVal) && fieldDict is not null)
                        {
                            fieldDict.Elements["/V"] = new PdfString(textVal);
                            // Bake a per-field font-size override (from the size stepper) into the
                            // field's /DA so the saved appearance and any later editor use it.
                            if (_formFontSizes.TryGetValue(objNum, out var ovPt) && ovPt > 0)
                            {
                                daStr = WithDaFontSize(daStr, ovPt);
                                fieldDict.Elements["/DA"] = new PdfString(daStr);
                            }
                            GenerateTextFieldAppearance(ann, textVal, daStr, fieldW, fieldH);
                        }
                        else if (_formCheckValues.TryGetValue(objNum, out var checkVal) && fieldDict is not null)
                        {
                            string onVal = "/Yes";
                            try
                            {
                                var apDict = ann.Elements.GetDictionary("/AP");
                                var nDict  = apDict?.Elements.GetDictionary("/N");
                                if (nDict is not null)
                                    foreach (var k in nDict.Elements.Keys)
                                        if (k != "/Off") { onVal = k; break; }
                            }
                            catch { }

                            fieldDict.Elements["/V"]  = new PdfName(checkVal ? onVal : "/Off");
                            fieldDict.Elements["/AS"] = new PdfName(checkVal ? onVal : "/Off");
                            ann.Elements["/AS"]        = new PdfName(checkVal ? onVal : "/Off");
                            GenerateCheckBoxAppearance(ann, checkVal, onVal, fieldW, fieldH);
                        }
                        else if (_formRadioValues.Count > 0 && fieldDict is not null)
                        {
                            // Radio button: look up by field name (shared across all widgets in the group)
                            string ft2 = fieldDict.Elements["/FT"]?.ToString() ?? "";
                            if (ft2.Contains("Btn"))
                            {
                                // Walk to find /T on the parent field node
                                string fieldName2 = "";
                                var n2 = fieldDict;
                                while (n2 is not null && string.IsNullOrEmpty(fieldName2))
                                {
                                    if (n2.Elements["/T"] is PdfString ts2) fieldName2 = ts2.Value;
                                    var pi2 = n2.Elements["/Parent"];
                                    if (pi2 is null) break;
                                    n2 = pi2 as PdfDictionary ?? DerefItem(pi2) as PdfDictionary;
                                }
                                if (_formRadioValues.TryGetValue(fieldName2, out var radioSel))
                                {
                                    // Set /V on the parent field
                                    fieldDict.Elements["/V"] = new PdfName(radioSel);
                                    // Set /AS on this widget to show selected or off
                                    string onVal2 = "/Yes";
                                    try
                                    {
                                        var apD = ann.Elements.GetDictionary("/AP");
                                        var nD  = apD?.Elements.GetDictionary("/N");
                                        if (nD is not null)
                                            foreach (var k in nD.Elements.Keys)
                                                if (k != "/Off") { onVal2 = k; break; }
                                    }
                                    catch { }
                                    ann.Elements["/AS"] = new PdfName(onVal2 == radioSel ? onVal2 : "/Off");
                                }
                            }
                        }
                    }
                }

                // Belt-and-suspenders: also set NeedAppearances in case any AP generation failed
                try
                {
                    var acroForm = _doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                    if (acroForm is not null)
                        acroForm.Elements["/NeedAppearances"] = new PdfBoolean(true);
                }
                catch { }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WriteFormValuesToDocument: {ex}"); }
        }

        /// <summary>
        /// Generates a /AP /N form XObject appearance stream for a text field and sets it
        /// on the widget annotation. Uses reflection to access PdfSharpCore's internal
        /// PdfDictionary.PdfStream constructor since there is no public factory method.
        /// </summary>
        private void GenerateTextFieldAppearance(PdfDictionary widgetAnn, string text, string? da, double fieldW, double fieldH)
        {
            try
            {
                var (fontName, fontSize) = ParseDaString(da);
                if (fontSize <= 0) fontSize = Math.Max(6, Math.Min(fieldH * 0.65, 12));
                fontSize = Math.Max(6, Math.Min(fontSize, fieldH * 0.85));

                // Vertical centering: PDF baseline is measured from bottom of the field rect.
                double textY = (fieldH - fontSize) / 2 + fontSize * 0.2;
                if (textY < 1) textY = 1;

                string escaped = EscapePdfString(text);
                string content =
                    $"/Tx BMC\nq\n0 0 {fieldW:F2} {fieldH:F2} re W n\n" +
                    $"BT\n{fontName} {fontSize:F2} Tf\n0 g\n2 {textY:F2} Td\n({escaped}) Tj\nET\nQ\nEMC";

                var xobj = BuildFormXObject(fontName, fieldW, fieldH, content);
                if (xobj is null) return;

                AttachAppearance(widgetAnn, xobj);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateTextFieldAppearance: {ex}"); }
        }

        /// <summary>
        /// Generates /AP /N (checked) and /AP /Off (unchecked) appearance streams for a
        /// checkbox widget and sets them on the annotation.
        /// </summary>
#pragma warning disable IDE0060 // isChecked unused — both AP states are always generated; /AS selects the active one
        private void GenerateCheckBoxAppearance(PdfDictionary widgetAnn, bool isChecked, string onVal, double fieldW, double fieldH)
#pragma warning restore IDE0060
        {
            try
            {
                double m = Math.Min(fieldW, fieldH) * 0.1; // margin
                double iw = fieldW - m * 2;
                double ih = fieldH - m * 2;

                // Checked: ZapfDingbats "4" = ✔, centred in the field
                double fs = Math.Min(iw, ih) * 0.85;
                double tx = (fieldW - fs * 0.6) / 2;
                double ty = (fieldH - fs) / 2 + fs * 0.15;

                string checkedContent =
                    $"q\nBT\n/ZaDb {fs:F2} Tf\n0 g\n{tx:F2} {ty:F2} Td\n(4) Tj\nET\nQ";

                string offContent = "q\nQ"; // empty — just clears

                // /Resources needs ZapfDingbats font for the checked state
                var checkedXobj = BuildFormXObject("/ZaDb", fieldW, fieldH, checkedContent, isZaDb: true);
                var offXobj     = BuildFormXObject("/ZaDb", fieldW, fieldH, offContent,     isZaDb: true);
                if (checkedXobj is null || offXobj is null) return;

                // /AP dictionary with /N being a sub-dict keyed by state name
                var nDict = new PdfDictionary(_doc);
                nDict.Elements[onVal]  = checkedXobj.Reference;
                nDict.Elements["/Off"] = offXobj.Reference;

                var apDict = new PdfDictionary(_doc);
                apDict.Elements["/N"] = nDict;

                widgetAnn.Elements["/AP"] = apDict;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GenerateCheckBoxAppearance: {ex}"); }
        }

        /// <summary>
        /// Creates an indirect PdfDictionary stream object representing a Form XObject,
        /// suitable for use as an /AP /N appearance stream.
        /// </summary>
        private PdfDictionary? BuildFormXObject(string fontName, double w, double h, string content, bool isZaDb = false)
        {
            byte[] bytes = System.Text.Encoding.GetEncoding("iso-8859-1").GetBytes(content);

            var xobj = new PdfDictionary(_doc);
            xobj.Elements["/Type"]     = new PdfName("/XObject");
            xobj.Elements["/Subtype"]  = new PdfName("/Form");
            xobj.Elements["/FormType"] = new PdfInteger(1);

            var bbox = new PdfArray(_doc);
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(0));
            bbox.Elements.Add(new PdfReal(w));
            bbox.Elements.Add(new PdfReal(h));
            xobj.Elements["/BBox"] = bbox;

            // Inline font resource — avoids adding top-level objects for every field.
            var fontEntry = new PdfDictionary(_doc);
            fontEntry.Elements["/Type"]    = new PdfName("/Font");
            fontEntry.Elements["/Subtype"] = new PdfName("/Type1");
            fontEntry.Elements["/BaseFont"] = isZaDb
                ? new PdfName("/ZapfDingbats")
                : new PdfName("/Helvetica");
            if (!isZaDb)
                fontEntry.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");

            var fontDict = new PdfDictionary(_doc);
            fontDict.Elements[fontName] = fontEntry;

            var res = new PdfDictionary(_doc);
            res.Elements["/Font"] = fontDict;
            xobj.Elements["/Resources"] = res;

            if (!TryAttachStreamBytes(xobj, bytes)) return null;

            _doc!.Internals.AddObject(xobj);
            return xobj;
        }

        /// <summary>
        /// Sets /AP /N on a widget annotation to the given form XObject (indirect ref).
        /// Replaces any existing AP entry.
        /// </summary>
        private static void AttachAppearance(PdfDictionary widgetAnn, PdfDictionary xobj)
        {
            var apDict = new PdfDictionary();
            apDict.Elements["/N"] = xobj.Reference;
            widgetAnn.Elements["/AP"] = apDict;
        }

        /// <summary>
        /// Attaches raw content bytes to a PdfDictionary as a stream.
        /// Accesses PdfDictionary.PdfStream via reflection because its constructor is internal.
        /// Falls back to the backing field if the property setter is protected.
        /// </summary>
        private static bool TryAttachStreamBytes(PdfDictionary dict, byte[] bytes)
        {
            try
            {
                var dictType   = typeof(PdfDictionary);
                var streamType = dictType.GetNestedType("PdfStream",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (streamType is null) return false;

                // Try (byte[], PdfDictionary) ctor first, then (byte[]) only
                System.Reflection.ConstructorInfo? ctor =
                    streamType.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, [typeof(byte[]), typeof(PdfDictionary)], null) ??
                    streamType.GetConstructor(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                        null, [typeof(byte[])], null);
                if (ctor is null) return false;

                object streamObj = ctor.GetParameters().Length == 2
                    ? ctor.Invoke([bytes, dict])
                    : ctor.Invoke([bytes]);

                // Try public Stream property setter first
                var prop = dictType.GetProperty("Stream",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop?.CanWrite == true)
                {
                    prop.SetValue(dict, streamObj);
                    return true;
                }

                // Fall back to the backing field
                var field = dictType.GetField("_stream",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field is not null)
                {
                    field.SetValue(dict, streamObj);
                    return true;
                }

                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Parses a PDF Default Appearance string ("/Helv 12 Tf 0 g") to extract
        /// the font resource name and point size.
        /// </summary>
        private static (string fontName, double fontSize) ParseDaString(string? da)
        {
            string fontName = "/Helv";
            double fontSize = 0;
            if (string.IsNullOrWhiteSpace(da)) return (fontName, fontSize);

            var tokens = da!.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < tokens.Length; i++)
            {
                if (tokens[i + 2] == "Tf" &&
                    double.TryParse(tokens[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double fs))
                {
                    fontName = tokens[i]; // e.g. "/Helv"
                    fontSize = fs;
                    break;
                }
            }
            return (fontName, fontSize);
        }

        /// <summary>
        /// Escapes a string for use in a PDF literal string (parentheses syntax).
        /// </summary>
        private static string EscapePdfString(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '(':  sb.Append("\\(");  break;
                    case ')':  sb.Append("\\)");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    default:
                        // Keep Latin-1 range; replace anything outside with '?'
                        sb.Append(c < 256 ? c : '?');
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// The container uses Background=null so non-link areas are hit-test-transparent
        /// and clicks fall through to the full-page nav overlay beneath it.  Link
        /// overlays inside the container use Background=Transparent so they ARE hit-
        /// testable and receive clicks.  The container is added last → topmost z-order.
        /// </summary>
        private void AddSecondaryPageLinks(int pageIndex, Grid pageGrid, int bitmapW, int bitmapH)
        {
            var links = GetPageLinks(pageIndex, bitmapW, bitmapH);
            if (links.Count == 0) return;

            // Container: not hit-testable itself (Background=null), but its children are.
            var linkCanvas = new Canvas { Width = bitmapW, Height = bitmapH, Background = null };

            foreach (var lnk in links)
            {
                var lo = new Canvas
                {
                    Width            = lnk.Cw,
                    Height           = lnk.Ch,
                    Background       = Brushes.Transparent,   // must be non-null to be hittable
                    Cursor           = Cursors.Hand,
                    ToolTip          = lnk.Tip,
                    IsHitTestVisible = true,
                };
                Canvas.SetLeft(lo, lnk.Cx);   // works because parent IS a Canvas
                Canvas.SetTop(lo, lnk.Cy);

                var capturedTag = lnk.Tag;
                lo.PreviewMouseLeftButtonDown += (_snd, args) =>
                {
                    // Links only follow with the Select tool, and never when the click is on an
                    // annotation. Otherwise forward to the page overlay so tools/selection work over a
                    // link region instead of the link eating the click (those orphan-scan PDFs embed a
                    // page-spanning site link).
                    var annCanvas = _continuousCanvases.TryGetValue(pageIndex, out var ac) ? ac : null;
                    bool follow = _currentTool == EditTool.Select;
                    if (follow && annCanvas is not null)
                    {
                        var pt = args.GetPosition(annCanvas);
                        if (_annotations.TryGetValue(pageIndex, out var al) && al.Any(a => HitTestAnnotation(a, pt, out _)))
                            follow = false;
                    }
                    if (!follow)
                    {
                        if (annCanvas is not null) Canvas_MouseLeftButtonDown(annCanvas, args);
                        return;
                    }
                    if (capturedTag is int tp)
                        PageList.SelectedIndex = tp;
                    else if (capturedTag is string u)
                        try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                    args.Handled = true;
                };

                linkCanvas.Children.Add(lo);
            }

            // Add container last so it is topmost in z-order; non-link areas fall through.
            pageGrid.Children.Add(linkCanvas);
        }

        /// <summary>
        /// Resolves a /Dest value (PdfArray, PdfString, or PdfName) to a 0-based page index.
        /// Returns null if the destination cannot be resolved.
        /// Note: PdfReference is internal to PdfSharpCore so we use reflection for ObjectNumber
        /// and var-inferred types instead of the type name.
        /// </summary>
        private int? ResolveDest(PdfItem? destItem)
        {
            if (destItem is null || _doc is null) return null;

            // Dereference indirect object if needed (PdfReference is internal, use duck-typing).
            destItem = DerefItem(destItem);

            PdfArray? arr = null;

            if (destItem is PdfArray a)
            {
                arr = a;
            }
            else if (destItem is PdfString || destItem is PdfName)
            {
                // Named destination — look up in the document catalog
                arr = ResolveNamedDest(destItem);
            }

            if (arr is null || arr.Elements.Count == 0) return null;

            // First element of the destination array is an indirect page reference.
            // PdfReference.ObjectNumber is public but its type is internal; use reflection.
            var pageRefItem = arr.Elements[0];
            int elemObjNum = GetObjectNumber(pageRefItem);
            if (elemObjNum > 0)
            {
                for (int i = 0; i < _doc.PageCount; i++)
                {
                    // PdfPage.Reference (public) gives us access to ObjectNumber
                    var pgRef = _doc.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == elemObjNum)
                        return i;
                }
            }
            else if (pageRefItem is PdfInteger pageInt)
            {
                int pn = pageInt.Value;
                if (pn >= 0 && pn < _doc.PageCount) return pn;
            }

            return null;
        }

        /// <summary>
        /// Dereferences a PdfItem if it is an indirect reference (PdfReference is internal;
        /// we detect it by looking for a public "Value" property returning PdfObject).
        /// </summary>
        private static PdfItem DerefItem(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved)
                return resolved;
            return item;
        }

        /// <summary>
        /// Returns the PDF object number of a PdfItem that is an indirect reference, or -1.
        /// Handles the internal PdfReference type via reflection.
        /// </summary>
        private static int GetObjectNumber(PdfItem? item)
        {
            if (item is null) return -1;
            var prop = item.GetType().GetProperty("ObjectNumber",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return prop?.GetValue(item) is int n ? n : -1;
        }

        /// <summary>
        /// Resolves a named destination (string or name) to a destination array using the
        /// catalog's /Dests dictionary or /Names /Dests name tree.
        /// </summary>
        private PdfArray? ResolveNamedDest(PdfItem nameItem)
        {
            if (_doc is null) return null;
            string name = nameItem switch
            {
                PdfString s => s.Value,
                PdfName   n => n.Value.TrimStart('/'),
                _           => ""
            };
            if (string.IsNullOrEmpty(name)) return null;

            var catalog = _doc.Internals.Catalog;

            // Legacy /Dests dictionary (direct mapping)
            var dests = catalog.Elements.GetDictionary("/Dests");
            if (dests != null)
            {
                PdfItem? val = DerefItem(dests.Elements[name] ?? dests.Elements["/" + name] ?? new PdfInteger(-1));
                if (val is PdfArray da) return da;
                if (val is PdfDictionary dd) return dd.Elements.GetArray("/D");
            }

            // Modern /Names /Dests name tree
            var names = catalog.Elements.GetDictionary("/Names");
            var destTree = names?.Elements.GetDictionary("/Dests");
            if (destTree != null)
                return ResolveNameTree(destTree, name);

            return null;
        }

        /// <summary>
        /// Walks a PDF name tree to find the destination array for the given name.
        /// </summary>
        private static PdfArray? ResolveNameTree(PdfDictionary node, string name)
        {
            // Leaf node: flat /Names array [key val key val ...]
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var key = namesArr.Elements[i];
                    string keyStr = key is PdfString ks ? ks.Value : key?.ToString() ?? "";
                    if (keyStr == name)
                    {
                        PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                        if (val is PdfArray va) return va;
                        if (val is PdfDictionary vd) return vd.Elements.GetArray("/D");
                    }
                }
            }

            // Intermediate node: recurse into /Kids
            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    PdfItem? kid = DerefItem(kids.Elements[i]);
                    if (kid is PdfDictionary kd)
                    {
                        var result = ResolveNameTree(kd, name);
                        if (result != null) return result;
                    }
                }
            }

            return null;
        }

        // ============================================================
        // Tool selection
        // ============================================================

        // Maps an editing tool to its mouse cursor. Shared by SetTool and by the
        // per-page overlay creation so freshly rendered tiles get the right cursor.
        private static Cursor CursorForTool(EditTool tool) => tool switch
        {
            EditTool.Text => Cursors.IBeam,
            EditTool.Highlight => Cursors.Cross,
            EditTool.Strikethrough => Cursors.Cross,
            EditTool.Underline => Cursors.Cross,
            EditTool.Draw => Cursors.Pen,
            EditTool.Signature => Cursors.Pen,
            EditTool.Image => Cursors.Hand,
            EditTool.Crop => Cursors.Cross,
            _ => Cursors.Arrow
        };

        private void SetTool(EditTool tool)
        {
            // Re-clicking the tool that owns the visible annotate bar tucks the bar away (or brings it
            // back) instead of rebuilding it - no flicker, and a quick way to get it out of the way.
            bool reclickedAnnotTool = tool == _currentTool && tool == _annotBarTool
                && (_textSettingsBar is not null || _drawSettingsBar is not null);
            if (reclickedAnnotTool)
            {
                ToggleAnnotBarMinimized();
                return;
            }

            // Continuous view now supports annotation tools inline via per-page overlays.
            CommitActiveTextBox();
            ClearTextSelection();
            _currentTool = tool;

            var map = new (Button btn, EditTool t)[]
            {
                (_toolSelectBtn, EditTool.Select),
                (_toolTextBtn, EditTool.Text),
                (_toolHighlightBtn, EditTool.Highlight),
                (_toolStrikeBtn, EditTool.Strikethrough),
                (_toolUnderlineBtn, EditTool.Underline),
                (_toolDrawBtn, EditTool.Draw),
                (_toolSignatureBtn, EditTool.Signature),
                (_toolImageBtn, EditTool.Image),
                (_toolCropBtn, EditTool.Crop)
            };
            foreach (var (btn, t) in map)
            {
                if (t == tool)
                {
                    btn.SetResourceReference(Control.BackgroundProperty, "SelectionBg");
                    btn.SetResourceReference(Control.ForegroundProperty, "SelectionFg");
                }
                else
                {
                    // Clear local values so the ToolbarButton style (incl. its hover trigger) applies.
                    // Setting Background locally here would override the style and kill the hover.
                    btn.ClearValue(Control.BackgroundProperty);
                    btn.ClearValue(Control.ForegroundProperty);
                }
            }

            // Apply the tool cursor to every page surface, not just the primary page.
            // In Grid / Two-Page / Continuous modes the secondary tiles are separate
            // overlay canvases tracked in _continuousCanvases; without this they keep
            // the default arrow cursor while only page 1 (_annotationCanvas) updates.
            var toolCursor = CursorForTool(tool);
            _annotationCanvas.Cursor = toolCursor;
            foreach (var overlay in _continuousCanvases.Values)
                overlay.Cursor = toolCursor;

            // Show/hide draw settings bar
            if (tool == EditTool.Draw || tool == EditTool.Highlight
                || tool == EditTool.Strikethrough || tool == EditTool.Underline)
                ShowDrawSettings(tool);
            else
                HideDrawSettings();

            // Show/hide text tool settings bar
            if (tool == EditTool.Text)
                ShowTextSettings();
            else
                HideTextSettings();

            // Hide signature popup when switching away
            if (tool != EditTool.Signature)
            {
                HideSignaturePopup();
                _pendingSignature = null;
            }

            // Dismiss crop confirm bar when switching away from Crop
            if (tool != EditTool.Crop)
                HideCropConfirmBar();
        }

        private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            if (_sidebarCollapsed)
            {
                // Save current width before collapsing so expand restores it.
                if (_sidebarCol.ActualWidth > 24)
                {
                    if (_sidebarShowingOutlines)
                        _savedOutlinesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxOutlines);
                    else
                        _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxPages);
                }
                _sidebarToggleBtn.ToolTip = Loc("Str_TT_ExpandSidebar");
                _sidebarBorder.Visibility = Visibility.Collapsed;
                _sidebarCol.Width = new GridLength(24);
                _sidebarCol.MinWidth = 24;
                // Splitter stays enabled so the user can grab it and drag the sidebar back open.
            }
            else
            {
                _sidebarBorder.Visibility = Visibility.Visible;
                double restore = _sidebarShowingOutlines ? _savedOutlinesWidth : _savedPagesWidth;
                _sidebarCol.Width = new GridLength(restore);
                _sidebarCol.MinWidth = SidebarMinOpen;   // open: clamp so the list can't be dragged below readable
                _sidebarToggleBtn.ToolTip = Loc("Str_TT_CollapseSidebar");
                SidebarSplitter.IsEnabled = true;
            }
            UpdateSidebarToggleGlyph();
            if (PageList.SelectedIndex >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(PageList.SelectedIndex));
        }

        // Pressing the splitter while the sidebar is collapsed begins pulling it open: reveal the page
        // list (still 0-width against the 24px strip, so there's no flash) so the drag grows it live. If
        // the user doesn't drag past the threshold, OnSidebarResized snaps it shut again on release.
        private bool _sidebarDragOpening;   // true while dragging the splitter open from the collapsed strip
        private bool _sidebarWantClose;     // set during an open-resize drag when pulled past the close edge

        private void OnSidebarSplitterPress()
        {
            if (!_sidebarCollapsed) return;
            // Begin pulling open from the strip. Show the sidebar background + grain right away so the
            // growing strip matches the sidebar, but keep the content (header + list) hidden until it's
            // pulled to the readable minimum (OnSidebarSplitterMove) - so nothing shows clipped, and the
            // column grows from the 24px edge tracking the mouse with no dead zone.
            _sidebarCollapsed = false;
            _sidebarDragOpening = true;
            _sidebarBorder.Visibility = Visibility.Visible;
            SidebarContentPanel.Visibility = Visibility.Collapsed;
            _sidebarToggleBtn.ToolTip = Loc("Str_TT_CollapseSidebar");
            UpdateSidebarToggleGlyph();
        }

        // Drives the splitter drag. While opening from the strip, reveal the list only once it's past the
        // readable minimum. Once open, pulling the mouse well past the minimum closes the sidebar (the
        // MinWidth clamp already stops the column from ever shrinking below the readable floor).
        private void OnSidebarSplitterMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_sidebarCollapsed || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            if (_sidebarDragOpening)
            {
                SidebarContentPanel.Visibility = _sidebarCol.ActualWidth >= SidebarMinOpen
                    ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            // The column is clamped at the readable minimum (MinWidth), so it can't clip and the document
            // isn't resized as you pull. Just record intent; the actual close (and its single re-render)
            // happens on release in OnSidebarResized - so dragging the splitter never re-renders the page.
            double mx = e.GetPosition(MainContentGrid).X;
            const double closeBuffer = 36;   // pull this far past the minimum edge to close on release
            _sidebarWantClose = _sidebarRight
                ? mx > MainContentGrid.ActualWidth - SidebarMinOpen + closeBuffer
                : mx < SidebarMinOpen - closeBuffer;
        }

        // Called when the user finishes dragging the sidebar splitter. If they dragged it narrower than
        // the close threshold, snap it fully closed (to the toggle strip) instead of leaving an unusable
        // sliver; otherwise remember the new width for the current mode.
        private void OnSidebarResized()
        {
            _sidebarDragOpening = false;
            if (_sidebarCollapsed) { _sidebarWantClose = false; return; }
            if (_sidebarWantClose) { _sidebarWantClose = false; CollapseSidebarToStrip(); return; }
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                if (_sidebarCollapsed) return;
                double w = _sidebarCol.ActualWidth;
                if (w < SidebarMinOpen)
                {
                    // Released below the readable minimum: snap to whichever end the user let go nearer
                    // to - fully closed, or the minimum open width.
                    double mid = (24 + SidebarMinOpen) / 2.0;
                    if (w < mid) { CollapseSidebarToStrip(); return; }
                    _sidebarCol.Width = new GridLength(SidebarMinOpen);
                    w = SidebarMinOpen;
                }
                _sidebarBorder.Visibility = Visibility.Visible;        // ensure the list shows when settled open
                SidebarContentPanel.Visibility = Visibility.Visible;
                _sidebarCol.MinWidth = SidebarMinOpen;                 // clamp future resizes so they can't clip
                if (_sidebarShowingOutlines) _savedOutlinesWidth = Math.Min(w, SidebarMaxOutlines);
                else _savedPagesWidth = Math.Min(w, SidebarMaxPages);
            }));
        }

        // Collapse to the 24px toggle strip without overwriting the saved width, so re-expand restores the
        // last good size rather than the thin dragged one. Mirrors SidebarToggle_Click's collapse branch.
        private void CollapseSidebarToStrip()
        {
            _sidebarCollapsed = true;
            _sidebarToggleBtn.ToolTip = Loc("Str_TT_ExpandSidebar");
            _sidebarBorder.Visibility = Visibility.Collapsed;
            SidebarContentPanel.Visibility = Visibility.Visible;   // reset so the next border-show has content
            _sidebarCol.Width = new GridLength(24);
            _sidebarCol.MinWidth = 24;
            UpdateSidebarToggleGlyph();   // splitter stays enabled so it can be dragged back open
        }

        // ============================================================
        // Sidebar outline/bookmark panel
        // ============================================================

        private void SidebarPagesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToPagesTab();
        private void SidebarOutlinesTab_Click(object sender, RoutedEventArgs e) => SwitchSidebarToOutlinesTab();

        private const double SidebarMaxPages   = 300;
        private const double SidebarMaxOutlines = 480;
        private const double SidebarMinOpen     = 120;   // narrowest readable width before labels/header clip

        private void SwitchSidebarToPagesTab()
        {
            _sidebarShowingOutlines = false;
            PageList.Visibility = Visibility.Visible;
            OutlineScrollViewer.Visibility = Visibility.Collapsed;
            PageControlsRow.Visibility = Visibility.Visible;
            SidebarPagesTab.Foreground = (Brush)FindResource("Accent");
            SidebarOutlinesTab.Foreground = (Brush)FindResource("TextSecondary");
            // Save current outlines width before snapping back to pages.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedOutlinesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxOutlines);

            SidebarSplitter.IsEnabled = true;   // pages are resizable too now (drag the splitter)
            _sidebarCol.MaxWidth = SidebarMaxPages;
            if (!_sidebarCollapsed)
            {
                double target = _savedPagesWidth;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Render,
                    (Action)(() => _sidebarCol.Width = new GridLength(target)));
            }
        }

        private void SwitchSidebarToOutlinesTab()
        {
            // Save current pages width, then restore (or auto-fit) the outlines width.
            if (!_sidebarCollapsed && _sidebarCol.ActualWidth > 0)
                _savedPagesWidth = Math.Min(_sidebarCol.ActualWidth, SidebarMaxPages);

            _sidebarShowingOutlines = true;
            PageList.Visibility = Visibility.Collapsed;
            OutlineScrollViewer.Visibility = Visibility.Visible;
            PageControlsRow.Visibility = Visibility.Collapsed;
            SidebarPagesTab.Foreground = (Brush)FindResource("TextSecondary");
            SidebarOutlinesTab.Foreground = (Brush)FindResource("Accent");
            SidebarSplitter.IsEnabled = true;
            _sidebarCol.MaxWidth = SidebarMaxOutlines;
            if (!_sidebarCollapsed)
            {
                if (!_outlinesFitted)
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Render,
                        (Action)AutoFitOutlineWidth);
                else
                {
                    double target = _savedOutlinesWidth;
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Render,
                        (Action)(() => _sidebarCol.Width = new GridLength(target)));
                }
            }
        }

        /// <summary>
        /// Sizes the sidebar to fit the widest outline item by measuring each item's
        /// text width via FormattedText plus its indentation depth.
        /// </summary>
        private void AutoFitOutlineWidth()
        {
            if (_sidebarCollapsed) return;

            var typeface = new Typeface(
                OutlineTree.FontFamily, OutlineTree.FontStyle,
                OutlineTree.FontWeight, OutlineTree.FontStretch);
            double em  = OutlineTree.FontSize;
            double max = 0;

            void Walk(ItemCollection items, int depth)
            {
                foreach (TreeViewItem node in items)
                {
                    var ft = new System.Windows.Media.FormattedText(
                        node.Header?.ToString() ?? string.Empty,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, em, Brushes.White,
                        /*pixelsPerDip*/ 1.0);
                    // 19 px indent per level + 19 px toggle + text + 12 px item padding
                    double w = depth * 19.0 + 19.0 + ft.Width + 12.0;
                    if (w > max) max = w;
                    if (node.Items.Count > 0)
                        Walk(node.Items, depth + 1);
                }
            }

            Walk(OutlineTree.Items, 0);

            // TreeView outer padding (8 px) + sidebar margins + scrollbar gutter (~36 px)
            double target = Math.Max(160.0, Math.Min(max + 44.0, SidebarMaxOutlines));
            _savedOutlinesWidth = target;
            _outlinesFitted     = true;
            _sidebarCol.Width   = new GridLength(target);
        }

        private void OutlineTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem item && item.Tag is int pageIdx && pageIdx >= 0 && _doc is not null)
            {
                if (pageIdx < _doc.PageCount)
                    PageList.SelectedIndex = pageIdx;
            }
        }

        // The TreeView's own scroll viewer swallows the wheel before the outer one sees it, so the
        // Outlines list wouldn't scroll. Forward the wheel to the outer scroll viewer.
        private void OutlineScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            OutlineScrollViewer.ScrollToVerticalOffset(OutlineScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private void LoadOutlines()
        {
            _outlinesFitted = false;   // triggers auto-fit on next tab switch
            OutlineTree.Items.Clear();
            try
            {
                var outlines = _doc?.Outlines;
                if (outlines is null || outlines.Count == 0)
                {
                    SidebarOutlinesTab.IsEnabled = false;
                    return;
                }
                SidebarOutlinesTab.IsEnabled = true;
                AddOutlineItems(OutlineTree.Items, outlines);
            }
            catch
            {
                // Malformed outline — show a placeholder and don't crash
                SidebarOutlinesTab.IsEnabled = false;
            }
        }

        private void AddOutlineItems(ItemCollection target, PdfSharpCore.Pdf.PdfOutlineCollection outlines)
        {
            foreach (PdfSharpCore.Pdf.PdfOutline outline in outlines)
            {
                int pageIdx = GetOutlinePageIndex(outline);
                var item = new TreeViewItem
                {
                    Header = string.IsNullOrEmpty(outline.Title) ? "(untitled)" : outline.Title,
                    IsExpanded = true,
                    Tag = pageIdx,
                    ToolTip = pageIdx >= 0 ? $"Page {pageIdx + 1}" : null,
                    Style = (Style)FindResource("OutlineItemStyle")
                };
                if (outline.Outlines is not null && outline.Outlines.Count > 0)
                    AddOutlineItems(item.Items, outline.Outlines);
                target.Add(item);
            }
        }

        private int GetOutlinePageIndex(PdfSharpCore.Pdf.PdfOutline outline)
        {
            if (outline.DestinationPage is PdfSharpCore.Pdf.PdfPage destPage)
            {
                for (int i = 0; i < _doc!.PageCount; i++)
                    if (ReferenceEquals(_doc.Pages[i], destPage)) return i;
            }
            return -1;
        }

        private void ToolSelect_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Select);
        private void ToolText_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Text);
        private void ToolHighlight_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Highlight);
        private void ToolStrike_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Strikethrough);
        private void ToolUnderline_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Underline);
        private void ToolDraw_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Draw);
        private void ToolImage_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Image);
        private void ToolCrop_Click(object sender, RoutedEventArgs e) => SetTool(EditTool.Crop);
        private void ToolSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_signaturePopup is not null)
            {
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
                return;
            }
            SetTool(EditTool.Signature);
            ShowSignaturePopup();
        }

        // ============================================================
        // Crop tool
        // ============================================================

        // ── Crop coordinate helpers ──────────────────────────────────────────
        //
        // The rendered canvas already incorporates the user-applied rotation stored
        // in _pageRotations.  These helpers invert / apply the same transforms that
        // the link-overlay code uses (lines ~1925-1957), so canvas↔PDF coords are
        // consistent with how Docnet drew the bitmap.
        //
        //  rot=0:   canvas_x = native_x * cW/pW,  canvas_y = (pH - native_y) * cH/pH
        //  rot=90:  canvas_x = native_y * cW/pH,  canvas_y = native_x * cH/pW
        //  rot=180: canvas_x = (pW - nx) * cW/pW, canvas_y = (pH - ny) * cH/pH
        //  rot=270: canvas_x = (pH - ny) * cW/pH, canvas_y = (pW - nx) * cH/pW
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Convert a canvas-space <see cref="Rect"/> to PDF CropBox coordinates
        /// (bottom-left origin, points) with rotation awareness.
        /// </summary>
        private static (double x1, double y1, double x2, double y2) CanvasToPdfRect(
            Rect cr, double pdfW, double pdfH, double canvasW, double canvasH, int rot)
        {
            double cx = cr.X, cy = cr.Y, cw = cr.Width, ch = cr.Height;
            return rot switch
            {
                90  => (cy      * pdfW / canvasH,
                        cx      * pdfH / canvasW,
                       (cy + ch) * pdfW / canvasH,
                       (cx + cw) * pdfH / canvasW),

                180 => (pdfW - (cx + cw) * pdfW / canvasW,
                        pdfH - (cy + ch) * pdfH / canvasH,
                        pdfW -  cx       * pdfW / canvasW,
                        pdfH -  cy       * pdfH / canvasH),

                270 => (pdfW - (cy + ch) * pdfW / canvasH,
                        pdfH - (cx + cw) * pdfH / canvasW,
                        pdfW -  cy       * pdfW / canvasH,
                        pdfH -  cx       * pdfH / canvasW),

                _   => (cx       * pdfW / canvasW,           // 0°
                        pdfH - (cy + ch) * pdfH / canvasH,
                       (cx + cw) * pdfW / canvasW,
                        pdfH -  cy       * pdfH / canvasH),
            };
        }

        /// <summary>
        /// Inverse of <see cref="CanvasToPdfRect"/> — map PDF CropBox coords back to a canvas-space
        /// <see cref="Rect"/>.
        /// </summary>
        private static Rect PdfToCanvasRect(
            double x1, double y1, double x2, double y2,
            double pdfW, double pdfH, double canvasW, double canvasH, int rot)
        {
            double cx, cy, cw, ch;
            switch (rot)
            {
                case 90:
                    cx = y1 * canvasW / pdfH;
                    cy = x1 * canvasH / pdfW;
                    cw = (y2 - y1) * canvasW / pdfH;
                    ch = (x2 - x1) * canvasH / pdfW;
                    break;
                case 180:
                    cx = (pdfW - x2) * canvasW / pdfW;
                    cy = (pdfH - y2) * canvasH / pdfH;
                    cw = (x2 - x1)   * canvasW / pdfW;
                    ch = (y2 - y1)   * canvasH / pdfH;
                    break;
                case 270:
                    cx = (pdfH - y2) * canvasW / pdfH;
                    cy = (pdfW - x2) * canvasH / pdfW;
                    cw = (y2 - y1)   * canvasW / pdfH;
                    ch = (x2 - x1)   * canvasH / pdfW;
                    break;
                default: // 0°
                    cx = x1 * canvasW / pdfW;
                    cy = (pdfH - y2) * canvasH / pdfH;
                    cw = (x2 - x1)  * canvasW / pdfW;
                    ch = (y2 - y1)  * canvasH / pdfH;
                    break;
            }
            return new Rect(Math.Max(0, cx), Math.Max(0, cy),
                            Math.Max(10, cw), Math.Max(10, ch));
        }

        /// <summary>
        /// Push current <see cref="_cropCanvasRect"/> → PDF coords into the x1/y1/x2/y2 TextBoxes.
        /// No-ops when the TextBoxes haven't been created yet (confirm bar not showing).
        /// </summary>
        private void SyncCropBoxInputs()
        {
            if (_cropX1Box is null || _doc is null) return;
            int pi = PageList.SelectedIndex;
            if (pi < 0 || !_renderDims.TryGetValue(pi, out var dims)) return;
            _pageRotations.TryGetValue(pi, out int rot);
            var page = _doc.Pages[pi];
            double pdfW = page.Width.Point, pdfH = page.Height.Point;

            var (x1, y1, x2, y2) = CanvasToPdfRect(_cropCanvasRect, pdfW, pdfH, dims.w, dims.h, rot);
            x1 = Math.Max(0,    x1); y1 = Math.Max(0,    y1);
            x2 = Math.Min(pdfW, x2); y2 = Math.Min(pdfH, y2);

            _updatingCropInputs = true;
            _cropX1Box.Text  = $"{x1:F1}";
            _cropY1Box!.Text = $"{y1:F1}";
            _cropX2Box!.Text = $"{x2:F1}";
            _cropY2Box!.Text = $"{y2:F1}";
            _updatingCropInputs = false;
        }

        /// <summary>
        /// Read x1/y1/x2/y2 TextBoxes → update <see cref="_cropCanvasRect"/> and visuals.
        /// Called on Enter-key or LostFocus inside each TextBox.
        /// </summary>
        private void CommitCropBoxInput()
        {
            if (_updatingCropInputs || _cropX1Box is null || _doc is null) return;
            int pi = PageList.SelectedIndex;
            if (pi < 0 || !_renderDims.TryGetValue(pi, out var dims)) return;
            if (!double.TryParse(_cropX1Box.Text,  out double x1)) return;
            if (!double.TryParse(_cropY1Box!.Text, out double y1)) return;
            if (!double.TryParse(_cropX2Box!.Text, out double x2)) return;
            if (!double.TryParse(_cropY2Box!.Text, out double y2)) return;

            _pageRotations.TryGetValue(pi, out int rot);
            var page = _doc.Pages[pi];
            double pdfW = page.Width.Point, pdfH = page.Height.Point;

            x1 = Math.Max(0,       Math.Min(pdfW - 1, x1));
            y1 = Math.Max(0,       Math.Min(pdfH - 1, y1));
            x2 = Math.Max(x1 + 1,  Math.Min(pdfW,     x2));
            y2 = Math.Max(y1 + 1,  Math.Min(pdfH,     y2));

            _cropCanvasRect = PdfToCanvasRect(x1, y1, x2, y2, pdfW, pdfH, dims.w, dims.h, rot);
            UpdateCropRectVisuals();
        }

        /// <summary>
        /// Parse a page-range string like "1-3,5,7-9" (1-based) into a zero-based index array.
        /// Returns <c>null</c> on parse error or if no valid pages are produced.
        /// </summary>
        private static int[]? ParsePageRange(string input, int pageCount)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var result = new System.Collections.Generic.HashSet<int>();
            foreach (var part in input.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = part.Trim();
                if (seg.Contains('-'))
                {
                    var halves = seg.Split('-');
                    if (halves.Length == 2 &&
                        int.TryParse(halves[0].Trim(), out int lo) &&
                        int.TryParse(halves[1].Trim(), out int hi))
                    {
                        for (int p = lo; p <= hi; p++)
                            if (p >= 1 && p <= pageCount) result.Add(p - 1);
                    }
                    else return null;
                }
                else if (int.TryParse(seg, out int pg))
                {
                    if (pg >= 1 && pg <= pageCount) result.Add(pg - 1);
                }
                else return null;
            }
            return result.Count == 0 ? null : [.. result.OrderBy(x => x)];
        }

        // ─────────────────────────────────────────────────────────────────────

        private void ShowCropConfirmBar()
        {
            // Save the preview rect — HideCropConfirmBar removes it, but we need it to persist.
            var savedRect = _cropPreviewRect;
            _cropPreviewRect = null;
            HideCropConfirmBar();
            _cropPreviewRect = savedRect;
            // Remove fill once confirmed — keep only the outline.
            if (_cropPreviewRect is not null)
                _cropPreviewRect.Fill = Brushes.Transparent;
            if (_doc is null) return;

            int currentPage = _cropPageIndex >= 0 ? _cropPageIndex : PageList.SelectedIndex;
            bool multiPage  = _doc.PageCount > 1;

            var bar = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(8, 6, 8, 6)
            };
            bar.SetResourceReference(Border.BackgroundProperty,  "BgModal");
            bar.SetResourceReference(Border.BorderBrushProperty, "Accent");

            var outer = new StackPanel { Orientation = Orientation.Vertical };

            // ── Row 1: CropBox coordinate inputs ────────────────────────────────
            var inputRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 5)
            };

            var headerLbl = new TextBlock
            {
                Text              = "CropBox (pts):",
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0)
            };
            headerLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            inputRow.Children.Add(headerLbl);

            // Helper: adds a label + TextBox pair to inputRow and returns the TextBox
            TextBox AddCoordField(string lbl)
            {
                var fieldLbl = new TextBlock
                {
                    Text              = lbl,
                    FontFamily        = new FontFamily("Segoe UI"),
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 2, 0)
                };
                fieldLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                inputRow.Children.Add(fieldLbl);
                var tb = new TextBox
                {
                    Width             = 52,
                    Height            = 22,
                    FontFamily        = new FontFamily("Segoe UI"),
                    FontSize          = 11,
                    BorderThickness   = new Thickness(1),
                    Padding           = new Thickness(3, 1, 3, 1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 6, 0)
                };
                tb.SetResourceReference(TextBox.BackgroundProperty,  "BgPanel");
                tb.SetResourceReference(TextBox.ForegroundProperty,  "TextPrimary");
                tb.SetResourceReference(TextBox.BorderBrushProperty, "BorderDim");
                tb.KeyDown   += (_, e) => { if (e.Key == Key.Enter) { CommitCropBoxInput(); e.Handled = true; } };
                tb.LostFocus += (_, _) => CommitCropBoxInput();
                inputRow.Children.Add(tb);
                return tb;
            }

            _cropX1Box = AddCoordField("x1:");
            _cropY1Box = AddCoordField("y1:");
            _cropX2Box = AddCoordField("x2:");
            _cropY2Box = AddCoordField("y2:");

            outer.Children.Add(inputRow);

            // ── Row 2: Apply / remove / cancel buttons ───────────────────────────
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };

            // Base button style - only non-theme values; theme colors set via
            // SetResourceReference on each instance so they update on theme switches.
            var btnStyle = new Style(typeof(Button));
            btnStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            btnStyle.Setters.Add(new Setter(Button.PaddingProperty,   new Thickness(8, 3, 8, 3)));
            btnStyle.Setters.Add(new Setter(Button.MarginProperty,    new Thickness(0, 0, 5, 0)));
            btnStyle.Setters.Add(new Setter(Button.CursorProperty,    Cursors.Hand));
            btnStyle.Setters.Add(new Setter(Button.FontFamilyProperty, new FontFamily("Segoe UI")));
            btnStyle.Setters.Add(new Setter(Button.FontSizeProperty,   12.0));

            // Wire up DynamicResource-equivalent bindings on a crop button.
            void StyleAccentBtn(Button b)
            {
                b.SetResourceReference(Button.BackgroundProperty,  "AccentDim");
                b.SetResourceReference(Button.ForegroundProperty,  "Accent");
                b.SetResourceReference(Button.BorderBrushProperty, "Accent");
            }

            // "This Page" apply
            var thisPageBtn = new Button { Content = "This Page", Style = btnStyle,
                ToolTip = Loc("Str_TT_CropThisPage") };
            StyleAccentBtn(thisPageBtn);
            thisPageBtn.Click += (_, _) => ApplyCrop([currentPage]);
            btnRow.Children.Add(thisPageBtn);

            // Range input + "Range" apply button
            _cropRangeBox = new TextBox
            {
                Width             = 68,
                Height            = 22,
                FontFamily        = new FontFamily("Segoe UI"),
                FontSize          = 11,
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(3, 1, 3, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 3, 0),
                ToolTip           = "Page range, e.g. 1-3,5"
            };
            _cropRangeBox.SetResourceReference(TextBox.BackgroundProperty,  "BgPanel");
            _cropRangeBox.SetResourceReference(TextBox.ForegroundProperty,  "TextPrimary");
            _cropRangeBox.SetResourceReference(TextBox.BorderBrushProperty, "BorderDim");
            var rangeApplyBtn = new Button { Content = "Range", Style = btnStyle,
                ToolTip = Loc("Str_TT_CropRange") };
            StyleAccentBtn(rangeApplyBtn);
            rangeApplyBtn.Click += (_, _) =>
            {
                int pc = _doc?.PageCount ?? 0;
                var pages = ParsePageRange(_cropRangeBox?.Text ?? "", pc);
                if (pages is null) { SetStatus(Loc("Str_InvalidRange")); return; }
                ApplyCrop(pages);
            };
            btnRow.Children.Add(_cropRangeBox);
            btnRow.Children.Add(rangeApplyBtn);

            if (multiPage)
            {
                var allPagesBtn = new Button { Content = "All Pages", Style = btnStyle };
                StyleAccentBtn(allPagesBtn);
                allPagesBtn.Click += (_, _) => ApplyCrop([.. Enumerable.Range(0, _doc!.PageCount)]);
                btnRow.Children.Add(allPagesBtn);
            }

            // Visual divider before destructive buttons
            var divider = new Border
            {
                Width             = 1,
                Margin            = new Thickness(2, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            divider.SetResourceReference(Border.BackgroundProperty, "BorderDim");
            btnRow.Children.Add(divider);

            // Remove Crop — only shown if current page already has a CropBox
            bool hasCropBox = _doc.Pages[currentPage].Elements.ContainsKey("/CropBox");
            if (hasCropBox)
            {
                var removeBtn = new Button { Content = "Remove Crop", Style = btnStyle,
                    ToolTip = Loc("Str_TT_CropRemove") };
                removeBtn.SetResourceReference(Button.BackgroundProperty,  "AccentDim");
                removeBtn.SetResourceReference(Button.ForegroundProperty,  "DangerRed");
                removeBtn.SetResourceReference(Button.BorderBrushProperty, "DangerRed");
                removeBtn.Click += (_, _) => RemoveCropBox([currentPage]);
                btnRow.Children.Add(removeBtn);

                if (multiPage)
                {
                    var removeAllBtn = new Button { Content = "Remove All", Style = btnStyle,
                        ToolTip = Loc("Str_TT_CropRemoveAll") };
                    removeAllBtn.SetResourceReference(Button.BackgroundProperty,  "AccentDim");
                    removeAllBtn.SetResourceReference(Button.ForegroundProperty,  "DangerRed");
                    removeAllBtn.SetResourceReference(Button.BorderBrushProperty, "DangerRed");
                    removeAllBtn.Click += (_, _) => RemoveCropBox([.. Enumerable.Range(0, _doc!.PageCount)]);
                    btnRow.Children.Add(removeAllBtn);
                }
            }

            var cancelBtn = new Button { Content = "Cancel", Style = btnStyle, ToolTip = Loc("Str_TT_CropCancel"), Background = Brushes.Transparent };
            cancelBtn.SetResourceReference(Button.ForegroundProperty,  "TextSecondary");
            cancelBtn.SetResourceReference(Button.BorderBrushProperty, "TextSecondary");
            cancelBtn.Click += (_, _) => HideCropConfirmBar();
            btnRow.Children.Add(cancelBtn);

            outer.Children.Add(btnRow);
            bar.Child = outer;
            bar.HorizontalAlignment = HorizontalAlignment.Left;
            bar.VerticalAlignment   = VerticalAlignment.Top;
            bar.Cursor              = Cursors.SizeAll;
            // *** Place bar in the OUTER (unscaled) grid so it renders at native
            // screen size regardless of the current zoom level. ***
            Panel.SetZIndex(bar, 100);

            // ── Drag-to-move ─────────────────────────────────────────────────────
            bar.MouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is Button || e.OriginalSource is TextBox) return;
                _cropBarDragging   = true;
                _cropBarDragOffset = e.GetPosition(PagePreviewPanel.Parent as UIElement);
                _cropBarDragOffset = new Point(
                    _cropBarDragOffset.X - bar.Margin.Left,
                    _cropBarDragOffset.Y - bar.Margin.Top);
                bar.CaptureMouse();
                e.Handled = true;
            };
            bar.MouseMove += (s, e) =>
            {
                if (!_cropBarDragging) return;
                var pos = e.GetPosition(PagePreviewPanel.Parent as UIElement);
                bar.Margin = new Thickness(
                    Math.Max(0, pos.X - _cropBarDragOffset.X),
                    Math.Max(0, pos.Y - _cropBarDragOffset.Y), 0, 0);
                e.Handled = true;
            };
            bar.MouseLeftButtonUp += (s, e) =>
            {
                if (!_cropBarDragging) return;
                _cropBarDragging = false;
                bar.ReleaseMouseCapture();
                e.Handled = true;
            };
            // ─────────────────────────────────────────────────────────────────────

            _cropConfirmBar = bar;

            var outerGrid = PagePreviewPanel.Parent as Panel ?? (Panel)_annotationCanvas;
            outerGrid.Children.Add(bar);
            AddCropHandles();
            RepositionCropConfirmBar();
            SyncCropBoxInputs();
        }

        private void HideCropConfirmBar()
        {
            if (_cropConfirmBar is not null)
            {
                // Remove from whichever panel it was added to (outer grid or canvas fallback)
                (_annotationCanvas.Parent as Panel)?.Children.Remove(_cropConfirmBar);
                _annotationCanvas.Children.Remove(_cropConfirmBar);   // no-op if not there
                (PagePreviewPanel.Parent as Panel)?.Children.Remove(_cropConfirmBar);
                _cropConfirmBar = null;
            }
            if (_cropPreviewRectBorder is not null)
            {
                (_cropPreviewRectBorder.Parent as Panel)?.Children.Remove(_cropPreviewRectBorder);
                _annotationCanvas.Children.Remove(_cropPreviewRectBorder);
                _cropPreviewRectBorder = null;
            }
            if (_cropPreviewRect is not null)
            {
                (_cropPreviewRect.Parent as Panel)?.Children.Remove(_cropPreviewRect);
                _annotationCanvas.Children.Remove(_cropPreviewRect);
                _cropPreviewRect = null;
            }
            RemoveCropHandles();
            _cropX1Box = _cropY1Box = _cropX2Box = _cropY2Box = null;
            _cropRangeBox = null;
        }

        private void AddCropHandles()
        {
            RemoveCropHandles();
            const double hSize = 24;
            var tags    = new[] { "NW", "NE", "SE", "SW" };
            var cursors = new[] { Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW };
            // Handles live in the OUTER unscaled panel (same as the confirm bar) so they render
            // at a fixed screen size regardless of canvas zoom level.
            var outerGrid = PagePreviewPanel.Parent as Panel ?? (Panel)_annotationCanvas;

            for (int i = 0; i < 4; i++)
            {
                var tag = tags[i];
                var h = new Rectangle
                {
                    Width  = hSize, Height = hSize,
                    Fill   = Brushes.Transparent,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                    StrokeThickness = 1.5,
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                        { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.6 },
                    Tag    = tag,
                    Cursor = cursors[i],
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment   = VerticalAlignment.Top,
                };
                Panel.SetZIndex(h, 101);
                // Attach drag directly on the handle so clicks don't need to reach _annotationCanvas.
                h.MouseLeftButtonDown += (_, e) =>
                {
                    _activeCropHandleTag  = tag;
                    // Measure and capture against the active surface (per-page overlay in
                    // Continuous view) so the drag delta matches the crop rect's coordinate space.
                    _cropHandleDragStart  = e.GetPosition(_activeCanvas);
                    _cropRectAtHandleDrag = _cropCanvasRect;
                    _activeCanvas.CaptureMouse();
                    e.Handled = true;
                };
                _cropHandles.Add(h);
                outerGrid.Children.Add(h);
            }
            PositionCropHandles();
        }

        private void RemoveCropHandles()
        {
            var outerGrid = PagePreviewPanel.Parent as Panel ?? (Panel)_annotationCanvas;
            foreach (var h in _cropHandles)
            {
                outerGrid.Children.Remove(h);
                _annotationCanvas.Children.Remove(h); // belt-and-suspenders in case it ended up in canvas
            }
            _cropHandles.Clear();
            _activeCropHandleTag = null;
            RemoveCropBrackets();   // no-op — list is always empty now, kept for safety
        }

        private void RemoveCropBrackets()
        {
            foreach (var b in _cropBrackets) _annotationCanvas.Children.Remove(b);
            _cropBrackets.Clear();
        }

        private void PositionCropHandles()
        {
            if (_cropHandles.Count < 4) return;
            const double hSize = 24;
            var outerGrid = PagePreviewPanel.Parent as UIElement ?? _annotationCanvas;
            // Translate canvas-space corners to outer-panel screen space (same as RepositionCropConfirmBar).
            var canvasCorners = new Point[]
            {
                new(_cropCanvasRect.X,      _cropCanvasRect.Y),
                new(_cropCanvasRect.Right,   _cropCanvasRect.Y),
                new(_cropCanvasRect.Right,   _cropCanvasRect.Bottom),
                new(_cropCanvasRect.X,       _cropCanvasRect.Bottom),
            };
            var offsets = new (double dx, double dy)[]
            {
                (0,       0      ),   // NW: top-left at top-left corner
                (-hSize,  0      ),   // NE: top-right at top-right corner
                (-hSize, -hSize  ),   // SE: bottom-right at bottom-right corner
                (0,      -hSize  ),   // SW: bottom-left at bottom-left corner
            };
            for (int i = 0; i < 4; i++)
            {
                Point screen = _activeCanvas.TranslatePoint(canvasCorners[i], outerGrid);
                _cropHandles[i].Margin = new Thickness(
                    screen.X + offsets[i].dx,
                    screen.Y + offsets[i].dy,
                    0, 0);
            }
        }

        private void UpdateCropRectVisuals()
        {
            if (_cropPreviewRect is null) return;
            var r = _cropCanvasRect;
            Canvas.SetLeft(_cropPreviewRect, r.X); Canvas.SetTop(_cropPreviewRect, r.Y);
            _cropPreviewRect.Width = r.Width;       _cropPreviewRect.Height = r.Height;
            PositionCropHandles();
            RepositionCropConfirmBar();
            SyncCropBoxInputs();
        }

        private void RepositionCropConfirmBar()
        {
            if (_cropConfirmBar is null) return;

            // The confirm bar lives in the OUTER (unscaled) panel, so we must
            // translate canvas-space coordinates to that panel's coordinate space.
            var outerGrid = PagePreviewPanel.Parent as UIElement ?? _annotationCanvas;
            Point topLeft     = _activeCanvas.TranslatePoint(
                new Point(_cropCanvasRect.X, _cropCanvasRect.Y), outerGrid);
            Point bottomLeft  = _activeCanvas.TranslatePoint(
                new Point(_cropCanvasRect.X, _cropCanvasRect.Bottom), outerGrid);

            const double barHeight = 78;
            double barLeft     = Math.Max(4, topLeft.X);
            double barTopBelow = bottomLeft.Y + 8;
            double barTopAbove = topLeft.Y - barHeight - 8;

            double parentH = (outerGrid as FrameworkElement)?.ActualHeight
                             ?? _annotationCanvas.ActualHeight;
            double barTop = barTopBelow + barHeight < parentH
                ? barTopBelow : Math.Max(4, barTopAbove);

            _cropConfirmBar.Margin = new Thickness(barLeft, barTop, 0, 0);
        }

        private void ApplyCrop(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_CropNoDoc")); return; }
            int currentPage = _cropPageIndex >= 0 ? _cropPageIndex : PageList.SelectedIndex;
            if (currentPage < 0) { SetStatus(Loc("Str_CropNoPage")); return; }
            if (!_renderDims.TryGetValue(currentPage, out var refDims))
            { SetStatus(Loc("Str_CropNoDims")); return; }

            try
            {
                PushDocUndo();

                // Convert canvas rect to PDF CropBox coords using the rotation-aware helper.
                // This is the correct inversion of how Docnet renders the rotated bitmap.
                _pageRotations.TryGetValue(currentPage, out int rot);
                var refPage = _doc.Pages[currentPage];
                double refPdfW = refPage.Width.Point;
                double refPdfH = refPage.Height.Point;

                var (rx1, ry1, rx2, ry2) = CanvasToPdfRect(
                    _cropCanvasRect, refPdfW, refPdfH, refDims.w, refDims.h, rot);

                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    var page  = _doc.Pages[pi];
                    double pW = page.Width.Point;
                    double pH = page.Height.Point;

                    // Scale proportionally when "All Pages" spans pages of different sizes
                    double x1 = rx1 * pW / refPdfW;
                    double y1 = ry1 * pH / refPdfH;
                    double x2 = rx2 * pW / refPdfW;
                    double y2 = ry2 * pH / refPdfH;

                    // Clamp to media box and ensure minimum 1-pt size
                    x1 = Math.Max(0, x1);  y1 = Math.Max(0, y1);
                    x2 = Math.Min(pW, x2); y2 = Math.Min(pH, y2);
                    if (x2 - x1 < 1) x2 = x1 + 1;
                    if (y2 - y1 < 1) y2 = y1 + 1;

                    // Write CropBox directly into the page dictionary (more reliable across
                    // PdfSharpCore versions than the CropBox property setter).
                    var cropArr = new PdfSharpCore.Pdf.PdfArray();
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x1));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y1));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x2));
                    cropArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y2));
                    page.Elements["/CropBox"] = cropArr;

                    // Mirror to TrimBox (PDF spec: TrimBox ⊆ CropBox ⊆ MediaBox)
                    var trimArr = new PdfSharpCore.Pdf.PdfArray();
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x1));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y1));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(x2));
                    trimArr.Elements.Add(new PdfSharpCore.Pdf.PdfReal(y2));
                    page.Elements["/TrimBox"] = trimArr;
                }

                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload(keepAnnotations: true);
                SetStatus(string.Format(Loc("Str_Cropped"), pageIndices.Length));
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_CropFailed"), ex.Message));
            }
        }

        private void RemoveCropBox(int[] pageIndices)
        {
            if (_doc is null || _currentFile is null) return;
            try
            {
                PushDocUndo();
                foreach (int pi in pageIndices)
                {
                    if (pi < 0 || pi >= _doc.PageCount) continue;
                    _doc.Pages[pi].Elements.Remove("/CropBox");
                    _doc.Pages[pi].Elements.Remove("/TrimBox");
                }
                HideCropConfirmBar();
                SetTool(EditTool.Select);
                SaveTempAndReload(keepAnnotations: true);
                SetStatus(string.Format(Loc("Str_RemovedCrop"), pageIndices.Length));
            }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_RemoveCropFailed"), ex.Message));
            }
        }

        // ============================================================
        // Draw/Highlight settings bar
        // ============================================================

        // The quick-colors shown in the annotate bars. User-configurable via the color picker's swatch
        // row (shared "UserSwatches" setting); seeded with these 8 defaults, restorable via the picker's
        // Reset. SwatchColors reads the live set each time a bar is built, so edits show up immediately.
        private static readonly Color[] DefaultSwatchColors =
        [
            Color.FromRgb(0xE0, 0x3C, 0x3C), Color.FromRgb(0xE8, 0x7A, 0x1E), Color.FromRgb(0xF2, 0xC0, 0x1E),
            Color.FromRgb(0x2E, 0xA5, 0x4C), Color.FromRgb(0x2E, 0x86, 0xDE), Color.FromRgb(0x8E, 0x5B, 0xD6),
            Color.FromRgb(0xE0, 0x4A, 0x9A), Colors.Black, Colors.White
        ];
        private static Color[] SwatchColors => LoadUserSwatches();
        private static Color[] LoadUserSwatches()
        {
            var raw = App.GetSetting("UserSwatches");
            if (string.IsNullOrWhiteSpace(raw)) return [.. DefaultSwatchColors];
            List<Color> list = [];
            foreach (var part in raw!.Split(','))
            {
                var t = part.Trim().TrimStart('#');
                if (t.Length == 6 && int.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int v))
                    list.Add(Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
            }
            return list.Count > 0 ? [.. list] : [.. DefaultSwatchColors];
        }

        // Frozen cached brushes for hot-path UI construction
        private static readonly SolidColorBrush _swatchDimBorder   = Freeze(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
        private static readonly SolidColorBrush _drawBarBackground  = Freeze(new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)));
        private static readonly SolidColorBrush _thumbBorderBrush   = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));

        private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        // Wraps a floating annotation bar's content with the app's film-grain layer so these bars
        // carry the same texture as the Settings / signature / dialog surfaces. The grain extends
        // under the host border's 4px padding (negative margin) and matches its bottom corners.
        private Grid GrainWrap(UIElement content)
        {
            var g = new Grid();
            g.Children.Add(new Border
            {
                CornerRadius     = new CornerRadius(0, 0, 4, 4),
                Margin           = new Thickness(-4),
                IsHitTestVisible = false,
                Opacity          = (double)FindResource("GrainOpacity"),
                Background       = (System.Windows.Media.Brush)FindResource("GrainBrushShared")
            });
            g.Children.Add(content);
            return g;
        }

        // A grab handle (vertical dots) placed at the left of an annotation bar so it can be slid
        // left/right along the top of the document. Returns the handle for EnableBarSlide.
        private Border MakeBarGrip(int dotCount = 3)
        {
            // Real ellipse dots (not a braille glyph, which didn't render on some fonts/themes - the
            // grip looked empty on the Light bars). Matches the sidebar splitter / minimized-bar dots.
            // dotCount scales with bar height: 3 for single-row bars, 4 for the double-height text bar.
            var dots = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            var fill = (Brush)FindResource("TextSecondary");
            for (int i = 0; i < dotCount; i++)
                dots.Children.Add(new System.Windows.Shapes.Ellipse
                { Width = 3, Height = 3, Margin = new Thickness(0, 1.5, 0, 1.5), Fill = fill });
            return new Border
            {
                Background        = Brushes.Transparent,
                Cursor            = Cursors.Hand,
                Padding           = new Thickness(1, 0, 10, 0),   // hard to the left edge, more gap before the labels
                VerticalAlignment = VerticalAlignment.Stretch,
                Child             = dots
            };
        }

        // Wraps an annotate bar's content with a film-grain layer (always visible, even minimized) and a
        // hidden grip-dots strip (the same dots the sidebar splitter uses) revealed when minimized.
        private FrameworkElement BuildBarHost(FrameworkElement content)
        {
            var host = new Grid();

            // Grain stays put when the controls collapse, so the minimized strip keeps the texture.
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Margin = new Thickness(-4),
                IsHitTestVisible = false,
                Opacity = (double)FindResource("GrainOpacity"),
                Background = (Brush)FindResource("GrainBrushShared")
            });

            host.Children.Add(content);   // the collapsible controls

            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            var fill = TryFindResource("TextSecondary") as Brush ?? Brushes.Gray;   // match the sidebar handle dots
            for (int i = 0; i < 6; i++)
                dots.Children.Add(new System.Windows.Shapes.Ellipse
                { Width = 3, Height = 3, Margin = new Thickness(2, 0, 2, 0), Fill = fill });
            // The dots live in a transparent, hit-testable strip that fills the bar. While the bar is
            // minimized this strip is shown, so the whole peek strip can be dragged left/right (the slide
            // is wired to it in PlaceAnnotationBar) - same as dragging the grip on the expanded bar.
            var dotsStrip = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                Visibility = Visibility.Collapsed,
                Child = dots
            };
            host.Children.Add(dotsStrip);

            _annotBarContent = content;
            _annotBarDots = dotsStrip;
            return host;
        }

        // Drop shadow for the annotate bars: offset straight down with depth >= blur, so it falls on the
        // sides and bottom but never above the bar (no halo between it and the toolbar). Removed entirely
        // while minimized.
        private static System.Windows.Media.Effects.DropShadowEffect AnnotBarShadow()
            => new() { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 3, Direction = 270, Opacity = 0.38 };

        // Lets the annotation bars slide horizontally along the top via their grip, clamped inside
        // the document area, with the X position remembered (shared across the draw/text bars).
        private void EnableBarSlide(FrameworkElement grip, Border bar, FrameworkElement bounds)
        {
            grip.MouseLeftButtonDown += (s, e) =>
            {
                double w = bar.ActualWidth;
                // Drag uniformly in left-edge coordinates whatever the current anchor; the edge it
                // anchors to is decided on release from where it ends up.
                double curLeft = bar.HorizontalAlignment == HorizontalAlignment.Right
                    ? bounds.ActualWidth - bar.Margin.Right - w
                    : bar.Margin.Left;
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                bar.Margin = new Thickness(curLeft, bar.Margin.Top, 0, 0);
                bar.Tag = (e.GetPosition(bounds).X, curLeft);   // (startX, origLeft)
                grip.CaptureMouse();
                e.Handled = true;
            };
            grip.MouseMove += (s, e) =>
            {
                if (bar.Tag is not (double startX, double origLeft) || !grip.IsMouseCaptured) return;
                double w = bar.ActualWidth;
                // Stop the drag at the scrollbar's left edge (not the pane edge) so the bar never
                // overshoots the scrollbar and then snaps back on release - the "bounce" the user saw.
                double sb = VerticalScrollBarInset();
                double maxLeft = Math.Max(0, bounds.ActualWidth - w - sb);
                double nl = Math.Max(0, Math.Min(maxLeft, origLeft + (e.GetPosition(bounds).X - startX)));
                bar.Margin = new Thickness(nl, bar.Margin.Top, 0, 0);
                // Merge the docked-side border with the pane border live while dragging (no footprint
                // change), so it doesn't pop in on release. The right side only docks flush when no
                // scrollbar sits between the bar and the pane edge.
                SetBarDockedBorder(bar, dockedLeft: nl <= 0.5, dockedRight: sb <= 0 && nl >= maxLeft - 0.5);
            };
            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (!grip.IsMouseCaptured) return;
                grip.ReleaseMouseCapture();
                double w = bar.ActualWidth;
                double left = bar.Margin.Left;
                // Measure the right gap from the scrollbar's left edge (the usable content edge), so a
                // bar parked against the scrollbar records gap ~0 and PositionAnnotationBar re-adds the
                // scrollbar width once - no double inset, no jump.
                double sb = VerticalScrollBarInset();
                double rightGap = Math.Max(0, (bounds.ActualWidth - sb) - (left + w));
                const double snap = 24;   // within this many px of an edge, cling to that edge exactly
                if (left <= snap)
                {
                    _annotBarCenterFrac = null; _annotBarAnchorRight = false; _annotBarGap = Math.Max(0, left);
                }
                else if (rightGap <= snap)
                {
                    _annotBarCenterFrac = null; _annotBarAnchorRight = true; _annotBarGap = rightGap;
                }
                else
                {
                    // Away from both edges: remember it as a fraction of the width so resizing scales it
                    // smoothly rather than snapping it to an edge.
                    _annotBarCenterFrac = (left + w / 2) / bounds.ActualWidth;
                }
                App.SetSetting("AnnotBarFrac",
                    _annotBarCenterFrac?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
                App.SetSetting("AnnotBarGap", ((int)(_annotBarGap ?? 8)).ToString());
                App.SetSetting("AnnotBarRightSide", _annotBarAnchorRight ? "1" : "0");
                if (PagePreviewPanel?.Parent is Grid area) PositionAnnotationBar(bar, area);
                e.Handled = true;
            };
        }

        // Saved horizontal placement for the floating draw/text settings bars (shared by both). The bar
        // anchors to whichever edge it sits nearer and remembers its gap from that edge, so it clings to
        // that edge on resize. RepositionAnnotationBars re-applies it - clamped fully inside the document
        // area - from the same window events that keep the Settings panel in-window, so it can never end
        // up off-screen regardless of which edge it was parked against.
        private double? _annotBarGap;
        private bool _annotBarAnchorRight = true;
        private double? _annotBarCenterFrac;   // set when parked away from both edges: hold this fraction of the width
        private bool _vScrollVisible;          // last-known document vertical scrollbar state, to reposition bars on change
        private EditTool? _annotBarTool;       // which tool the visible annotate bar is for (so we fade only on real switches)
        private bool _annotBarMinimized;       // annotate bar collapsed to a peek strip (toggled by re-clicking its tool)
        private double _annotBarFullHeight;    // remembered full height to expand back to
        private FrameworkElement? _annotBarContent;   // the bar's normal content (hidden while minimized)
        private FrameworkElement? _annotBarDots;      // grip-dots strip shown while minimized

        // Positions an annotation bar and wires up sliding. If we already know the X (this session or
        // saved), set it synchronously so the bar appears in place; only the very first time do we
        // defer to compute the default top-right from the laid-out width.
        private void PlaceAnnotationBar(Border bar, Border grip, bool fadeIn = false)
        {
            if (PagePreviewPanel.Parent is not Grid area) return;

            if (_annotBarGap is null && _annotBarCenterFrac is null)
            {
                if (double.TryParse(App.GetSetting("AnnotBarFrac"), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double f))
                {
                    _annotBarCenterFrac = f;   // parked away from both edges last time
                }
                else
                {
                    _annotBarGap = int.TryParse(App.GetSetting("AnnotBarGap"), out int sg) ? sg : 8;
                    _annotBarAnchorRight = App.GetSetting("AnnotBarRightSide") != "0";   // default: right edge
                }
            }
            EnableBarSlide(grip, bar, area);
            // The minimized peek strip drags the bar too, so a collapsed bar can be repositioned.
            if (_annotBarDots is not null) EnableBarSlide(_annotBarDots, bar, area);
            // A freshly built bar has no measured width yet, so PositionAnnotationBar can't place it until
            // layout runs. Hide it for that one frame (Opacity 0 still lays out, so width measures), then
            // anchor and reveal it - otherwise it renders at its default right edge first and visibly
            // jumps to its saved spot on every tool switch.
            bar.Opacity = 0;
            PositionAnnotationBar(bar, area);   // sync position (edge-anchored modes are correct without a width)
            if (fadeIn)
            {
                // Start the fade-in immediately so it overlaps the outgoing bar's fade-out (a true
                // crossfade). Waiting for the deferred layout pass left a frame where neither bar was
                // visible, which read as a blink. Final clamp/centre still happens once laid out.
                bar.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110)))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => PositionAnnotationBar(bar, area)));
            }
            else if (_annotBarCenterFrac is not null)
            {
                // Centre-parked needs a measured width to place, so stay hidden one layout frame so it
                // can't render at the default edge first and then jump.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => { PositionAnnotationBar(bar, area); bar.Opacity = 1; }));
            }
            else
            {
                // Edge-anchored: already placed correctly above (no width needed), so reveal it right away
                // instead of hiding for a frame - that one hidden frame was the "blink" on a same-tool
                // refresh / same-family tool switch.
                bar.Opacity = 1;
            }
        }

        private void ShowDrawSettings(EditTool tool)
        {
            // Fade the bar in only when it's genuinely appearing (no bar yet, or coming from the text
            // bar). Switching between tools that share this same draw bar (Highlight / Underline /
            // Strikethrough / Draw) swaps it in place instantly - otherwise the two near-identical bars
            // crossfade through ~50% opacity and read as a blink even though nothing visually changed.
            bool prevWasDrawBar = _annotBarTool is EditTool.Draw or EditTool.Highlight
                                                or EditTool.Underline or EditTool.Strikethrough;
            bool appearing = _annotBarTool != tool && !prevWasDrawBar;
            if (_drawSettingsBar is not null)
            {
                // On a switch, fade the old bar out (crossfades with the new one); on a refresh, swap it
                // out instantly so clicking a swatch doesn't flicker the whole bar.
                if (appearing) FadeOutAndRemoveBar(_drawSettingsBar);
                else (PagePreviewPanel.Parent as Grid)?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2) };

            // Drag grip so the bar can be slid left/right along the top.
            var drawGrip = MakeBarGrip();
            panel.Children.Add(drawGrip);

            // Color label
            var colorLbl = new TextBlock
            {
                Text = "Color:",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            colorLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(colorLbl);

            // Color swatches
            bool isLineTool = tool == EditTool.Strikethrough || tool == EditTool.Underline;
            var activeColor = tool == EditTool.Draw ? _drawColor
                            : isLineTool ? Color.FromRgb(_lineAnnotColor.R, _lineAnnotColor.G, _lineAnnotColor.B)
                            : Color.FromRgb(_highlightColor.R, _highlightColor.G, _highlightColor.B);
            foreach (var color in SwatchColors)
            {
                bool isActive = color == activeColor;
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = Freeze(new SolidColorBrush(color)),
                    BorderThickness = new Thickness(isActive ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = color
                };
                if (isActive)
                    swatch.SetResourceReference(Border.BorderBrushProperty, "Accent");
                else
                    swatch.BorderBrush = _swatchDimBorder;
                swatch.MouseLeftButtonDown += (s, e) =>
                {
                    var c = (Color)((Border)s!).Tag;
                    if (tool == EditTool.Draw)
                        _drawColor = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                    else if (isLineTool)
                        _lineAnnotColor = Color.FromArgb(_lineAnnotColor.A, c.R, c.G, c.B);
                    else
                        _highlightColor = Color.FromArgb(_highlightColor.A, c.R, c.G, c.B);
                    ApplyDrawStyleToSelection();   // edit the selected annotation, if any
                    ShowDrawSettings(tool); // refresh selection
                };
                panel.Children.Add(swatch);
            }

            // "More colors..." -> full RGB picker, applied to whichever draw color this bar drives.
            var moreDraw = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Margin = new Thickness(1),
                Cursor = Cursors.Hand, BorderThickness = new Thickness(1), ToolTip = "More colors...",
                BorderBrush = _swatchDimBorder,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.25),
                        new GradientStop(Colors.Lime, 0.5), new GradientStop(Colors.Cyan, 0.7),
                        new GradientStop(Colors.Blue, 1)
                    }
                }
            };
            moreDraw.MouseLeftButtonDown += (_, _) => OpenColorPicker(activeColor, c =>
            {
                if (tool == EditTool.Draw)      _drawColor      = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                else if (isLineTool)            _lineAnnotColor = Color.FromArgb(_lineAnnotColor.A, c.R, c.G, c.B);
                else                            _highlightColor = Color.FromArgb(_highlightColor.A, c.R, c.G, c.B);
                ApplyDrawStyleToSelection();
                ShowDrawSettings(tool);
            }, () => ShowDrawSettings(tool));
            panel.Children.Add(moreDraw);

            // Separator
            var sep1 = new Rectangle { Width = 1, Margin = new Thickness(8, 2, 8, 2) };
            sep1.SetResourceReference(Rectangle.FillProperty, "BorderDim");
            panel.Children.Add(sep1);

            // Size slider (draw only)
            if (tool == EditTool.Draw)
            {
                var sizeLbl = new TextBlock
                {
                    Text = "Size:",
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
                };
                sizeLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                panel.Children.Add(sizeLbl);

                var sizeSlider = new Slider
                {
                    Minimum = 1, Maximum = 20, Value = _drawWidth,
                    Width = 90, VerticalAlignment = VerticalAlignment.Center,
                    TickFrequency = 1, IsSnapToTickEnabled = true,
                    Style = (Style)FindResource("DarkSlider")
                };
                sizeSlider.ValueChanged += (s, e) => { _drawWidth = e.NewValue; ApplyDrawStyleToSelection(); };
                panel.Children.Add(sizeSlider);

                var sizeLabel = new TextBlock
                {
                    Text = $"{_drawWidth:F0}px",
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
                    Width = 34, TextAlignment = TextAlignment.Right
                };
                sizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                sizeSlider.ValueChanged += (s, e) => sizeLabel.Text = $"{e.NewValue:F0}px";
                panel.Children.Add(sizeLabel);

                var sep2 = new Rectangle { Width = 1, Margin = new Thickness(8, 2, 8, 2) };
                sep2.SetResourceReference(Rectangle.FillProperty, "BorderDim");
                panel.Children.Add(sep2);
            }

            // Opacity slider
            var opacityLbl = new TextBlock
            {
                Text = "Opacity:",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            opacityLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(opacityLbl);

            byte currentOpacity = tool == EditTool.Draw ? _drawOpacity : isLineTool ? _lineAnnotColor.A : _highlightColor.A;
            var opacitySlider = new Slider
            {
                Minimum = 10, Maximum = 255, Value = currentOpacity,
                Width = 90, VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("DarkSlider")
            };
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(currentOpacity / 255.0 * 100)}%",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0),
                Width = 40, TextAlignment = TextAlignment.Right
            };
            opacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                if (tool == EditTool.Draw)
                {
                    _drawOpacity = a;
                    _drawColor = Color.FromArgb(a, _drawColor.R, _drawColor.G, _drawColor.B);
                }
                else if (isLineTool)
                {
                    _lineAnnotColor = Color.FromArgb(a, _lineAnnotColor.R, _lineAnnotColor.G, _lineAnnotColor.B);
                }
                else
                {
                    _highlightColor = Color.FromArgb(a, _highlightColor.R, _highlightColor.G, _highlightColor.B);
                }
                ApplyDrawStyleToSelection();   // edit the selected annotation, if any
            };
            panel.Children.Add(opacitySlider);
            panel.Children.Add(opacityLabel);

            _drawSettingsBar = new Border
            {
                BorderThickness = new Thickness(1, 0, 1, 1),   // no top border - the toolbar above already separates
                HorizontalAlignment = HorizontalAlignment.Right,  // right-anchored; slid via the grip
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Effect = AnnotBarShadow(),
                Child = BuildBarHost(panel),
                Margin = new Thickness(0, 0, 0, 0)
            };
            _drawSettingsBar.SetResourceReference(Border.BackgroundProperty,  "BgFlyout");
            _drawSettingsBar.SetResourceReference(Border.BorderBrushProperty, "PaneBorder");

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_drawSettingsBar, 100);
                previewArea.Children.Add(_drawSettingsBar);
                PlaceAnnotationBar(_drawSettingsBar, drawGrip, fadeIn: appearing);
            }
            _annotBarTool = tool;
            _annotBarMinimized = false;   // a freshly built bar is full-size
        }

        private void HideDrawSettings()
        {
            FadeOutAndRemoveBar(_drawSettingsBar);
            _drawSettingsBar = null;
            if (_annotBarTool is EditTool.Draw or EditTool.Highlight
                or EditTool.Strikethrough or EditTool.Underline)
                _annotBarTool = null;
        }

        // ============================================================
        // Text tool settings bar
        // ============================================================

        private void ApplyTextStyleToActiveBox()
        {
            if (_activeTextBox is null) return;
            _activeTextBox.Foreground = new SolidColorBrush(_textColor);
            _activeTextBox.Background = TextEditBackground();   // reflect the chosen fill live
            int pg = _activeTextBox.Tag is int tp ? tp : PageList.SelectedIndex;
            double fontCanvas = _textFontSize;
            if (_doc is not null && pg >= 0 && _renderDims.TryGetValue(pg, out var rd) && rd.h > 0)
            {
                double sy = _doc.Pages[pg].Height.Point / rd.h;
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            _activeTextBox.FontSize = fontCanvas;
        }

        // Applies the current text style to whatever is active: the live edit box if one is open,
        // otherwise the selected text box (so its color / fill / size can be changed after placing it).
        // Opens the full RGB color picker seeded with the current color; applies the result on OK.
        private void OpenColorPicker(Color current, Action<Color> apply, Action? refreshBar = null)
        {
            var dlg = new ColorPickerDialog(this, Color.FromRgb(current.R, current.G, current.B));
            // Live-update the annotate bar behind the (modal) dialog whenever the shared palette is edited.
            if (refreshBar is not null) dlg.SwatchesChanged += refreshBar;
            if (dlg.ShowDialog() == true) apply(dlg.SelectedColor);
        }

        // Diagonal rainbow fill for the "more colors" swatches that open the picker.
        private static LinearGradientBrush RainbowBrush() => new()
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.25),
                new GradientStop(Colors.Lime, 0.5), new GradientStop(Colors.Cyan, 0.7),
                new GradientStop(Colors.Blue, 1)
            }
        };

        private void ApplyTextStyleToSelection()
        {
            if (_activeTextBox is not null)
            {
                ApplyTextStyleToActiveBox();
                return;
            }
            if (_selectedAnnotation is TextAnnotation ta)
            {
                ta.SetColor(_textColor);
                ta.SetFill(_textFillColor);
                double sy = 1.0;
                if (_doc is not null && _renderDims.TryGetValue(ta.PageIndex, out var rd) && rd.h > 0)
                    sy = _doc.Pages[ta.PageIndex].Height.Point / rd.h;
                if (sy > 0 && _textFontSize > 0) ta.FontSize = _textFontSize / sy;
                MarkDirty();
                RenderAllAnnotations(ta.PageIndex);
                if (_selectionBorder is not null) _activeCanvas.Children.Add(_selectionBorder);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
            }
        }

        // Draw-bar counterpart to ApplyTextStyleToSelection: when a highlight / line / ink annotation
        // is selected (not just being freshly drawn), push the bar's current color, opacity and width
        // onto it and repaint - so editing an existing annotation works the same as setting up a new one.
        private void ApplyDrawStyleToSelection()
        {
            if (_selectedAnnotation is HighlightAnnotation ha)
            {
                ha.SetColor(ha.Style == HighlightStyle.Fill ? _highlightColor : _lineAnnotColor);
                MarkDirty();
                RenderAllAnnotations(ha.PageIndex);
                ReattachSelectionVisuals();
            }
            else if (_selectedAnnotation is InkAnnotation ia)
            {
                ia.SetColor(_drawColor);
                ia.StrokeWidth = _drawWidth;
                MarkDirty();
                RenderAllAnnotations(ia.PageIndex);
                ReattachSelectionVisuals();
            }
        }

        // RenderAllAnnotations rebuilds the page canvas from scratch, so the selection outline and any
        // resize handles (which live on that same canvas) must be re-added after a repaint.
        private void ReattachSelectionVisuals()
        {
            if (_selectionBorder is not null) _activeCanvas.Children.Add(_selectionBorder);
            if (_pairedCoverOutline is not null) _activeCanvas.Children.Add(_pairedCoverOutline);   // stays put during resize
            foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
        }

        private void ShowTextSettings()
        {
            bool appearing = _annotBarTool != EditTool.Text;   // real appear/switch vs same-tool refresh
            if (_textSettingsBar is not null)
            {
                if (appearing) FadeOutAndRemoveBar(_textSettingsBar);
                else (PagePreviewPanel.Parent as Grid)?.Children.Remove(_textSettingsBar);
                _textSettingsBar = null;
            }

            // Two aligned rows in a Grid: text row (Color | Size | Opacity) over fill row
            // (Fill | gap | Fill Opacity). Shared Auto columns line the swatches up under each other and
            // the Fill Opacity slider directly under the Opacity slider; the empty middle of the fill
            // row (under Size) is left as a grabbable strip.
            var grid = new Grid { Margin = new Thickness(8, 2, 8, 2) };
            for (int ci = 0; ci < 9; ci++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            void Place(UIElement el, int r, int col, int span = 1)
            {
                Grid.SetRow(el, r);
                Grid.SetColumn(el, col);
                if (span > 1) Grid.SetColumnSpan(el, span);
                grid.Children.Add(el);
            }
            TextBlock DimLabel(string text, int top, bool rightAlign = false)
            {
                var t = new TextBlock
                {
                    Text = text,
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = rightAlign ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                    Margin = new Thickness(0, top, 6, 0)
                };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                return t;
            }
            Border ColorSwatch(Color c, bool isActive, MouseButtonEventHandler onClick)
            {
                var sw = new Border
                {
                    Width = 18, Height = 18,
                    Background = new SolidColorBrush(c),
                    BorderThickness = new Thickness(isActive ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                if (isActive) sw.SetResourceReference(Border.BorderBrushProperty, "Accent");
                else sw.BorderBrush = _swatchDimBorder;
                sw.MouseLeftButtonDown += onClick;
                return sw;
            }
            Rectangle Sep()
            {
                var s = new Rectangle { Width = 1, Margin = new Thickness(8, 2, 8, 2) };
                s.SetResourceReference(Rectangle.FillProperty, "BorderDim");
                return s;
            }
            // Opens the full RGB picker. When the current color isn't one of the presets it shows that
            // color with an accent ring (and a small rainbow corner), so the bar reflects a custom pick.
            Grid MoreColorsSwatch(Color current, bool customActive, MouseButtonEventHandler onClick)
            {
                var grid = new Grid { Width = 18, Height = 18, Margin = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "More colors..." };
                var bg = new Border
                {
                    CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(customActive ? 2 : 1),
                    Background = customActive
                        ? (Brush)new SolidColorBrush(Color.FromRgb(current.R, current.G, current.B))
                        : RainbowBrush()
                };
                if (customActive) bg.SetResourceReference(Border.BorderBrushProperty, "Accent"); else bg.BorderBrush = _swatchDimBorder;
                grid.Children.Add(bg);
                if (customActive)
                    grid.Children.Add(new System.Windows.Shapes.Polygon
                    {
                        Points = [new Point(18, 7), new Point(18, 18), new Point(7, 18)],
                        Fill = RainbowBrush(), IsHitTestVisible = false
                    });
                grid.MouseLeftButtonDown += onClick;
                return grid;
            }

            // Drag grip (col 0), spanning both rows and centred vertically (not pinned to the top row).
            // 4 dots since this bar is double height.
            var textGrip = MakeBarGrip(4);
            textGrip.VerticalAlignment = VerticalAlignment.Center;
            Place(textGrip, 0, 0);
            Grid.SetRowSpan(textGrip, 2);

            // Labels, col 1.
            Place(DimLabel("Color:", 0), 0, 1);
            Place(DimLabel("Fill:", 4), 1, 1);

            // Swatch rows, col 2. Row 0 gets a leading spacer the size of the "None" tile so the
            // color swatches sit directly above the fill swatches.
            var swatchRow1 = new StackPanel { Orientation = Orientation.Horizontal };
            swatchRow1.Children.Add(new Border { Width = 18, Height = 18, Margin = new Thickness(1), Background = Brushes.Transparent });
            foreach (var color in SwatchColors)
            {
                var c = color;
                bool isActive = c.R == _textColor.R && c.G == _textColor.G && c.B == _textColor.B;
                swatchRow1.Children.Add(ColorSwatch(c, isActive, (_, _) =>
                {
                    _textColor = Color.FromArgb(_textOpacity, c.R, c.G, c.B);
                    ApplyTextStyleToSelection();
                    ShowTextSettings();
                }));
            }
            bool textCustom = !SwatchColors.Any(sc => sc.R == _textColor.R && sc.G == _textColor.G && sc.B == _textColor.B);
            swatchRow1.Children.Add(MoreColorsSwatch(_textColor, textCustom, (_, _) => OpenColorPicker(_textColor, c =>
            {
                _textColor = Color.FromArgb(_textOpacity, c.R, c.G, c.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            }, () => ShowTextSettings())));
            Place(swatchRow1, 0, 2);

            var swatchRow2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            bool noneActive = _textFillColor.A == 0;
            var noneGrid = new Grid { Width = 18, Height = 18, Margin = new Thickness(1), Cursor = Cursors.Hand };
            var noneBg = new Border { CornerRadius = new CornerRadius(3), Background = Brushes.White, BorderThickness = new Thickness(noneActive ? 2 : 1) };
            if (noneActive) noneBg.SetResourceReference(Border.BorderBrushProperty, "Accent"); else noneBg.BorderBrush = _swatchDimBorder;
            noneGrid.Children.Add(noneBg);
            noneGrid.Children.Add(new System.Windows.Shapes.Line { X1 = 3, Y1 = 15, X2 = 15, Y2 = 3, Stroke = Brushes.Red, StrokeThickness = 1.5 });
            noneGrid.MouseLeftButtonDown += (_, _) =>
            {
                _textFillColor = Color.FromArgb(0, _textFillColor.R, _textFillColor.G, _textFillColor.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            };
            swatchRow2.Children.Add(noneGrid);
            foreach (var color in SwatchColors)
            {
                var c = color;
                bool isActive = _textFillColor.A > 0 && c.R == _textFillColor.R && c.G == _textFillColor.G && c.B == _textFillColor.B;
                swatchRow2.Children.Add(ColorSwatch(c, isActive, (_, _) =>
                {
                    byte a = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;   // enable at full/current opacity
                    _textFillColor = Color.FromArgb(a, c.R, c.G, c.B);
                    ApplyTextStyleToSelection();
                    ShowTextSettings();
                }));
            }
            bool fillCustom = _textFillColor.A > 0 && !SwatchColors.Any(sc => sc.R == _textFillColor.R && sc.G == _textFillColor.G && sc.B == _textFillColor.B);
            swatchRow2.Children.Add(MoreColorsSwatch(_textFillColor.A == 0 ? Colors.White : _textFillColor, fillCustom, (_, _) => OpenColorPicker(_textFillColor.A == 0 ? Colors.White : _textFillColor, c =>
            {
                byte a = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;
                _textFillColor = Color.FromArgb(a, c.R, c.G, c.B);
                ApplyTextStyleToSelection();
                ShowTextSettings();
            }, () => ShowTextSettings())));
            // Faint divider + a little breathing room between the Color row and the Fill row.
            var fillWrap = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                Margin = new Thickness(2, 5, 8, 0), Padding = new Thickness(0, 5, 0, 0),
                Child = swatchRow2
            };
            Place(fillWrap, 1, 2);

            // Size group (row 0 only): sep | label + slider + value | sep. Cols 3-5.
            Place(Sep(), 0, 3);
            var sizeStack = new StackPanel { Orientation = Orientation.Horizontal };
            sizeStack.Children.Add(DimLabel("Size:", 0));
            var sizeSlider = new Slider
            {
                Minimum = 8, Maximum = 72, Value = Math.Max(8, Math.Min(72, _textFontSize)),
                Width = 90, VerticalAlignment = VerticalAlignment.Center,
                TickFrequency = 1, IsSnapToTickEnabled = true,
                Style = (Style)FindResource("DarkSlider")
            };
            // Editable size box (type an exact value; the slider stays for quick coarse adjustment).
            var sizeBox = new TextBox
            {
                Text = $"{_textFontSize:F0}",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                Width = 32, MaxLength = 4,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(1),
                Template = FlatTextBoxTemplate()
            };
            sizeBox.SetResourceReference(TextBox.BackgroundProperty, "BgPanel");
            sizeBox.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
            sizeBox.SetResourceReference(TextBox.BorderBrushProperty, "BorderDim");
            sizeBox.SetResourceReference(TextBox.CaretBrushProperty,  "Accent");
            sizeBox.SetResourceReference(TextBox.SelectionBrushProperty, "AccentDim");   // no WPF-default blue
            var ptLabel = new TextBlock
            {
                Text = "pt", FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0)
            };
            ptLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            // Slider drives the box; the box drives the slider. _suppressSizeSync breaks the feedback loop.
            sizeSlider.ValueChanged += (s, e) =>
            {
                if (_suppressSizeSync) return;
                _textFontSize = e.NewValue;
                sizeBox.Text = $"{e.NewValue:F0}";
                ApplyTextStyleToSelection();
            };
            void CommitSizeBox()
            {
                if (double.TryParse(sizeBox.Text, out double v))
                {
                    _textFontSize = Math.Max(1, Math.Min(400, Math.Round(v)));
                    _suppressSizeSync = true;
                    sizeSlider.Value = Math.Max(8, Math.Min(72, _textFontSize));   // thumb clamps; box keeps exact
                    _suppressSizeSync = false;
                    ApplyTextStyleToSelection();
                }
                sizeBox.Text = $"{_textFontSize:F0}";   // normalise / revert invalid input
            }
            sizeBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)  { CommitSizeBox(); e.Handled = true; }
                else if (e.Key == Key.Escape) { sizeBox.Text = $"{_textFontSize:F0}"; e.Handled = true; }
            };
            sizeBox.LostFocus += (s, e) => CommitSizeBox();
            sizeBox.GotFocus  += (s, e) => sizeBox.SelectAll();
            sizeStack.Children.Add(sizeSlider);
            sizeStack.Children.Add(sizeBox);
            sizeStack.Children.Add(ptLabel);
            Place(sizeStack, 0, 4);
            Place(Sep(), 0, 5);

            // Grabbable strip filling the empty middle of the fill row (under the Size group).
            var gapDrag = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 4, 0, 0) };
            Place(gapDrag, 1, 3, 3);

            // Opacity (row 0) and Fill Opacity (row 1), aligned: label col 6, slider col 7, value col 8.
            Place(DimLabel("Opacity:", 0, rightAlign: true), 0, 6);
            var opacitySlider = new Slider
            {
                Minimum = 10, Maximum = 255, Value = _textOpacity,
                Width = 90, VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("DarkSlider")
            };
            Place(opacitySlider, 0, 7);
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(_textOpacity / 255.0 * 100)}%",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
                Width = 40, TextAlignment = TextAlignment.Right
            };
            opacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Place(opacityLabel, 0, 8);
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                _textOpacity = a;
                _textColor = Color.FromArgb(a, _textColor.R, _textColor.G, _textColor.B);
                ApplyTextStyleToSelection();
            };

            Place(DimLabel("Fill Opacity:", 10, rightAlign: true), 1, 6);
            byte curFillA = _textFillColor.A == 0 ? (byte)255 : _textFillColor.A;
            var fillOpSlider = new Slider
            {
                Minimum = 10, Maximum = 255, Value = curFillA,
                Width = 90, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
                Style = (Style)FindResource("DarkSlider")
            };
            Place(fillOpSlider, 1, 7);
            var fillOpLabel = new TextBlock
            {
                Text = $"{(int)(curFillA / 255.0 * 100)}%",
                FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 10, 0, 0),
                Width = 40, TextAlignment = TextAlignment.Right
            };
            fillOpLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            Place(fillOpLabel, 1, 8);
            fillOpSlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                fillOpLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                // Dragging opacity turns the fill on (defaults to the current color, white for whiteout).
                _textFillColor = Color.FromArgb(a, _textFillColor.R, _textFillColor.G, _textFillColor.B);
                ApplyTextStyleToSelection();
            };

            // Faint divider between the Opacity and Fill Opacity rows, matching the color/fill one.
            var opDivider = new Border
            {
                Height = 1, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                Margin = new Thickness(0, 5, 8, 0)
            };
            Place(opDivider, 1, 7, 2);

            _textSettingsBar = new Border
            {
                BorderThickness = new Thickness(1, 0, 1, 1),   // no top border - the toolbar above already separates
                HorizontalAlignment = HorizontalAlignment.Right,  // right-anchored; slid via the grip
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Effect = AnnotBarShadow(),
                Child = BuildBarHost(grid),
                Margin = new Thickness(0, 0, 0, 0)
            };
            _textSettingsBar.SetResourceReference(Border.BackgroundProperty,  "BgFlyout");
            _textSettingsBar.SetResourceReference(Border.BorderBrushProperty, "PaneBorder");

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_textSettingsBar, 100);
                previewArea.Children.Add(_textSettingsBar);
                PlaceAnnotationBar(_textSettingsBar, textGrip, fadeIn: appearing);
                EnableBarSlide(gapDrag, _textSettingsBar, previewArea);   // the empty middle strip also drags the bar
            }
            _annotBarTool = EditTool.Text;
            _annotBarMinimized = false;   // a freshly built bar is full-size
        }

        private void HideTextSettings()
        {
            FadeOutAndRemoveBar(_textSettingsBar);
            _textSettingsBar = null;
            if (_annotBarTool == EditTool.Text) _annotBarTool = null;
        }

        // ── Form-field font-size stepper ─────────────────────────────────
        // A small "Field size: - N +" bar shown top-right while a form text field is focused, so the
        // user can resize that field's text (PDF forms otherwise lock the size to the field's /DA).
        // The chosen size is stored per field and baked into the field's /DA on save.
        private void ShowFormSizeBar(TextBox tb, int objNum, double scale)
        {
            HideFormSizeBar();
            _activeFormTb    = tb;
            _activeFormObj   = objNum;
            _activeFormScale = scale > 0 ? scale : 1;

            double curPt = Math.Round(_activeFormTb.FontSize / _activeFormScale);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
            var lbl = new TextBlock
            {
                Text = "Font size:",
                FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(lbl);

            var sizeLbl = new TextBlock
            {
                Text = curPt.ToString("0"),
                FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                MinWidth = 22, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            sizeLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

            panel.Children.Add(MakeFormSizeStep("", () => AdjustFormFontSize(-1, sizeLbl)));  // minus
            panel.Children.Add(sizeLbl);
            panel.Children.Add(MakeFormSizeStep("", () => AdjustFormFontSize(+1, sizeLbl)));  // plus

            _formSizeBar = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.55 },
                Margin  = new Thickness(0, 0, 8, 0),
                Child   = panel,
            };
            _formSizeBar.SetResourceReference(Border.BackgroundProperty,  "BgPanel");
            _formSizeBar.SetResourceReference(Border.BorderBrushProperty, "BorderDim");
            if (PagePreviewPanel.Parent is Grid g)
            {
                Panel.SetZIndex(_formSizeBar, 100);
                g.Children.Add(_formSizeBar);
            }
        }

        // A flat, non-focusable +/- step. It's a Border (not a Button) so clicking it doesn't move
        // keyboard focus out of the text field, which would otherwise blur the field and dismiss this bar.
        private Border MakeFormSizeStep(string glyph, Action onClick)
        {
            var t = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            var b = new Border
            {
                Width = 24, Height = 22, CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand,
                Margin = new Thickness(2, 0, 2, 0), Background = Brushes.Transparent, Child = t
            };
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "BgHover");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
            return b;
        }

        private void AdjustFormFontSize(int delta, TextBlock sizeLbl)
        {
            if (_activeFormTb is null) return;
            double scale = _activeFormScale > 0 ? _activeFormScale : 1;
            double pt = Math.Round(_activeFormTb.FontSize / scale);
            pt = Math.Max(4, Math.Min(96, pt + delta));
            _formFontSizes[_activeFormObj] = pt;
            _activeFormTb.FontSize = pt * scale;
            sizeLbl.Text = pt.ToString("0");
            MarkDirty(true);
        }

        private void HideFormSizeBar()
        {
            if (_formSizeBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_formSizeBar);
                _formSizeBar = null;
            }
        }

        // Returns a /DA default-appearance string with its font size replaced (or a sensible default
        // when none exists), used to bake a user font-size override into the saved field.
        private static string WithDaFontSize(string? da, double pt)
        {
            string size = pt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(da)) return $"/Helv {size} Tf 0 g";
            var t = da!.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int i = 1; i < t.Count; i++)
                if (t[i] == "Tf") { t[i - 1] = size; return string.Join(" ", t); }
            return $"/Helv {size} Tf " + da;   // no Tf operator present; prepend a font selection
        }

        // ============================================================
        // Signatures
        // ============================================================

        private void LoadSignatures() => _signatureStore.Load();

        private void PersistSignatures() => _signatureStore.Persist();

        private void ShowSignaturePopup()
        {
            // NOTE: this popup is rebuilt on every open. All event handlers here are lambdas
            // on the popup's own child elements — no external source subscriptions, so no leak.
            // If SignatureStore.Signatures ever becomes ObservableCollection and this popup
            // subscribes to CollectionChanged, use CollectionChangedEventManager instead of +=.
            HideSignaturePopup();

            var stack = new StackPanel { Margin = new Thickness(4) };

            // Title doubles as a drag handle so the user can move the popup anywhere inside the
            // document area (position is remembered). Wrapped in a transparent Border so the whole
            // title strip is grabbable, not just the text glyphs.
            var sigHeaderGrid = new Grid();
            sigHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sigHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var sigTitleText = new TextBlock
            {
                Text = Loc("Str_Sig_Title"),
                Foreground = (SolidColorBrush)FindResource("Accent"),   // accent heading, shared secondary-window style
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 2, ShadowDepth = 1, Direction = 270, Opacity = 0.7 }
            };
            Grid.SetColumn(sigTitleText, 0);
            // Close X, matching the Settings panel's close glyph (Segoe MDL2). A TextBlock (not Button)
            // keeps it chromeless; e.Handled stops the click from starting the header drag.
            var sigCloseBtn = new TextBlock
            {
                Text = "\ue711",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(4)
            };
            sigCloseBtn.MouseEnter += (_, _) => { sigCloseBtn.Foreground = (SolidColorBrush)FindResource("DangerRed"); sigCloseBtn.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.5 }; };
            sigCloseBtn.MouseLeave += (_, _) => { sigCloseBtn.Foreground = (SolidColorBrush)FindResource("TextSecondary"); sigCloseBtn.Effect = null; };
            sigCloseBtn.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                HideSignaturePopup();
                if (_currentTool == EditTool.Signature && _pendingSignature is null)
                    SetTool(EditTool.Select);
            };
            Grid.SetColumn(sigCloseBtn, 1);
            sigHeaderGrid.Children.Add(sigTitleText);
            sigHeaderGrid.Children.Add(sigCloseBtn);
            var sigHeader = new Border
            {
                Background = Brushes.Transparent,
                Margin     = new Thickness(0, 0, 0, 4),
                Child = sigHeaderGrid
            };
            stack.Children.Add(sigHeader);

            // Saved signatures and initials, shown as two labelled sections so the HR-style
            // "initial here, sign there" flow can pick each independently. One tile builder is shared.
            UIElement MakeSigItem(SavedSignature sigCopy)
            {
                var item = new Border
                {
                    Background = Brushes.White,
                    BorderBrush = _swatchDimBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(4, 2, 4, 2),
                    Padding = new Thickness(4),
                    Cursor = Cursors.Hand,
                    Height = 60,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                if (sigCopy.ImageData is not null)
                {
                    try
                    {
                        var imgBytes = Convert.FromBase64String(sigCopy.ImageData);
                        var bmpImg = new System.Windows.Media.Imaging.BitmapImage();
                        bmpImg.BeginInit();
                        bmpImg.StreamSource = new System.IO.MemoryStream(imgBytes);
                        bmpImg.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bmpImg.EndInit();
                        item.Child = new System.Windows.Controls.Image
                        {
                            Source = bmpImg,
                            Height = 50,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Stretch = System.Windows.Media.Stretch.Uniform,
                            IsHitTestVisible = false
                        };
                    }
                    catch { item.Child = new TextBlock { Text = "(image)", IsHitTestVisible = false }; }
                }
                else
                {
                    var canvas = new Canvas
                    {
                        Width = 288, Height = 50,
                        Background = Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    RenderSignaturePreview(canvas, sigCopy, 288, 50);
                    item.Child = canvas;
                }

                item.MouseLeftButtonDown += (s, e) =>
                {
                    HideSignaturePopup();
                    // If a signature field is waiting, fill it and remember the choice (pick once, reuse).
                    if (_pendingSignField is { } tgt)
                    {
                        if (tgt.Initials) _activeInitialsChoice = sigCopy; else _activeSignatureChoice = sigCopy;
                        var t = tgt; _pendingSignField = null;
                        DropSignatureInField(t.ObjNum, sigCopy, t.Page, t.X, t.Y, t.W, t.H);
                        return;
                    }
                    _pendingSignature = sigCopy;
                    _annotationCanvas.Cursor = Cursors.Cross;
                    SetStatus(sigCopy.Kind == SignatureKind.Initials
                        ? "Click on the page to place your initials"
                        : "Click on the page to place your signature");
                };
                item.MouseEnter += (s, e) =>
                    ((Border)s!).BorderBrush = (SolidColorBrush)FindResource("Accent");
                item.MouseLeave += (s, e) =>
                    ((Border)s!).BorderBrush = _swatchDimBorder;

                var itemGrid = new Grid();
                itemGrid.Children.Add(item);

                var delBtn = new Button
                {
                    Content = "\ue711",
                    FontSize = 10,
                    Width = 18, Height = 18,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 2, 0),
                    Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                    Foreground = (SolidColorBrush)FindResource("DangerRed"),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(0),
                    Style = (Style)FindResource("ToolbarButton")
                };
                delBtn.Click += (s, e) =>
                {
                    _signatureStore.Remove(sigCopy);
                    PersistSignatures();
                    ShowSignaturePopup(); // refresh
                };
                itemGrid.Children.Add(delBtn);
                return itemGrid;
            }

            // Section = header + saved tiles (or a "none yet" hint) + Create/Import for that Kind.
            void AddSigSection(string sectionTitle, SignatureKind kind)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = sectionTitle,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 11,
                    Margin = new Thickness(4, 6, 4, 2)
                });

                var items = _signatureStore.Signatures.Where(x => x.Kind == kind).ToList();
                if (items.Count > 0)
                {
                    var scroll = new ScrollViewer
                    {
                        MaxHeight = 170,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                    };
                    var listPanel = new StackPanel();
                    foreach (var sig in items)
                        listPanel.Children.Add(MakeSigItem(sig));
                    scroll.Content = listPanel;
                    stack.Children.Add(scroll);
                }
                else
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = Loc("Str_Sig_None"),
                        Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 11,
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(4, 2, 4, 6),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                }

                var rowBtns = new Grid { Margin = new Thickness(4, 8, 4, 2) };
                rowBtns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowBtns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var createBtn = UiButtons.Make(Loc("Str_Sig_Create"), accent: true);
                createBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                createBtn.Margin = new Thickness(0, 0, 3, 0);
                createBtn.Click += (s, e) => { HideSignaturePopup(); OpenSignatureCreator(kind); ShowSignaturePopup(); };
                var importBtn = UiButtons.Make(Loc("Str_Sig_Import"), accent: false);
                importBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
                importBtn.Margin = new Thickness(3, 0, 0, 0);
                importBtn.Click += (s, e) => { HideSignaturePopup(); ImportImageSignature(kind); ShowSignaturePopup(); };
                Grid.SetColumn(createBtn, 0);
                Grid.SetColumn(importBtn, 1);
                rowBtns.Children.Add(createBtn);
                rowBtns.Children.Add(importBtn);
                stack.Children.Add(rowBtns);
            }

            AddSigSection("Signatures", SignatureKind.Signature);

            stack.Children.Add(new Rectangle
            {
                Height = 1,
                Fill = (SolidColorBrush)FindResource("BorderDim"),
                Margin = new Thickness(4, 6, 4, 2)
            });

            AddSigSection("Initials", SignatureKind.Initials);

            // Match the Settings/menu popups: themed modal surface + accent border + film grain.
            var sigContent = new Grid();
            sigContent.Children.Add(new Border
            {
                CornerRadius     = new CornerRadius(6),
                IsHitTestVisible = false,
                Opacity          = (double)FindResource("GrainOpacity"),
                Background       = (System.Windows.Media.Brush)FindResource("GrainBrushShared")
            });
            sigContent.Children.Add(stack);

            _signaturePopup = new Border
            {
                Background = (SolidColorBrush)FindResource("BgModal"),
                BorderBrush = (SolidColorBrush)FindResource("AccentBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                Child = sigContent,
                // Free-positioned (Left/Top) inside the document grid so it can be dragged; the
                // exact spot is set after layout from the saved position (or a default top-right).
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 16, Opacity = 0.55, ShadowDepth = 3
                }
            };

            var previewGrid = PagePreviewPanel.Parent as Grid;
            if (previewGrid is not null)
            {
                Panel.SetZIndex(_signaturePopup, 200);
                previewGrid.Children.Add(_signaturePopup);
                // Freshly-inserted element: defer the fade until it's laid out, otherwise the
                // animation is missed (unlike the always-present Settings/About overlays).
                _signaturePopup.Opacity = 0;
                var popup = _signaturePopup;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                {
                    if (popup is null) return;
                    // Place it (saved position, or default to the old top-right spot) and wire up
                    // dragging now that ActualWidth/Height are known.
                    ApplySavedPanelPosition(popup, previewGrid, "SigPopup", fallbackRightInset: 80, fallbackTop: 4);
                    EnablePanelDrag(sigHeader, popup, previewGrid, "SigPopup");
                    popup.BeginAnimation(UIElement.OpacityProperty,
                        new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110)))
                        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                }));
            }
        }

        private void HideSignaturePopup()
        {
            if (_signaturePopup is not null)
            {
                var previewGrid = PagePreviewPanel.Parent as Grid;
                previewGrid?.Children.Remove(_signaturePopup);
                _signaturePopup = null;
            }
        }

        // ---- Draggable floating panels (signature popup, settings panel) -------------------------

        /// <summary>
        /// Clamps a Left/Top so a panel stays fully inside its container's bounds.
        /// </summary>
        private static void ClampPanelToBounds(FrameworkElement panel, FrameworkElement bounds,
                                               ref double left, ref double top)
        {
            double w = panel.ActualWidth  > 0 ? panel.ActualWidth  : panel.Width;
            double h = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
            if (double.IsNaN(w)) w = 0;
            if (double.IsNaN(h)) h = 0;
            double maxLeft = Math.Max(0, bounds.ActualWidth  - w);
            double maxTop  = Math.Max(0, bounds.ActualHeight - h);
            left = Math.Max(0, Math.Min(maxLeft, left));
            top  = Math.Max(0, Math.Min(maxTop,  top));
        }

        /// <summary>
        /// Positions a Left/Top-aligned floating panel from its saved position, falling back to a
        /// top-right inset when nothing is stored. Always clamped inside <paramref name="bounds"/>.
        /// Must run after layout so the panel's ActualWidth/Height are known.
        /// </summary>
        private void ApplySavedPanelPosition(FrameworkElement panel, FrameworkElement bounds, string keyPrefix,
                                             double fallbackRightInset, double fallbackTop)
        {
            double w = panel.ActualWidth > 0 ? panel.ActualWidth : (double.IsNaN(panel.Width) ? 0 : panel.Width);
            double left, top;
            if (int.TryParse(App.GetSetting(keyPrefix + "Left"), out int sl) &&
                int.TryParse(App.GetSetting(keyPrefix + "Top"),  out int st))
            {
                left = sl; top = st;
            }
            else
            {
                left = bounds.ActualWidth - w - fallbackRightInset;
                top  = fallbackTop;
            }
            ClampPanelToBounds(panel, bounds, ref left, ref top);
            panel.Margin = new Thickness(left, top, 0, 0);
        }

        /// <summary>
        /// Makes <paramref name="handle"/> drag <paramref name="panel"/> within <paramref name="bounds"/>,
        /// clamped to stay inside, and persists the resulting position under <paramref name="keyPrefix"/>.
        /// </summary>
        private void EnablePanelDrag(FrameworkElement handle, FrameworkElement panel, FrameworkElement bounds,
                                     string keyPrefix)
        {
            handle.Cursor = Cursors.SizeAll;
            Point start = default;
            Thickness orig = default;
            bool dragging = false;

            handle.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                start = e.GetPosition(bounds);
                orig  = panel.Margin;
                handle.CaptureMouse();
                e.Handled = true;
            };
            handle.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var p = e.GetPosition(bounds);
                double nl = orig.Left + (p.X - start.X);
                double nt = orig.Top  + (p.Y - start.Y);
                ClampPanelToBounds(panel, bounds, ref nl, ref nt);
                panel.Margin = new Thickness(nl, nt, 0, 0);
            };
            handle.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                handle.ReleaseMouseCapture();
                App.SetSetting(keyPrefix + "Left", ((int)panel.Margin.Left).ToString());
                App.SetSetting(keyPrefix + "Top",  ((int)panel.Margin.Top).ToString());
                e.Handled = true;
            };
        }

        private void RenderSignaturePreview(Canvas canvas, SavedSignature sig, double targetW, double targetH)
        {
            double scaleX = targetW / sig.CanvasWidth;
            double scaleY = targetH / sig.CanvasHeight;
            double scale = Math.Min(scaleX, scaleY) * 0.9;

            double offsetX = (targetW - sig.CanvasWidth * scale) / 2;
            double offsetY = (targetH - sig.CanvasHeight * scale) / 2;

            foreach (var stroke in sig.Strokes)
            {
                if (stroke.Count < 2) continue;
                var poly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = Math.Max(0.8, sig.StrokeWidth * scale),
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                foreach (var pt in stroke)
                    poly.Points.Add(new Point(pt.X * scale + offsetX, pt.Y * scale + offsetY));
                canvas.Children.Add(poly);
            }
        }

        private void OpenSignatureCreator(SignatureKind kind = SignatureKind.Signature)
        {
            var win = new Window
            {
                Title = "Create Signature",
                Width = 460,
                SizeToContent = SizeToContent.Height,   // size to content so there's no empty padding below
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent
            };
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(win, TextRenderingMode.Grayscale);

            // Outer chrome
            var outerChrome = new Border
            {
                Background      = (SolidColorBrush)FindResource("BgModal"),
                BorderBrush     = (SolidColorBrush)FindResource("PaneBorder"),   // 1px doc-pane frame
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(7),
                Margin          = new Thickness(10),    // transparent halo so the drop shadow can render
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.6
                }
            };
            var rootStack = new StackPanel();

            // Title bar - transparent so the window-wide film grain shows through it too. No padding;
            // the chrome close button sets the (slim) height and the title gets its own left inset.
            var titleBar = new Border
            {
                Background   = Brushes.Transparent,
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // Wordmark + subtitle. A LAYERED shadow (a blurred dark copy behind a crisp copy) gives
            // the text a soft shadow without an Effect rasterizing/blurring the visible text itself.
            StackPanel BuildSigTitle(bool shadow)
            {
                var fam = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI");
                System.Windows.Media.Brush primary = shadow ? Brushes.Black : (System.Windows.Media.Brush)FindResource("TextPrimary");
                System.Windows.Media.Brush logo    = shadow ? Brushes.Black : (System.Windows.Media.Brush)FindResource("AccentLogo");
                System.Windows.Media.Brush sub      = shadow ? Brushes.Black : (System.Windows.Media.Brush)FindResource("TextSecondary");
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = "Killer", FontFamily = fam, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = primary, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = "PDF",    FontFamily = fam, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = logo, VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = $" - {Loc("Str_Sig_Create")}", FontFamily = fam, FontSize = 13, Foreground = sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 1, 0, 0) });
                return sp;
            }
            var titleText = new Grid { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleShadow = BuildSigTitle(true);
            titleShadow.Opacity = 0.5;
            titleShadow.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 2 };
            titleShadow.RenderTransform = new TranslateTransform(0.7, 1.2);
            titleText.Children.Add(titleShadow);
            titleText.Children.Add(BuildSigTitle(false));
            Grid.SetColumn(titleText, 0);
            var closeWinBtn = new Button
            {
                Content         = "",
                VerticalAlignment = VerticalAlignment.Top,
                // Same chrome close button as the print dialog / main window: red fill on hover
                // that follows the window's rounded top-right corner.
                Style = (Style)FindResource("ChromeCloseButton")
            };
            closeWinBtn.Click += (_, _2) => win.Close();
            Grid.SetColumn(closeWinBtn, 1);
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeWinBtn);
            titleBar.Child = titleGrid;
            rootStack.Children.Add(titleBar);

            var contentArea = new StackPanel();

            // Drawing canvas
            var canvasBorder = new Border
            {
                Background = Brushes.White,
                // Faint outline so the white drawing pane reads as a distinct field on the modal.
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xcc, 0xcc, 0xcc)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(12, 12, 12, 4),
                CornerRadius = new CornerRadius(4),
                Height = 170
            };
            var drawCanvas = new Canvas
            {
                Background = Brushes.White,
                ClipToBounds = true,
                Cursor = Cursors.Pen
            };
            canvasBorder.Child = drawCanvas;

            // Placeholder text
            // In a Canvas, alignment is ignored, so position the hint with a little padding rather
            // than leaving it jammed in the top-left corner. A script face suits a signature prompt.
            var placeholder = new TextBlock
            {
                Text = Loc("Str_Sig_DrawHere"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xb0, 0xb0, 0xb0)),
                FontFamily = new FontFamily("Segoe Script, Segoe UI"),
                FontSize = 18,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(placeholder, 18);
            Canvas.SetTop(placeholder, 14);
            drawCanvas.Children.Add(placeholder);

            // Drawing state
            var strokes = new List<List<Point>>();
            List<Point>? currentStroke = null;
            Polyline? currentPoly = null;
            double penWidth = 2.5;   // medium; set by the pen-width selector below

            drawCanvas.MouseLeftButtonDown += (s, e) =>
            {
                if (placeholder.Visibility == Visibility.Visible)
                    placeholder.Visibility = Visibility.Collapsed;
                currentStroke = [];
                var pos = e.GetPosition(drawCanvas);
                currentStroke.Add(pos);
                currentPoly = new Polyline
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = penWidth,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                currentPoly.Points.Add(pos);
                drawCanvas.Children.Add(currentPoly);
                drawCanvas.CaptureMouse();
            };

            drawCanvas.MouseMove += (s, e) =>
            {
                if (currentStroke is null || currentPoly is null) return;
                var pos = e.GetPosition(drawCanvas);
                pos.X = Math.Max(0, Math.Min(drawCanvas.ActualWidth, pos.X));
                pos.Y = Math.Max(0, Math.Min(drawCanvas.ActualHeight, pos.Y));
                currentStroke.Add(pos);
                currentPoly.Points.Add(pos);
            };

            drawCanvas.MouseLeftButtonUp += (s, e) =>
            {
                if (currentStroke is not null && currentStroke.Count > 1)
                    strokes.Add(currentStroke);
                else if (currentPoly is not null)
                    drawCanvas.Children.Remove(currentPoly);
                currentStroke = null;
                currentPoly = null;
                drawCanvas.ReleaseMouseCapture();
            };

            contentArea.Children.Add(canvasBorder);

            // Pen-width selector: three preset thicknesses, active one highlighted. On the left so the
            // modal does not read bottom-right-heavy.
            var penRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 6, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            penRow.Children.Add(new TextBlock { Text = "Pen", Foreground = (SolidColorBrush)FindResource("TextSecondary"), FontFamily = new FontFamily("Segoe UI"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var penOptions = new (string Label, double W)[] { ("Thin", 1.5), ("Medium", 2.5), ("Thick", 4.0) };
            var penBtns = new List<Button>();
            void RefreshPen()
            {
                for (int bi = 0; bi < penBtns.Count; bi++)
                {
                    bool active = Math.Abs(penOptions[bi].W - penWidth) < 0.01;
                    penBtns[bi].Background  = active ? (SolidColorBrush)FindResource("SelectionBg") : (SolidColorBrush)FindResource("BgPanel");
                    penBtns[bi].Foreground  = active ? (SolidColorBrush)FindResource("SelectionFg") : (SolidColorBrush)FindResource("TextPrimary");
                    penBtns[bi].BorderBrush = active ? (SolidColorBrush)FindResource("Accent")      : (SolidColorBrush)FindResource("BorderDim");
                }
            }
            foreach (var (lbl, w) in penOptions)
            {
                double ww = w;
                var pb = new Button
                {
                    Content = lbl,
                    Style = (Style)FindResource("DarkButton"),
                    Padding = new Thickness(12, 3, 12, 3),
                    Margin = new Thickness(0, 0, 6, 0),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11
                };
                pb.Click += (s2, e2) => { penWidth = ww; RefreshPen(); };
                penBtns.Add(pb);
                penRow.Children.Add(pb);
            }
            RefreshPen();
            contentArea.Children.Add(penRow);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 4, 12, 12)
            };

            var clearBtn = UiButtons.Make(Loc("Str_Sig_Clear"), accent: false);
            clearBtn.Margin = new Thickness(0, 0, 8, 0);
            clearBtn.Click += (s, e) =>
            {
                strokes.Clear();
                drawCanvas.Children.Clear();
                placeholder.Visibility = Visibility.Visible;
                drawCanvas.Children.Add(placeholder);
            };

            var saveBtn = UiButtons.Make(Loc("Str_Sig_SaveSig"), accent: true);
            saveBtn.Click += (s, e) =>
            {
                if (strokes.Count == 0)
                {
                    KillerDialog.Show(this, "Draw a signature first.", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double cw = drawCanvas.ActualWidth > 0 ? drawCanvas.ActualWidth : 400;
                double ch = drawCanvas.ActualHeight > 0 ? drawCanvas.ActualHeight : 150;

                var saved = new SavedSignature
                {
                    Kind = kind,
                    StrokeWidth = penWidth,
                    CanvasWidth = cw,
                    CanvasHeight = ch,
                    Name = $"{(kind == SignatureKind.Initials ? "Initials" : "Signature")} {_signatureStore.Signatures.Count(x => x.Kind == kind) + 1}"
                };
                foreach (var stroke in strokes)
                {
                    var sPts = stroke.Select(p => new SerializablePoint { X = p.X, Y = p.Y }).ToList();
                    saved.Strokes.Add(sPts);
                }
                _signatureStore.Add(saved);
                PersistSignatures();

                // Auto-select the new signature for placement
                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Signature saved - click on the page to place it");

                win.Close();
            };

            btnPanel.Children.Add(clearBtn);
            btnPanel.Children.Add(saveBtn);
            contentArea.Children.Add(btnPanel);

            rootStack.Children.Add(contentArea);

            // Film grain behind everything (incl. the transparent title bar) so this window carries
            // the same texture as the rest of the app.
            var creatorGrid = new Grid();
            creatorGrid.Children.Add(new Border
            {
                CornerRadius     = new CornerRadius(6),
                IsHitTestVisible = false,
                Opacity          = (double)FindResource("GrainOpacity"),
                Background       = (System.Windows.Media.Brush)FindResource("GrainBrushShared")
            });
            creatorGrid.Children.Add(rootStack);
            outerChrome.Child = creatorGrid;
            win.Content = outerChrome;
            win.ShowDialog();
        }

        private void ImportImageSignature(SignatureKind kind = SignatureKind.Signature)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
                Title = "Import Signature Image"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage(new Uri(dlg.FileName));
                byte[] pngBytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                var saved = new SavedSignature
                {
                    Kind = kind,
                    Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName),
                    CanvasWidth = bmp.PixelWidth,
                    CanvasHeight = bmp.PixelHeight,
                    ImageData = Convert.ToBase64String(pngBytes)
                };
                _signatureStore.Add(saved);
                PersistSignatures();

                _pendingSignature = saved;
                _annotationCanvas.Cursor = Cursors.Cross;
                SetStatus("Image loaded - click on the page to place it");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Failed to import image:\n{ex.Message}", "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PlaceSignature(Point pos, int pageIdx)
        {
            if (_pendingSignature is null) return;

            var sig = _pendingSignature;
            double scale = sig.Kind == SignatureKind.Initials ? 0.3 : 0.5;

            var annot = new SignatureAnnotation
            {
                PageIndex = pageIdx,
                Position = pos,
                Scale = scale,
                StrokeWidth = sig.StrokeWidth,
                SourceWidth = sig.CanvasWidth,
                SourceHeight = sig.CanvasHeight,
                ImageData = sig.ImageData
            };

            // Drawn signature — convert serializable points to WPF points
            if (sig.ImageData is null)
            {
                foreach (var stroke in sig.Strokes)
                    annot.Strokes.Add([..stroke.Select(p => new Point(p.X, p.Y))]);
            }

            AddAnnotation(annot);
            RenderAllAnnotations(pageIdx);

            // Auto-switch to Select and select the placed signature so the user
            // can immediately reposition or resize without an extra click.
            SetTool(EditTool.Select);
            double sigW = sig.CanvasWidth * scale;
            double sigH = sig.CanvasHeight * scale;
            SelectAnnotation(annot, new Rect(pos.X, pos.Y, sigW, sigH));
            SetStatus("Signature placed — drag to reposition, use the corner handle to resize");
        }

        // Guided AcroForm signing -------------------------------------------------------------------
        private enum FormFillRole { None, Signature, Initials, Date }

        // Classifies a fillable text field by its name so signature / initials fields become
        // click-to-sign zones. Checkboxes, radios, and dropdowns are never sign fields.
        private static FormFillRole ClassifyFormField(FormFieldInfo f)
        {
            if (f.IsCheckBox || f.IsRadio || f.FieldType == "/Ch") return FormFillRole.None;
            string n = (f.FieldName ?? string.Empty).ToLowerInvariant();
            if (n.Contains("initial")) return FormFillRole.Initials;
            if (n.Contains("signature") || n.Contains("sign")) return FormFillRole.Signature;
            if (n.Contains("date")) return FormFillRole.Date;
            return FormFillRole.None;
        }

        // A highlighted, clickable overlay sized to the field rectangle. Clicking fills it.
        private UIElement BuildSignZone(FormFieldInfo f, bool initials, int pageIndex)
        {
            var accent = Color.FromRgb(0x2a, 0x6e, 0xa5);
            var zone = new Border
            {
                Tag             = FormOverlayTag,
                Width           = f.Cw,
                Height          = f.Ch,
                Background      = new SolidColorBrush(Color.FromArgb(38, accent.R, accent.G, accent.B)),
                BorderBrush     = new SolidColorBrush(Color.FromArgb(190, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1.4),
                CornerRadius    = new CornerRadius(2),
                Cursor          = Cursors.Hand,
                ToolTip         = initials ? "Click to add your initials" : "Click to sign",
                Child = new TextBlock
                {
                    Text                = initials ? "Initial" : "Sign",
                    FontSize            = Math.Max(8, Math.Min(f.Ch * 0.45, 12)),
                    FontWeight          = FontWeights.SemiBold,
                    Foreground          = new SolidColorBrush(accent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    IsHitTestVisible    = false,
                },
            };
            double zx = f.Cx, zy = f.Cy, zw = f.Cw, zh = f.Ch; int zp = pageIndex, zo = f.ObjNum;
            zone.MouseLeftButtonDown += (_, e) => { e.Handled = true; FillSignField(initials, zo, zp, zx, zy, zw, zh); };
            return zone;
        }

        // Already signed -> change/remove menu. Otherwise drop the reusable choice, or open the
        // picker the first time and route the pick back to this field.
        private void FillSignField(bool initials, int objNum, int pageIndex, double x, double y, double w, double h)
        {
            if (_signedFields.ContainsKey(objNum))
            {
                ShowSignedFieldMenu(initials, objNum, pageIndex, x, y, w, h);
                return;
            }
            var choice = initials ? _activeInitialsChoice : _activeSignatureChoice;
            if (choice is null)
            {
                _pendingSignField = (initials, objNum, pageIndex, x, y, w, h);
                ShowSignaturePopup();
                SetStatus(initials
                    ? "Choose initials - they will be reused for every initials field"
                    : "Choose a signature - it will be reused for every signature field");
                return;
            }
            DropSignatureInField(objNum, choice, pageIndex, x, y, w, h);
        }

        // Re-clicking a signed field: change (re-pick) or remove it.
        private void ShowSignedFieldMenu(bool initials, int objNum, int pageIndex, double x, double y, double w, double h)
        {
            string what = initials ? "initials" : "signature";
            var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            menu.Items.Add(MakeMenuItem("Change " + what, (_, _) =>
            {
                RemoveSignedField(objNum, pageIndex);
                _pendingSignField = (initials, objNum, pageIndex, x, y, w, h);
                ShowSignaturePopup();
            }));
            menu.Items.Add(MakeMenuItem("Remove " + what, (_, _) => RemoveSignedField(objNum, pageIndex)));
            menu.IsOpen = true;
        }

        // Deletes the signature placed in a field and clears its signed state.
        private void RemoveSignedField(int objNum, int pageIndex)
        {
            if (!_signedFields.TryGetValue(objNum, out var annot)) return;
            if (_annotations.TryGetValue(pageIndex, out var list)) list.Remove(annot);
            _signedFields.Remove(objNum);
            RenderAllAnnotations(pageIndex);
            MarkDirty(true);
            SetStatus("Field cleared");
        }

        // Places a SignatureAnnotation centred in and scaled to fit the field rectangle.
        private void DropSignatureInField(int objNum, SavedSignature sig, int pageIndex, double x, double y, double w, double h)
        {
            const double pad = 2;
            double sw = sig.CanvasWidth, sh = sig.CanvasHeight;
            double scale = Math.Min((w - 2 * pad) / sw, (h - 2 * pad) / sh);
            if (scale <= 0) scale = Math.Min(w / sw, h / sh);
            double drawW = sw * scale, drawH = sh * scale;
            double px = x + (w - drawW) / 2;
            double py = y + (h - drawH) / 2;

            var annot = new SignatureAnnotation
            {
                PageIndex    = pageIndex,
                Position     = new Point(px, py),
                Scale        = scale,
                StrokeWidth  = sig.StrokeWidth,
                SourceWidth  = sw,
                SourceHeight = sh,
                ImageData    = sig.ImageData,
            };
            if (sig.ImageData is null)
                foreach (var stroke in sig.Strokes)
                    annot.Strokes.Add([.. stroke.Select(pt => new Point(pt.X, pt.Y))]);

            _signedFields[objNum] = annot;
            AddAnnotation(annot);
            RenderAllAnnotations(pageIndex);
            MarkDirty(true);
            SetStatus("Field signed");
        }

        private void PlaceImageFromDialog(Point pos, int pageIdx)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Insert Image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff;*.tif|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var imgBytes = File.ReadAllBytes(dlg.FileName);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new MemoryStream(imgBytes);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();

                double srcW = bmp.PixelWidth > 0 ? bmp.PixelWidth : 400;
                double srcH = bmp.PixelHeight > 0 ? bmp.PixelHeight : 300;

                // Default the placed image to ~50% of the page's longest side (in render-dim
                // units) so it is a usable size regardless of page dimensions, never upscaling
                // beyond the source's native resolution.
                double pageMax = _renderDims.TryGetValue(pageIdx, out var rdImg)
                    ? Math.Max(rdImg.w, rdImg.h) : 2048.0;
                double MaxCanvasDim = pageMax * 0.5;
                double scale = Math.Min(1.0, Math.Min(MaxCanvasDim / srcW, MaxCanvasDim / srcH));

                var imgAnnot = new ImageAnnotation
                {
                    PageIndex = pageIdx,
                    Position = pos,
                    Scale = scale,
                    SourceWidth = srcW,
                    SourceHeight = srcH,
                    ImageData = Convert.ToBase64String(imgBytes)
                };

                // Switch to Select FIRST so placement renders last and nothing wipes the image
                // (calling SetTool between render and select was what made the image vanish).
                SetTool(EditTool.Select);
                AddAnnotation(imgAnnot);
                RenderAllAnnotations(pageIdx);
                double w = srcW * scale;
                double h = srcH * scale;
                SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                SetStatus("Image placed - drag to reposition, use the corner handle to resize");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Could not load image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Ctrl+V: drop a clipboard image (as an image annotation) or clipboard text (as a text
        // annotation) onto the current page, centered, then select it. Coordinates are in the page's
        // render-dim space (== _renderDims[page]), matching how clicks place annotations.
        private void PasteFromClipboard()
        {
            if (_doc is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) pageIdx = 0;
            if (pageIdx >= _doc.PageCount) return;

            double pw = _renderDims.TryGetValue(pageIdx, out var rd) ? rd.w : 2048.0;
            double ph = _renderDims.TryGetValue(pageIdx, out var rd2) ? rd2.h : 2048.0;

            try
            {
                if (Clipboard.ContainsImage())
                {
                    var src = Clipboard.GetImage();
                    if (src is null) { SetStatus("Clipboard image could not be read"); return; }

                    // Encode to PNG so ImageAnnotation stores standard bytes (same as file import).
                    byte[] imgBytes;
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                    using (var ms = new MemoryStream()) { encoder.Save(ms); imgBytes = ms.ToArray(); }

                    double srcW = src.PixelWidth  > 0 ? src.PixelWidth  : 400;
                    double srcH = src.PixelHeight > 0 ? src.PixelHeight : 300;
                    double pageMax = Math.Max(pw, ph);
                    double maxCanvasDim = pageMax * 0.5;
                    double scale = Math.Min(1.0, Math.Min(maxCanvasDim / srcW, maxCanvasDim / srcH));
                    double w = srcW * scale, h = srcH * scale;
                    var pos = new Point((pw - w) / 2, (ph - h) / 2);

                    var imgAnnot = new ImageAnnotation
                    {
                        PageIndex = pageIdx, Position = pos, Scale = scale,
                        SourceWidth = srcW, SourceHeight = srcH,
                        ImageData = Convert.ToBase64String(imgBytes)
                    };
                    SetTool(EditTool.Select);
                    AddAnnotation(imgAnnot);
                    RenderAllAnnotations(pageIdx);
                    SelectAnnotation(imgAnnot, new Rect(pos.X, pos.Y, w, h));
                    SetStatus("Pasted image - drag to reposition, use the corner handle to resize");
                }
                else if (Clipboard.ContainsText())
                {
                    string content = Clipboard.GetText().Trim();
                    if (string.IsNullOrEmpty(content)) { SetStatus("Clipboard has no text to paste"); return; }

                    // Convert the point size to the page's canvas units (see PlaceTextBox).
                    double fontCanvas = _textFontSize;
                    double sy = _doc.Pages[pageIdx].Height.Point / Math.Max(1.0, ph);
                    if (sy > 0) fontCanvas = _textFontSize / sy;

                    var ta = new TextAnnotation
                    {
                        PageIndex = pageIdx,
                        Position  = new Point(pw * 0.25, ph * 0.45),
                        Content   = content,
                        FontSize  = fontCanvas
                    };
                    ta.SetColor(_textColor);
                    SetTool(EditTool.Select);
                    AddAnnotation(ta);
                    RenderAllAnnotations(pageIdx);
                    SelectAnnotation(ta, AnnotBounds(ta));
                    SetStatus("Pasted text - drag to reposition, Delete to remove");
                }
                else
                {
                    SetStatus("Clipboard has nothing to paste");
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Could not paste:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ============================================================
        // Canvas interaction
        // ============================================================

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_doc is null) return;
            if (sender is Canvas srcCanvas) _activeCanvas = srcCanvas;
            // A click on a live text-edit corner handle starts a free-form resize. Checked FIRST, before
            // the "click inside the editing box" guard below: that guard tests OriginalSource, which is
            // unreliable across the nested transparent canvases, so it can otherwise swallow a corner
            // click right after placement. This is a reliable position-based hit test.
            if (_textEditHandles.Count > 0 && _tehBox is not null)
            {
                var hpos = e.GetPosition(_activeCanvas);
                string? corner = TextEditHandleAt(hpos);
                if (corner is not null)
                {
                    _tehCorner = corner;
                    double bx = Canvas.GetLeft(_tehBox), by = Canvas.GetTop(_tehBox);
                    double bw = _tehBox.ActualWidth  > 0 ? _tehBox.ActualWidth  : _tehBox.Width;
                    double bh = _tehBox.ActualHeight > 0 ? _tehBox.ActualHeight : Math.Max(_tehBox.MinHeight, 24);
                    _tehAnchor = _tehCorner switch
                    {
                        "NW" => new Point(bx + bw, by + bh),
                        "NE" => new Point(bx,      by + bh),
                        "SW" => new Point(bx + bw, by),
                        _    => new Point(bx,      by)   // SE
                    };
                    _draggingTextEditHandle = true;
                    _gestureCanvas = _activeCanvas;
                    _gesturePage   = _activeCanvas.Tag is int tp ? tp : PageList.SelectedIndex;
                    _activeCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }
            // Don't intercept clicks on an active text editing box
            if (_activeTextBox is not null && e.OriginalSource is DependencyObject src &&
                IsDescendantOf(src, _activeTextBox))
                return;
            // Don't intercept clicks on the crop confirm bar (canvas uses Preview events which
            // tunnel before child Button clicks fire — we must not swallow them here).
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;
            // Don't intercept clicks on form field overlay controls (TextBox, CheckBox, etc.)
            // — WPF must handle those natively so focus, toggling, and text entry work.
            if (e.OriginalSource is DependencyObject formSrc && IsFormFieldElement(formSrc))
                return;
            // Check if click lands inside a PDF link overlay.
            // We do an explicit bounds check rather than relying on WPF hit-testing through
            // nested transparent canvases, which is unreliable. Links are only followed with the
            // Select tool (so drawing/typing over a link region edits instead of navigating), and
            // never when the click is on an annotation - annotations stay selectable over a link
            // (those orphan-scan PDFs embed a page-spanning site link that otherwise eats every click).
            if (_currentTool == EditTool.Select && _linkOverlays.Count > 0)
            {
                var clickPos = e.GetPosition(_activeCanvas);
                int linkPage = _activeCanvas.Tag is int ltp ? ltp : PageList.SelectedIndex;
                bool onAnnot = _annotations.TryGetValue(linkPage, out var lal)
                               && lal.Any(a => HitTestAnnotation(a, clickPos, out _));
                foreach (var lo in onAnnot ? Enumerable.Empty<Canvas>() : _linkOverlays)
                {
                    double lx = Canvas.GetLeft(lo);
                    double ly = Canvas.GetTop(lo);
                    if (clickPos.X >= lx && clickPos.X <= lx + lo.Width &&
                        clickPos.Y >= ly && clickPos.Y <= ly + lo.Height)
                    {
                        var lTarget = lo.Tag is LinkAnnotInfo lai ? lai.Target : lo.Tag;
                        if (lTarget is int tp)
                            PageList.SelectedIndex = tp;
                        else if (lTarget is string u)
                            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
                        e.Handled = true;
                        return;
                    }
                }
            }
            var pos = e.GetPosition(_activeCanvas);
            int pageIdx = _activeCanvas.Tag is int tagPage ? tagPage : PageList.SelectedIndex;
            if (pageIdx < 0) return;
            // Pin the surface/page this gesture started on so async re-renders (grid tile streaming)
            // can't redirect the in-progress draw/select to another page. See _gestureCanvas.
            _gestureCanvas = _activeCanvas;
            _gesturePage   = pageIdx;

            // Crop corner handles live in the outer panel and have direct MouseLeftButtonDown
            // handlers attached in AddCropHandles() — no detection needed here.

            // Check if click is on any of the four corner resize handles (signature, image, or text box)
            if (_resizeHandles.Count > 0 && _selectedAnnotation is not null)
            {
                foreach (var hd in _resizeHandles)
                {
                    double hx = Canvas.GetLeft(hd), hy = Canvas.GetTop(hd);
                    if (pos.X >= hx && pos.X <= hx + hd.Width &&
                        pos.Y >= hy && pos.Y <= hy + hd.Height)
                    {
                        _resizeCorner = hd.Tag as string ?? "SE";
                        // Anchor on the opposite corner so it stays put while the dragged corner moves.
                        if (_selectedAnnotation is PlacedAnnotation rsa)
                        {
                            _isResizingSig = true;
                            _resizeSigStart = pos;
                            _resizeSigStartScale = rsa.Scale;
                            _resizeSigAnnot = rsa;
                            double w0 = rsa.SourceWidth * rsa.Scale, h0 = rsa.SourceHeight * rsa.Scale;
                            double ax = rsa.Position.X, ay = rsa.Position.Y;
                            _resizeAnchor = _resizeCorner switch
                            {
                                "NW" => new Point(ax + w0, ay + h0),
                                "NE" => new Point(ax,      ay + h0),
                                "SW" => new Point(ax + w0, ay),
                                _    => new Point(ax,      ay)   // SE
                            };
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                        if (_selectedAnnotation is TextAnnotation rta)
                        {
                            _isResizingSig = true;          // shared "resize in progress" flag + capture
                            _resizeTextAnnot = rta;
                            double ax = rta.Position.X, ay = rta.Position.Y;
                            double w0 = rta.Width, h0 = rta.Height;
                            _resizeAnchor = _resizeCorner switch
                            {
                                "NW" => new Point(ax + w0, ay + h0),
                                "NE" => new Point(ax,      ay + h0),
                                "SW" => new Point(ax + w0, ay),
                                _    => new Point(ax,      ay)   // SE
                            };
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                        if (_selectedAnnotation is HighlightAnnotation rha)
                        {
                            _isResizingSig = true;          // shared "resize in progress" flag + capture
                            _resizeHlAnnot = rha;
                            double ax = rha.Bounds.X, ay = rha.Bounds.Y;
                            double w0 = rha.Bounds.Width, h0 = rha.Bounds.Height;
                            _resizeAnchor = _resizeCorner switch
                            {
                                "NW" => new Point(ax + w0, ay + h0),
                                "NE" => new Point(ax,      ay + h0),
                                "SW" => new Point(ax + w0, ay),
                                _    => new Point(ax,      ay)   // SE
                            };
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                        if (_selectedAnnotation is InkAnnotation rink && rink.Points.Count > 0)
                        {
                            _isResizingSig = true;          // shared "resize in progress" flag + capture
                            _resizeInkAnnot = rink;
                            _resizeInkOrigPoints = [.. rink.Points];
                            double minX = rink.Points.Min(p => p.X), minY = rink.Points.Min(p => p.Y);
                            double maxX = rink.Points.Max(p => p.X), maxY = rink.Points.Max(p => p.Y);
                            _resizeInkOrigBounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
                            _resizeAnchor = _resizeCorner switch
                            {
                                "NW" => new Point(maxX, maxY),
                                "NE" => new Point(minX, maxY),
                                "SW" => new Point(maxX, minY),
                                _    => new Point(minX, minY)   // SE
                            };
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                            return;
                        }
                    }
                }
            }

            switch (_currentTool)
            {
                case EditTool.Select:
                    if (e.ClickCount == 2)
                    {
                        ClearSelection();
                        ClearTextSelection();
                        EditTextAtPosition(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        // Shift+click builds a multi-selection instead of replacing it.
                        bool shiftSel = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                        // Single click: check if hitting a PlacedAnnotation first — select and drag
                        bool hitPlaced = false;
                        if (_annotations.TryGetValue(pageIdx, out var pageAnnotsList))
                        {
                            for (int i = pageAnnotsList.Count - 1; i >= 0; i--)
                            {
                                if (IsDraggable(pageAnnotsList[i]) &&
                                    HitTestAnnotation(pageAnnotsList[i], pos, out Rect paBounds))
                                {
                                    var pa = pageAnnotsList[i];
                                    if (shiftSel)
                                    {
                                        // Toggle in/out of the multi-selection; no drag while shifting.
                                        ToggleMultiSelect(pa, paBounds, _gestureCanvas ?? _activeCanvas);
                                        e.Handled = true;
                                        hitPlaced = true;
                                        break;
                                    }
                                    ClearSelection();
                                    RenderAllAnnotations(pageIdx);
                                    SelectAnnotation(pa, paBounds);
                                    _isDraggingAnnot = true;
                                    _dragAnnotStart = pos;
                                    _dragAnnotOrigPos = AnnotGetPos(pa);
                                    _dragAnnot = pa;
                                    _activeCanvas.CaptureMouse();
                                    e.Handled = true;
                                    hitPlaced = true;
                                    break;
                                }
                            }
                        }
                        if (!hitPlaced)
                        {
                            // Keep the existing multi-selection when shift is held (a click on a
                            // non-draggable annotation is added on mouse-up); only a plain click clears.
                            if (!shiftSel) ClearSelection();
                            ClearTextSelection();
                            _isSelecting = true;
                            _selectStart = pos;
                            _selectRect = new Rectangle
                            {
                                Fill = AccentBrush(40),
                                Stroke = AccentBrush(150),
                                StrokeThickness = 1,
                                Width = 0, Height = 0,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(_selectRect, pos.X);
                            Canvas.SetTop(_selectRect, pos.Y);
                            _activeCanvas.Children.Add(_selectRect);
                            _activeCanvas.CaptureMouse();
                            e.Handled = true;
                        }
                    }
                    break;

                case EditTool.Text:
                    // A click inside the box that's already being edited must NOT commit-and-replace it
                    // (that makes the box appear to jump to the cursor). In Grid view a per-tile overlay
                    // can sit above the TextBox, so the OriginalSource guard near the top of this method
                    // misses the hit; a bounds check on the box's own canvas catches it reliably.
                    if (ClickInsideActiveTextBox(pos))
                    {
                        e.Handled = true;
                        break;
                    }
                    CommitActiveTextBox();
                    PlaceTextBox(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Highlight:
                case EditTool.Strikethrough:
                case EditTool.Underline:
                    ClearSelection();
                    _isDrawing = true;
                    _drawStart = pos;
                    var previewFill = _currentTool == EditTool.Highlight
                        ? _highlightColor
                        : Color.FromArgb(70, _lineAnnotColor.R, _lineAnnotColor.G, _lineAnnotColor.B);
                    var rect = new Rectangle
                    {
                        Fill = new SolidColorBrush(previewFill),
                        Width = 0, Height = 0
                    };
                    Canvas.SetLeft(rect, pos.X);
                    Canvas.SetTop(rect, pos.Y);
                    _activeCanvas.Children.Add(rect);
                    _activePreview = rect;
                    _activeCanvas.CaptureMouse();
                    break;

                case EditTool.Draw:
                    ClearSelection();
                    _isDrawing = true;
                    _activeInk = new InkAnnotation { PageIndex = pageIdx, StrokeWidth = _drawWidth };
                    _activeInk.SetColor(_drawColor);
                    _activeInk.Points.Add(pos);
                    var poly = new Polyline
                    {
                        Stroke = new SolidColorBrush(_drawColor),
                        StrokeThickness = _drawWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round
                    };
                    poly.Points.Add(pos);
                    _activeCanvas.Children.Add(poly);
                    _activePreview = poly;
                    _activeCanvas.CaptureMouse();
                    break;

                case EditTool.Signature:
                    if (_pendingSignature is not null)
                    {
                        PlaceSignature(pos, pageIdx);
                        e.Handled = true;
                    }
                    else
                    {
                        ShowSignaturePopup();
                    }
                    break;

                case EditTool.Image:
                    PlaceImageFromDialog(pos, pageIdx);
                    e.Handled = true;
                    break;

                case EditTool.Crop:
                    ClearSelection();
                    HideCropConfirmBar();
                    _isDrawing = true;
                    _drawStart = pos;
                    _cropPageIndex = _activeCanvas.Tag is int cpi ? cpi : (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex);
                    _cropPreviewRect = new Rectangle
                    {
                        Stroke          = Brushes.White,
                        StrokeThickness = 1.5,
                        StrokeDashArray = [5, 3],
                        Fill            = AccentBrush(55),
                        Width = 0, Height = 0,
                        IsHitTestVisible = false,
                        Effect = new System.Windows.Media.Effects.DropShadowEffect
                            { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 },
                    };
                    Canvas.SetLeft(_cropPreviewRect, pos.X);
                    Canvas.SetTop(_cropPreviewRect, pos.Y);
                    Panel.SetZIndex(_cropPreviewRect, 1); // handles sit at ZIndex 10
                    _activeCanvas.Children.Add(_cropPreviewRect);
                    _activePreview = _cropPreviewRect;
                    _activeCanvas.CaptureMouse();
                    break;
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            // Don't interfere with mouse interaction inside form field overlays.
            if (e.OriginalSource is DependencyObject moveSrc && IsFormFieldElement(moveSrc))
                return;

            // Resolve the pointer against the surface the gesture started on, not _activeCanvas,
            // which RenderAllAnnotations (and async grid tile streaming) can re-point mid-gesture.
            var gc = _gestureCanvas ?? _activeCanvas;
            var pos = e.GetPosition(gc);
            pos.X = Math.Max(0, Math.Min(gc.ActualWidth, pos.X));
            pos.Y = Math.Max(0, Math.Min(gc.ActualHeight, pos.Y));

            // Live text-edit box resize (mid-edit corner handles): free-form, opposite corner held fixed.
            if (_draggingTextEditHandle && _tehBox is not null)
            {
                double newW = Math.Max(40, Math.Abs(pos.X - _tehAnchor.X));
                double newH = Math.Max(24, Math.Abs(pos.Y - _tehAnchor.Y));
                double nx = (_tehCorner is "NW" or "SW") ? _tehAnchor.X - newW : _tehAnchor.X;
                double ny = (_tehCorner is "NW" or "NE") ? _tehAnchor.Y - newH : _tehAnchor.Y;
                Canvas.SetLeft(_tehBox, nx);
                Canvas.SetTop(_tehBox, ny);
                _tehBox.Width  = newW;
                _tehBox.Height = newH;
                LayoutTextEditHandles();
                return;
            }

            // Text box resize drag: width follows the dragged corner; height auto-fits the wrapped text.
            if (_isResizingSig && _resizeTextAnnot is not null)
            {
                var rta = _resizeTextAnnot;
                // Free-form: the dragged corner sets both width and height (the opposite corner is fixed),
                // exactly like resizing an image or crop rectangle. Text wraps to the width and is clipped
                // to the height. The corner is clamped to the page so it can't leave it.
                var cp = ClampPointToPage(rta.PageIndex, pos);
                double newW = Math.Max(40, Math.Abs(cp.X - _resizeAnchor.X));
                double newH = Math.Max(20, Math.Abs(cp.Y - _resizeAnchor.Y));
                double nx = (_resizeCorner is "NW" or "SW") ? _resizeAnchor.X - newW : _resizeAnchor.X;
                double ny = (_resizeCorner is "NW" or "NE") ? _resizeAnchor.Y - newH : _resizeAnchor.Y;
                rta.Width = newW;
                rta.Height = newH;
                rta.Position = new Point(nx, ny);
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                    Canvas.SetLeft(_selectionBorder, nx - 4);
                    Canvas.SetTop(_selectionBorder, ny - 4);
                }
                LayoutResizeHandles(nx, ny, newW, newH);
                RenderAllAnnotations(rta.PageIndex);
                _activeCanvas.Children.Add(_selectionBorder!);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
                return;
            }

            // Signature resize drag
            if (_isResizingSig && _resizeSigAnnot is not null)
            {
                // Uniform-scale resize from the dragged corner; the opposite corner (_resizeAnchor)
                // stays fixed. Aspect is preserved by taking whichever axis demands the larger scale.
                double desiredW = Math.Abs(pos.X - _resizeAnchor.X);
                double desiredH = Math.Abs(pos.Y - _resizeAnchor.Y);
                double sw = Math.Max(1.0, _resizeSigAnnot.SourceWidth);
                double sh = Math.Max(1.0, _resizeSigAnnot.SourceHeight);
                double newScale = Math.Max(0.05, Math.Max(desiredW / sw, desiredH / sh));
                _resizeSigAnnot.Scale = newScale;

                double newW = _resizeSigAnnot.SourceWidth * newScale;
                double newH = _resizeSigAnnot.SourceHeight * newScale;
                // Reposition the top-left so the anchor corner is preserved.
                double nx = (_resizeCorner is "NW" or "SW") ? _resizeAnchor.X - newW : _resizeAnchor.X;
                double ny = (_resizeCorner is "NW" or "NE") ? _resizeAnchor.Y - newH : _resizeAnchor.Y;
                _resizeSigAnnot.Position = new Point(nx, ny);

                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                    Canvas.SetLeft(_selectionBorder, nx - 4);
                    Canvas.SetTop(_selectionBorder, ny - 4);
                }
                LayoutResizeHandles(nx, ny, newW, newH);

                // Re-render annotations to show updated size
                RenderAllAnnotations(_resizeSigAnnot.PageIndex);
                // Restore selection visuals (RenderAllAnnotations clears canvas children including our overlays)
                _activeCanvas.Children.Add(_selectionBorder!);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
                return;
            }

            // Highlight / strikethrough / underline resize drag (modifies the Bounds rectangle).
            if (_isResizingSig && _resizeHlAnnot is not null)
            {
                var cp = ClampPointToPage(_resizeHlAnnot.PageIndex, pos);   // keep the dragged corner on-page
                double newW = Math.Max(4, Math.Abs(cp.X - _resizeAnchor.X));
                double newH = Math.Max(4, Math.Abs(cp.Y - _resizeAnchor.Y));
                double nx = (_resizeCorner is "NW" or "SW") ? _resizeAnchor.X - newW : _resizeAnchor.X;
                double ny = (_resizeCorner is "NW" or "NE") ? _resizeAnchor.Y - newH : _resizeAnchor.Y;
                _resizeHlAnnot.Bounds = new Rect(nx, ny, newW, newH);
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = newW + 8;
                    _selectionBorder.Height = newH + 8;
                    Canvas.SetLeft(_selectionBorder, nx - 4);
                    Canvas.SetTop(_selectionBorder, ny - 4);
                }
                LayoutResizeHandles(nx, ny, newW, newH);
                RenderAllAnnotations(_resizeHlAnnot.PageIndex);
                _activeCanvas.Children.Add(_selectionBorder!);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
                return;
            }

            // Ink resize drag: scale every stroke point about the fixed anchor corner.
            if (_isResizingSig && _resizeInkAnnot is not null && _resizeInkOrigPoints is not null)
            {
                var cp = ClampPointToPage(_resizeInkAnnot.PageIndex, pos);   // keep the dragged corner on-page
                double newW = Math.Max(4, Math.Abs(cp.X - _resizeAnchor.X));
                double newH = Math.Max(4, Math.Abs(cp.Y - _resizeAnchor.Y));
                double sx = newW / _resizeInkOrigBounds.Width;
                double sy = newH / _resizeInkOrigBounds.Height;
                for (int i = 0; i < _resizeInkOrigPoints.Count && i < _resizeInkAnnot.Points.Count; i++)
                {
                    var p = _resizeInkOrigPoints[i];
                    _resizeInkAnnot.Points[i] = new Point(
                        _resizeAnchor.X + (p.X - _resizeAnchor.X) * sx,
                        _resizeAnchor.Y + (p.Y - _resizeAnchor.Y) * sy);
                }
                var ib = AnnotBounds(_resizeInkAnnot);
                if (_selectionBorder is not null)
                {
                    _selectionBorder.Width  = ib.Width + 8;
                    _selectionBorder.Height = ib.Height + 8;
                    Canvas.SetLeft(_selectionBorder, ib.X - 4);
                    Canvas.SetTop(_selectionBorder, ib.Y - 4);
                }
                LayoutResizeHandles(ib.X, ib.Y, ib.Width, ib.Height);
                RenderAllAnnotations(_resizeInkAnnot.PageIndex);
                _activeCanvas.Children.Add(_selectionBorder!);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
                return;
            }

            // Annotation drag-to-move
            if (_isDraggingAnnot && _dragAnnot is not null)
            {
                double dx = pos.X - _dragAnnotStart.X;
                double dy = pos.Y - _dragAnnotStart.Y;
                AnnotSetPos(_dragAnnot, new Point(_dragAnnotOrigPos.X + dx, _dragAnnotOrigPos.Y + dy));
                AnnotSetPos(_dragAnnot, ClampAnnotPos(_dragAnnot));   // keep the whole annotation on-page
                var db = AnnotBounds(_dragAnnot);
                if (_selectionBorder is not null)
                {
                    Canvas.SetLeft(_selectionBorder, db.X - 4);
                    Canvas.SetTop(_selectionBorder, db.Y - 4);
                }
                if (_dragAnnot is PlacedAnnotation or TextAnnotation or HighlightAnnotation or InkAnnotation)
                    LayoutResizeHandles(db.X, db.Y, db.Width, db.Height);
                RenderAllAnnotations(_dragAnnot.PageIndex);
                _activeCanvas.Children.Add(_selectionBorder!);
                foreach (var hd in _resizeHandles) _activeCanvas.Children.Add(hd);
                return;
            }

            // Text selection drag
            if (_isSelecting && _selectRect is not null)
            {
                Canvas.SetLeft(_selectRect, Math.Min(pos.X, _selectStart.X));
                Canvas.SetTop(_selectRect, Math.Min(pos.Y, _selectStart.Y));
                _selectRect.Width = Math.Abs(pos.X - _selectStart.X);
                _selectRect.Height = Math.Abs(pos.Y - _selectStart.Y);
                return;
            }

            // Crop corner handle drag — must be before the _isDrawing guard since handle drag
            // runs with _isDrawing = false and _activePreview = null.
            if (_activeCropHandleTag is not null && _cropPreviewRect is not null)
            {
                double dx = pos.X - _cropHandleDragStart.X;
                double dy = pos.Y - _cropHandleDragStart.Y;
                var r = _cropRectAtHandleDrag;
                double newX = r.X, newY = r.Y, newW = r.Width, newH = r.Height;
                switch (_activeCropHandleTag)
                {
                    case "NW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = r.Right - newX;
                        newH = r.Bottom - newY;
                        break;
                    case "NE":
                        newY = Math.Min(r.Bottom - 10, r.Y + dy);
                        newW = Math.Max(10, r.Width + dx);
                        newH = r.Bottom - newY;
                        break;
                    case "SE":
                        newW = Math.Max(10, r.Width + dx);
                        newH = Math.Max(10, r.Height + dy);
                        break;
                    case "SW":
                        newX = Math.Min(r.Right - 10, r.X + dx);
                        newW = r.Right - newX;
                        newH = Math.Max(10, r.Height + dy);
                        break;
                }
                _cropCanvasRect = new Rect(newX, newY, newW, newH);
                UpdateCropRectVisuals();
                return;
            }

            if (!_isDrawing || _activePreview is null) return;

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle:
                case EditTool.Strikethrough when _activePreview is Rectangle:
                case EditTool.Underline when _activePreview is Rectangle:
                    var rect = (Rectangle)_activePreview;
                    Canvas.SetLeft(rect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(rect, Math.Min(pos.Y, _drawStart.Y));
                    rect.Width = Math.Abs(pos.X - _drawStart.X);
                    rect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;

                case EditTool.Draw when _activePreview is Polyline poly && _activeInk is not null:
                    _activeInk.Points.Add(pos);
                    poly.Points.Add(pos);
                    break;

                case EditTool.Crop when _activePreview is Rectangle crect:
                    Canvas.SetLeft(crect, Math.Min(pos.X, _drawStart.X));
                    Canvas.SetTop(crect, Math.Min(pos.Y, _drawStart.Y));
                    crect.Width = Math.Abs(pos.X - _drawStart.X);
                    crect.Height = Math.Abs(pos.Y - _drawStart.Y);
                    break;
            }
        }

        // Draggable annotations (placed image/signature and typewriter text) expose a top-left
        // Position; these helpers read/write it generically so one drag path serves both.
        private static bool IsDraggable(PageAnnotation a) => a is PlacedAnnotation or TextAnnotation or HighlightAnnotation or InkAnnotation;
        private static Point AnnotGetPos(PageAnnotation a) => a switch
        {
            PlacedAnnotation p => p.Position,
            TextAnnotation t   => t.Position,
            HighlightAnnotation h => h.Bounds.Location,
            // Ink has no single origin; use the stroke's bounding-box top-left.
            InkAnnotation ink when ink.Points.Count > 0
                => new Point(ink.Points.Min(p => p.X), ink.Points.Min(p => p.Y)),
            _                  => default
        };
        private static void AnnotSetPos(PageAnnotation a, Point pos)
        {
            switch (a)
            {
                case PlacedAnnotation p: p.Position = pos; break;
                case TextAnnotation t:   t.Position = pos; break;
                case HighlightAnnotation h: h.Bounds = new Rect(pos, h.Bounds.Size); break;
                case InkAnnotation ink when ink.Points.Count > 0:
                    // Move the whole stroke by the delta from its current bounding-box origin.
                    double ox = ink.Points.Min(p => p.X), oy = ink.Points.Min(p => p.Y);
                    double dx = pos.X - ox, dy = pos.Y - oy;
                    for (int i = 0; i < ink.Points.Count; i++)
                        ink.Points[i] = new Point(ink.Points[i].X + dx, ink.Points[i].Y + dy);
                    break;
            }
        }
        private Rect AnnotBounds(PageAnnotation a)
        {
            // Ink isn't a simple rect in HitTestAnnotation (it's a proximity test), so derive its bounds
            // from the stroke points; everything else reuses HitTestAnnotation's out-bounds via a far probe.
            if (a is InkAnnotation ia)
            {
                if (ia.Points.Count == 0) return Rect.Empty;
                double minX = ia.Points.Min(p => p.X), minY = ia.Points.Min(p => p.Y);
                double maxX = ia.Points.Max(p => p.X), maxY = ia.Points.Max(p => p.Y);
                return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
            }
            HitTestAnnotation(a, new Point(double.MinValue, double.MinValue), out Rect b);
            return b;
        }

        // Constrains a rectangle (annotation-canvas coordinates) to the page so its corners can't land
        // off the page, where resize handles become unreachable. The page occupies (0,0)-(w,h) in the
        // same coordinate space as annotations, taken from _renderDims. A box bigger than the page is
        // pinned to the top-left rather than shrunk.
        private Rect ClampRectToPage(int pageIdx, Rect r)
        {
            if (!_renderDims.TryGetValue(pageIdx, out var d)) return r;
            double pw = d.w, ph = d.h;
            double x = Math.Max(0, Math.Min(r.X, pw - r.Width));
            double y = Math.Max(0, Math.Min(r.Y, ph - r.Height));
            return new Rect(x, y, r.Width, r.Height);
        }

        // Clamps an annotation's position so its whole bounding box stays on its page. Returns the
        // top-left to feed back through AnnotSetPos (which knows how to move each annotation type).
        private Point ClampAnnotPos(PageAnnotation a)
        {
            var b = AnnotBounds(a);
            return ClampRectToPage(a.PageIndex, b).Location;
        }

        // Clamps a point to the page rectangle. Used during resize so a dragged corner can't leave the
        // page (with the opposite corner already on-page, that keeps the whole box on-page).
        private Point ClampPointToPage(int pageIdx, Point p)
            => _renderDims.TryGetValue(pageIdx, out var d)
                ? new Point(Math.Max(0, Math.Min(p.X, d.w)), Math.Max(0, Math.Min(p.Y, d.h)))
                : p;

        // Returns the page index + canvas under the mouse across every per-page overlay
        // (grid / two-page / continuous tiles) and the primary page canvas. Used to drop a placed
        // annotation onto a different page than the one it started on.
        private (int page, Canvas canvas)? PageCanvasUnderPointer(MouseEventArgs e)
        {
            foreach (var kv in _continuousCanvases)
            {
                var c = kv.Value;
                if (c.ActualWidth <= 0 || c.ActualHeight <= 0) continue;
                var p = e.GetPosition(c);
                if (p.X >= 0 && p.X <= c.ActualWidth && p.Y >= 0 && p.Y <= c.ActualHeight)
                    return (kv.Key, c);
            }
            if (_annotationCanvas.ActualWidth > 0 && _annotationCanvas.ActualHeight > 0)
            {
                var pp = e.GetPosition(_annotationCanvas);
                if (pp.X >= 0 && pp.X <= _annotationCanvas.ActualWidth &&
                    pp.Y >= 0 && pp.Y <= _annotationCanvas.ActualHeight)
                    return (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex, _annotationCanvas);
            }
            return null;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Don't process release events that originate inside the crop confirm bar.
            if (_cropConfirmBar is not null && e.OriginalSource is DependencyObject cropSrc &&
                IsDescendantOf(cropSrc, _cropConfirmBar))
                return;

            // The gesture may have re-pointed _activeCanvas via RenderAllAnnotations since mouse-down
            // (in Grid view, async tile streaming does this), so restore it to the surface the
            // gesture started on. This makes page resolution, capture release, and preview cleanup
            // below all act on the correct page - the root cause of highlights landing on the wrong
            // page and select/delete failing in Grid view.
            if (_gestureCanvas is not null) _activeCanvas = _gestureCanvas;

            // Use the page the gesture started on (continuous/grid have per-page canvases tagged
            // with their index), matching the mouse-down handler. Falls back to the selected page
            // for the single-page canvas. Prevents committing an annotation to the wrong page.
            int pageIdx = _gesturePage >= 0
                ? _gesturePage
                : (_activeCanvas?.Tag is int tagPage ? tagPage : PageList.SelectedIndex);

            // Finish a live text-edit box resize and hand focus back so typing continues.
            if (_draggingTextEditHandle)
            {
                _draggingTextEditHandle = false;
                _activeCanvas?.ReleaseMouseCapture();
                _tehBox?.Focus();
                e.Handled = true;
                return;
            }

            // Finish crop handle drag
            if (_activeCropHandleTag is not null)
            {
                _activeCropHandleTag = null;
                _activeCanvas?.ReleaseMouseCapture();
                return;
            }

            // Finish annotation drag-to-move
            if (_isDraggingAnnot)
            {
                _isDraggingAnnot = false;
                _activeCanvas?.ReleaseMouseCapture();
                if (_dragAnnot is not null)
                {
                    var da = _dragAnnot;
                    _dragAnnot = null;
                    int oldPage = da.PageIndex;
                    // Released over a different page? Move it there (position was updated live during drag).
                    var drop = PageCanvasUnderPointer(e);
                    if (drop is { } d && d.page != oldPage && _doc is not null
                        && d.page >= 0 && d.page < _doc.PageCount)
                    {
                        var pt = e.GetPosition(d.canvas);
                        AnnotSetPos(da, new Point(pt.X - (_dragAnnotStart.X - _dragAnnotOrigPos.X),
                                                  pt.Y - (_dragAnnotStart.Y - _dragAnnotOrigPos.Y)));
                        if (_annotations.TryGetValue(oldPage, out var oldList)) oldList.Remove(da);
                        da.PageIndex = d.page;
                        if (!_annotations.TryGetValue(d.page, out var newList)) { newList = []; _annotations[d.page] = newList; }
                        newList.Add(da);
                        ClearSelection();
                        RenderAllAnnotations(oldPage);
                        RenderAllAnnotations(d.page);
                        SelectAnnotation(da, AnnotBounds(da));
                        MarkDirty();
                        return;
                    }
                    RenderAllAnnotations(da.PageIndex);
                    SelectAnnotation(da, AnnotBounds(da));
                    MarkDirty();
                }
                return;
            }

            // Finish text box resize
            if (_isResizingSig && _resizeTextAnnot is not null)
            {
                _isResizingSig = false;
                _activeCanvas?.ReleaseMouseCapture();
                var rta = _resizeTextAnnot;
                _resizeTextAnnot = null;
                RenderAllAnnotations(rta.PageIndex);
                SelectAnnotation(rta, new Rect(rta.Position.X, rta.Position.Y, rta.Width, rta.Height));
                MarkDirty();
                return;
            }

            // Finish highlight / strikethrough / underline resize
            if (_isResizingSig && _resizeHlAnnot is not null)
            {
                _isResizingSig = false;
                _activeCanvas?.ReleaseMouseCapture();
                var rha = _resizeHlAnnot;
                _resizeHlAnnot = null;
                RenderAllAnnotations(rha.PageIndex);
                SelectAnnotation(rha, rha.Bounds);
                MarkDirty();
                return;
            }

            // Finish ink resize
            if (_isResizingSig && _resizeInkAnnot is not null)
            {
                _isResizingSig = false;
                _activeCanvas?.ReleaseMouseCapture();
                var rink = _resizeInkAnnot;
                _resizeInkAnnot = null;
                _resizeInkOrigPoints = null;
                RenderAllAnnotations(rink.PageIndex);
                SelectAnnotation(rink, AnnotBounds(rink));
                MarkDirty();
                return;
            }

            // Finish signature resize
            if (_isResizingSig)
            {
                _isResizingSig = false;
                _activeCanvas?.ReleaseMouseCapture();
                if (_resizeSigAnnot is not null)
                {
                    // Final re-render and re-select to reposition handle cleanly
                    var sa = _resizeSigAnnot;
                    _resizeSigAnnot = null;
                    RenderAllAnnotations(sa.PageIndex);
                    double newW = sa.SourceWidth * sa.Scale;
                    double newH = sa.SourceHeight * sa.Scale;
                    SelectAnnotation(sa, new Rect(sa.Position.X, sa.Position.Y, newW, newH));
                    MarkDirty();
                }
                return;
            }

            // Handle text selection release
            if (_isSelecting)
            {
                _isSelecting = false;
                _activeCanvas?.ReleaseMouseCapture();
                var pos = e.GetPosition(_activeCanvas);
                double dragW = Math.Abs(pos.X - _selectStart.X);
                double dragH = Math.Abs(pos.Y - _selectStart.Y);

                if (dragW < 5 && dragH < 5)
                {
                    // Tiny drag = single click -> try annotation selection
                    ClearTextSelection();
                    bool shiftSel = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                    if (pageIdx >= 0 && _annotations.ContainsKey(pageIdx))
                    {
                        for (int i = _annotations[pageIdx].Count - 1; i >= 0; i--)
                        {
                            if (HitTestAnnotation(_annotations[pageIdx][i], _selectStart, out Rect bounds))
                            {
                                if (shiftSel)
                                    // Add/remove this annotation from the multi-selection.
                                    ToggleMultiSelect(_annotations[pageIdx][i], bounds,
                                                      _gestureCanvas ?? CanvasForPage(pageIdx));
                                else
                                    SelectAnnotation(_annotations[pageIdx][i], bounds);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    var selectBounds = new Rect(
                        Math.Min(pos.X, _selectStart.X), Math.Min(pos.Y, _selectStart.Y),
                        dragW, dragH);
                    // Box-select: if any annotations fall inside the rectangle, multi-select them all (so
                    // stacked annotations become individually visible and editable). Only when none are
                    // caught do we fall back to selecting page text in the region (copy/extract).
                    var hits = new List<(PageAnnotation a, Rect b)>();
                    if (pageIdx >= 0 && _annotations.TryGetValue(pageIdx, out var apg))
                        foreach (var a in apg)
                        {
                            if (!IsDraggable(a)) continue;
                            var ab = AnnotBounds(a);
                            if (!ab.IsEmpty && selectBounds.IntersectsWith(ab)) hits.Add((a, ab));
                        }
                    if (hits.Count > 0)
                    {
                        ClearSelection();
                        var cv = _gestureCanvas ?? CanvasForPage(pageIdx);
                        foreach (var (a, b) in hits) ToggleMultiSelect(a, b, cv);
                        SetStatus($"Selected {hits.Count} annotation{(hits.Count == 1 ? "" : "s")}");
                    }
                    else
                    {
                        ExtractTextFromRegion(pageIdx, selectBounds);
                    }
                }
                return;
            }

            if (!_isDrawing) return;
            _isDrawing = false;
            _activeCanvas?.ReleaseMouseCapture();

            switch (_currentTool)
            {
                case EditTool.Highlight when _activePreview is Rectangle:
                case EditTool.Strikethrough when _activePreview is Rectangle:
                case EditTool.Underline when _activePreview is Rectangle:
                    {
                        var rect = (Rectangle)_activePreview;
                        if (rect.Width > 3 && rect.Height > 3)
                        {
                            var ha = new HighlightAnnotation
                            {
                                PageIndex = pageIdx,
                                Bounds = new Rect(Canvas.GetLeft(rect), Canvas.GetTop(rect), rect.Width, rect.Height),
                                Style = _currentTool == EditTool.Strikethrough ? HighlightStyle.Strikethrough
                                      : _currentTool == EditTool.Underline   ? HighlightStyle.Underline
                                      : HighlightStyle.Fill
                            };
                            ha.SetColor(_currentTool == EditTool.Highlight ? _highlightColor : _lineAnnotColor);
                            AddAnnotation(ha);
                            _activeCanvas?.Children.Remove(rect);
                            RenderAllAnnotations(pageIdx);
                        }
                        else
                        {
                            _activeCanvas?.Children.Remove(rect);
                        }
                    }
                    break;

                case EditTool.Draw when _activeInk is not null:
                    if (_activeInk.Points.Count > 2)
                    {
                        AddAnnotation(_activeInk);
                    }
                    else
                    {
                        _activeCanvas?.Children.Remove(_activePreview);
                    }
                    _activeInk = null;
                    break;

                case EditTool.Crop when _activePreview is Rectangle cr:
                    _activeCanvas?.ReleaseMouseCapture(); // MUST release before showing handles
                    if (cr.Width > 10 && cr.Height > 10)
                    {
                        _cropCanvasRect = new Rect(Canvas.GetLeft(cr), Canvas.GetTop(cr), cr.Width, cr.Height);
                        _activePreview = null;
                        if (_cropPreviewRect is not null)
                            Panel.SetZIndex(_cropPreviewRect, 1); // below handles (ZIndex 10)
                        ShowCropConfirmBar();
                        return;
                    }
                    else
                    {
                        _activeCanvas?.Children.Remove(cr);
                        _cropPreviewRect = null;
                    }
                    break;
            }
            _activePreview = null;
        }

        // ============================================================
        // Selection
        // ============================================================

        private bool HitTestAnnotation(PageAnnotation annot, Point pos, out Rect bounds)
        {
            switch (annot)
            {
                case CoverAnnotation cov:
                    // A text cover gets a forgiving grab margin (and sits below its text, which is checked
                    // first), so you can click the colored background to select/move it without hunting.
                    bounds = cov.Bounds;
                    var coverHit = bounds; coverHit.Inflate(6, 6);
                    return coverHit.Contains(pos);

                case HighlightAnnotation ha:
                    bounds = ha.Bounds;
                    return bounds.Contains(pos);

                case TextAnnotation ta:
                    bounds = new Rect(ta.Position.X, ta.Position.Y,
                                      Math.Max(8, ta.Width), Math.Max(8, ta.Height));
                    // Forgiving grab area: clicking anywhere in the box - plus a small margin around it,
                    // since a one-line box is a thin band - selects and drags the text, so you don't have
                    // to land on a glyph. The selection loop still checks topmost-first, so any annotation
                    // stacked above the text takes the click before this does (no conflict).
                    var textHit = bounds; textHit.Inflate(8, 10);
                    return textHit.Contains(pos);

                case InkAnnotation ia when ia.Points.Count > 0:
                    bool near = ia.Points.Any(p =>
                        Math.Sqrt((p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y)) < 15);
                    if (near)
                    {
                        double minX = ia.Points.Min(p => p.X);
                        double minY = ia.Points.Min(p => p.Y);
                        double maxX = ia.Points.Max(p => p.X);
                        double maxY = ia.Points.Max(p => p.Y);
                        bounds = new Rect(minX, minY, Math.Max(maxX - minX, 4), Math.Max(maxY - minY, 4));
                        return true;
                    }
                    bounds = Rect.Empty;
                    return false;

                case SignatureAnnotation sa:
                    double sigW = sa.SourceWidth * sa.Scale;
                    double sigH = sa.SourceHeight * sa.Scale;
                    bounds = new Rect(sa.Position.X, sa.Position.Y, sigW, sigH);
                    return bounds.Contains(pos);

                case ImageAnnotation ia:
                    double iaW = ia.SourceWidth * ia.Scale;
                    double iaH = ia.SourceHeight * ia.Scale;
                    bounds = new Rect(ia.Position.X, ia.Position.Y, iaW, iaH);
                    return bounds.Contains(pos);

                default:
                    bounds = Rect.Empty;
                    return false;
            }
        }

        // Resolve the active theme's "SelectionAccent" color: a per-theme color picked to stay
        // readable on the white PDF page (Accent is white in several themes, and AccentBorder is a
        // pale cream that washes out on white). Falls back to brand green.
        private Color AccentColor()
            => TryFindResource("SelectionAccent") is SolidColorBrush b ? b.Color : Color.FromRgb(30, 165, 76);
        private SolidColorBrush AccentBrush(byte alpha = 255)
        {
            var c = AccentColor();
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
        // A darker shade of the accent, used for a cover's selection chrome and its in-edit outline so a
        // cover reads as distinct from the lighter accent on the text box stacked over it.
        private SolidColorBrush DarkerAccentBrush(byte alpha = 255)
        {
            var c = AccentColor();
            return new SolidColorBrush(Color.FromArgb(alpha, (byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6)));
        }

        // Recolor any live selection / crop visuals to the current theme's SelectionAccent.
        // Their brushes are plain (not resource references), so a theme swap won't update them
        // until reselected unless we repaint them here.
        private void RefreshSelectionAccent()
        {
            if (_selectionBorder is not null)
            {
                _selectionBorder.BorderBrush = AccentBrush();
                _selectionBorder.Background  = AccentBrush(40);
            }
            foreach (var hd in _resizeHandles)
                hd.Fill = AccentBrush();
            if (_cropPreviewRect is not null)
                _cropPreviewRect.Fill = AccentBrush(55);
        }

        // Positions the four corner handles around an annotation's bounds (top-left x,y and size w,h).
        private void LayoutResizeHandles(double x, double y, double w, double h)
        {
            foreach (var hd in _resizeHandles)
            {
                double hs = hd.Width;
                (double cx, double cy) = (hd.Tag as string) switch
                {
                    "NW" => (x,     y),
                    "NE" => (x + w, y),
                    "SW" => (x,     y + h),
                    _    => (x + w, y + h)   // SE
                };
                Canvas.SetLeft(hd, cx - hs / 2);
                Canvas.SetTop(hd, cy - hs / 2);
            }
        }

        private void SelectAnnotation(PageAnnotation annot, Rect bounds)
        {
            _selectedAnnotation = annot;
            // Recovery: if an annotation's corners drifted off-page (placed before the on-page guard),
            // pull it back on selection so its handles become reachable again. Only acts when it's
            // actually off-page, and re-derives bounds for the selection visuals below.
            if (IsDraggable(annot))
            {
                var onPage = ClampAnnotPos(annot);
                if (onPage != AnnotGetPos(annot))
                {
                    AnnotSetPos(annot, onPage);
                    RenderAllAnnotations(annot.PageIndex);
                    bounds = AnnotBounds(annot);
                    MarkDirty();
                }
            }
            // Continuous-view overlays are scaled down by their LayoutTransform, which would
            // shrink the selection outline and resize handle to near-invisibility. Compensate
            // so they render at the same on-screen size as single-page view.
            double inv = 1.0;
            if (_activeCanvas.LayoutTransform is ScaleTransform _selScale && _selScale.ScaleX > 0.0001)
                inv = 1.0 / _selScale.ScaleX;
            // A cover gets a darker accent so its handles/border stand apart from the text box over it.
            bool isCover = annot is CoverAnnotation;
            var selBrush = isCover ? DarkerAccentBrush() : AccentBrush();
            _selectionBorder = new Border
            {
                BorderBrush = selBrush,
                BorderThickness = new Thickness(2 * inv),
                Background = isCover ? DarkerAccentBrush(40) : AccentBrush(40),
                Width = bounds.Width + 8,
                Height = bounds.Height + 8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_selectionBorder, bounds.X - 4);
            Canvas.SetTop(_selectionBorder, bounds.Y - 4);
            _activeCanvas.Children.Add(_selectionBorder);

            // Selecting EITHER paired box dash-outlines its partner so both boxes stay visible: clicking
            // the cover keeps the text box shown, and vice versa. Cleared in ClearSelection.
            PageAnnotation? partner = null;
            if (annot.PairId.Length > 0 && _annotations.TryGetValue(annot.PageIndex, out var ppl))
                partner = annot is CoverAnnotation
                    ? ppl.OfType<TextAnnotation>().FirstOrDefault(t => t.PairId == annot.PairId)
                    : ppl.OfType<CoverAnnotation>().FirstOrDefault(c => c.PairId == annot.PairId);
            if (partner is not null)
            {
                var pb = AnnotBounds(partner);
                _pairedCoverOutline = new Rectangle
                {
                    Width = pb.Width + 4, Height = pb.Height + 4,
                    Stroke = DarkerAccentBrush(), StrokeThickness = 1.5 * inv,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Fill = Brushes.Transparent, IsHitTestVisible = false
                };
                Canvas.SetLeft(_pairedCoverOutline, pb.X - 2);
                Canvas.SetTop(_pairedCoverOutline, pb.Y - 2);
                _activeCanvas.Children.Add(_pairedCoverOutline);
            }

            // Add four corner resize handles for resizable annotations (signature, image, text box,
            // highlight/strikethrough/underline, and ink).
            if (annot is PlacedAnnotation or TextAnnotation or HighlightAnnotation or InkAnnotation)
            {
                double hSize = 14 * inv;
                _resizeHandles.Clear();
                foreach (string tag in new[] { "NW", "NE", "SE", "SW" })
                {
                    var hd = new Rectangle
                    {
                        Width = hSize, Height = hSize,
                        Fill = selBrush,
                        Stroke = Brushes.White, StrokeThickness = 1 * inv,
                        Cursor = (tag is "NW" or "SE") ? Cursors.SizeNWSE : Cursors.SizeNESW,
                        IsHitTestVisible = true,
                        Tag = tag
                    };
                    _resizeHandles.Add(hd);
                    _activeCanvas.Children.Add(hd);
                }
                LayoutResizeHandles(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                string label = annot switch
                {
                    SignatureAnnotation => "Signature",
                    ImageAnnotation     => "Image",
                    TextAnnotation      => "Text box",
                    CoverAnnotation     => "Text cover",
                    HighlightAnnotation { Style: HighlightStyle.Strikethrough } => "Strikethrough",
                    HighlightAnnotation { Style: HighlightStyle.Underline }     => "Underline",
                    HighlightAnnotation => "Highlight",
                    InkAnnotation       => "Drawing",
                    _                   => "Item"
                };
                string how = annot is TextAnnotation
                    ? "drag a side corner to set width, double-click to edit, Delete to remove"
                    : "drag any corner to resize, Delete to remove";
                SetStatus($"{label} selected - {how}");
            }
            else
            {
                SetStatus($"Selected {annot.GetType().Name.Replace("Annotation", "").ToLower()} annotation - press Delete to remove");
            }

            // Selecting a text box opens the text bar (synced to that box) so its color, fill and size
            // can be changed without re-typing. The bar's swatches/sliders then apply to the selection.
            if (annot is TextAnnotation tsel)
            {
                var col = tsel.GetColor();
                _textColor = col;
                _textOpacity = col.A;
                _textFillColor = tsel.GetFill();
                double sy = 1.0;
                if (_doc is not null && _renderDims.TryGetValue(tsel.PageIndex, out var rd) && rd.h > 0)
                    sy = _doc.Pages[tsel.PageIndex].Height.Point / rd.h;
                _textFontSize = Math.Max(1, Math.Round(tsel.FontSize * sy));
                ShowTextSettings();
            }
            // Selecting a highlight / strikethrough / underline opens the draw bar synced to it, so its
            // color and opacity can be edited in place. The annotation's style picks the matching tool.
            else if (annot is HighlightAnnotation hsel)
            {
                if (hsel.Style == HighlightStyle.Fill) _highlightColor = hsel.GetColor();
                else                                   _lineAnnotColor = hsel.GetColor();
                ShowDrawSettings(hsel.Style switch
                {
                    HighlightStyle.Strikethrough => EditTool.Strikethrough,
                    HighlightStyle.Underline     => EditTool.Underline,
                    _                            => EditTool.Highlight
                });
            }
            // Selecting a freehand stroke opens the draw bar synced to it (color, opacity and width).
            else if (annot is InkAnnotation isel)
            {
                _drawColor   = isel.GetColor();
                _drawOpacity = isel.GetColor().A;
                _drawWidth   = isel.StrokeWidth;
                ShowDrawSettings(EditTool.Draw);
            }
        }

        private static bool IsDescendantOf(DependencyObject child, DependencyObject parent)
        {
            var current = child;
            while (current != null)
            {
                if (current == parent) return true;
                current = GetAnyParent(current);
            }
            return false;
        }

        // VisualTreeHelper.GetParent throws on non-Visuals (e.g. a Run / Hyperlink inside a TextBlock,
        // which is what the footer and About text are made of). Walk the logical tree for those and the
        // visual tree otherwise, so a click on inline text never crashes the outside-click dismissers.
        private static DependencyObject? GetAnyParent(DependencyObject d)
        {
            if (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(d);
            return LogicalTreeHelper.GetParent(d);
        }

        /// <summary>
        /// Returns true if <paramref name="element"/> is inside a form field overlay control
        /// (tagged with <see cref="FormOverlayTag"/>). Used to let WPF handle mouse events
        /// for TextBox, CheckBox, RadioButton, and ComboBox controls natively.
        /// </summary>
        private static bool IsFormFieldElement(DependencyObject element)
        {
            var current = element;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.Tag as string == FormOverlayTag)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void ClearSelection()
        {
            if (_selectionBorder is not null)
            {
                (_selectionBorder.Parent as Canvas)?.Children.Remove(_selectionBorder);
                _selectionBorder = null;
            }
            foreach (var hd in _resizeHandles)
                (hd.Parent as Canvas)?.Children.Remove(hd);
            _resizeHandles.Clear();
            if (_pairedCoverOutline is not null)
            {
                (_pairedCoverOutline.Parent as Canvas)?.Children.Remove(_pairedCoverOutline);
                _pairedCoverOutline = null;
            }
            ClearMultiSelection();
            _isResizingSig = false;
            _resizeSigAnnot = null;
            _resizeTextAnnot = null;
            _resizeHlAnnot = null;
            _resizeInkAnnot = null;
            _resizeInkOrigPoints = null;
            _isDraggingAnnot = false;
            _dragAnnot = null;
            _selectedAnnotation = null;
            // If the text bar was opened for a now-cleared text-box selection (not because the Text tool
            // is active), close it again.
            if (_currentTool != EditTool.Text && _annotBarTool == EditTool.Text)
                HideTextSettings();
            // Likewise the draw bar: if it was opened to edit a selected highlight / line / ink
            // annotation (the active tool isn't a draw-family tool), close it when the selection clears.
            if (_currentTool is not (EditTool.Draw or EditTool.Highlight or EditTool.Strikethrough or EditTool.Underline)
                && _annotBarTool is EditTool.Draw or EditTool.Highlight or EditTool.Strikethrough or EditTool.Underline)
                HideDrawSettings();
        }

        // ---- Shift+click multi-selection (Select tool) -------------------------------------------

        /// <summary>Removes every shift-selection outline and empties the multi-selection set.</summary>
        private void ClearMultiSelection()
        {
            foreach (var o in _selectionOutlines)
                (o.Parent as Canvas)?.Children.Remove(o);
            _selectionOutlines.Clear();
            _selectedSet.Clear();
        }

        /// <summary>The overlay canvas that hosts a given page's annotations.</summary>
        private Canvas CanvasForPage(int pageIndex)
            => _continuousCanvases.TryGetValue(pageIndex, out var c) ? c : _annotationCanvas;

        /// <summary>Total annotations currently selected (primary + shift-selected set).</summary>
        private int SelectionCount()
            => _selectedSet.Count
             + (_selectedAnnotation is not null && !_selectedSet.Contains(_selectedAnnotation) ? 1 : 0);

        /// <summary>Draws a selection outline for a shift-selected annotation on its page canvas.</summary>
        private void AddSelectionOutline(Rect bounds, Canvas canvas)
        {
            // Match SelectAnnotation: counter a continuous/grid overlay's LayoutTransform so the
            // outline keeps a constant on-screen thickness regardless of the tile scale.
            double inv = 1.0;
            if (canvas.LayoutTransform is ScaleTransform st && st.ScaleX > 0.0001)
                inv = 1.0 / st.ScaleX;
            var outline = new Border
            {
                BorderBrush     = AccentBrush(),
                BorderThickness = new Thickness(2 * inv),
                Background      = AccentBrush(40),
                Width           = bounds.Width + 8,
                Height          = bounds.Height + 8,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(outline, bounds.X - 4);
            Canvas.SetTop(outline, bounds.Y - 4);
            canvas.Children.Add(outline);
            _selectionOutlines.Add(outline);
        }

        /// <summary>
        /// Shift+click handler: toggle an annotation in or out of the multi-selection. The first
        /// shift+click folds any existing single selection into the set so nothing is lost.
        /// </summary>
        private void ToggleMultiSelect(PageAnnotation annot, Rect bounds, Canvas canvas)
        {
            // Fold an existing single (primary) selection into the set so it stays selected.
            if (_selectedAnnotation is not null && !_selectedSet.Contains(_selectedAnnotation))
            {
                var prim = _selectedAnnotation;
                // Drop the single-selection chrome (border + resize handles) but keep the annotation.
                if (_selectionBorder is not null)
                {
                    (_selectionBorder.Parent as Canvas)?.Children.Remove(_selectionBorder);
                    _selectionBorder = null;
                }
                foreach (var hd in _resizeHandles)
                    (hd.Parent as Canvas)?.Children.Remove(hd);
                _resizeHandles.Clear();
                _selectedAnnotation = null;
                _selectedSet.Add(prim);
                AddSelectionOutline(AnnotBounds(prim), CanvasForPage(prim.PageIndex));
            }

            if (_selectedSet.Remove(annot))
            {
                // Was selected -> rebuild the outlines without it.
                foreach (var o in _selectionOutlines)
                    (o.Parent as Canvas)?.Children.Remove(o);
                _selectionOutlines.Clear();
                foreach (var a in _selectedSet)
                    AddSelectionOutline(AnnotBounds(a), CanvasForPage(a.PageIndex));
            }
            else
            {
                _selectedSet.Add(annot);
                AddSelectionOutline(bounds, canvas);
            }

            int n = SelectionCount();
            SetStatus(n == 0 ? "Selection cleared"
                             : $"{n} annotations selected - press Delete to remove");
        }

        private void DeleteSelected()
        {
            // Gather the primary selection plus any shift-selected annotations, de-duplicated.
            var toDelete = new List<PageAnnotation>();
            if (_selectedAnnotation is not null) toDelete.Add(_selectedAnnotation);
            foreach (var a in _selectedSet)
                if (!toDelete.Contains(a)) toDelete.Add(a);
            if (toDelete.Count == 0) return;

            var pages = new HashSet<int>();
            foreach (var a in toDelete)
                if (_annotations.TryGetValue(a.PageIndex, out var list) && list.Remove(a))
                    pages.Add(a.PageIndex);

            // If a paired replacement text was deleted, unpair its cover so it stops rendering dashed and
            // becomes a plain solid box (the original "two fields" hint is gone, just the cover remains).
            foreach (var a in toDelete)
                if (a is TextAnnotation t && t.PairId.Length > 0 && _annotations.TryGetValue(t.PageIndex, out var pl))
                    foreach (var cov in pl.OfType<CoverAnnotation>())
                        if (cov.PairId == t.PairId) cov.PairId = "";

            ClearSelection();
            foreach (var p in pages) RenderAllAnnotations(p);
            SetStatus(toDelete.Count == 1
                ? "Deleted selected annotation"
                : $"Deleted {toDelete.Count} annotations");
        }

        // Ctrl+A: multi-select every annotation on the pages currently on screen (single selected page,
        // or all continuous/grid tiles), so the user can see where everything is and grab stacked items.
        // Returns false when there are no annotations to select (caller falls back to text select).
        private bool SelectAllAnnotations()
        {
            List<int> pages = _continuousCanvases.Count > 0
                ? [.. _continuousCanvases.Keys]
                : [PageList.SelectedIndex];
            ClearSelection();
            int n = 0;
            foreach (int p in pages)
            {
                if (p < 0 || !_annotations.TryGetValue(p, out var list)) continue;
                var cv = CanvasForPage(p);
                foreach (var a in list)
                {
                    if (!IsDraggable(a)) continue;
                    var b = AnnotBounds(a);
                    if (b.IsEmpty) continue;
                    ToggleMultiSelect(a, b, cv);
                    n++;
                }
            }
            if (n > 0) SetStatus($"Selected {n} annotation{(n == 1 ? "" : "s")}");
            return n > 0;
        }

        private void SelectAllText()
        {
            if (_currentFile is null) return;
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);
                _selectedText = WordsToText(page.GetWords());
                if (string.IsNullOrWhiteSpace(_selectedText))
                {
                    SetStatus("No text found on this page");
                    return;
                }
                Clipboard.SetText(_selectedText);
                // Visual feedback: highlight entire canvas
                ClearTextSelection();
                _selectRect = new Rectangle
                {
                    Fill = new SolidColorBrush(Color.FromArgb(30, 74, 130, 255)),
                    Stroke = new SolidColorBrush(Color.FromArgb(80, 74, 130, 255)),
                    StrokeThickness = 1,
                    Width = _annotationCanvas.Width,
                    Height = _annotationCanvas.Height,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_selectRect, 0);
                Canvas.SetTop(_selectRect, 0);
                _annotationCanvas.Children.Add(_selectRect);
                SetStatus($"Selected all text - copied to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Select all error: {ex.Message}");
            }
        }

        private void CopySelectedText()
        {
            if (!string.IsNullOrEmpty(_selectedText))
            {
                Clipboard.SetText(_selectedText);
                SetStatus($"Copied to clipboard");
            }
            else
            {
                SetStatus("No text selected - drag to select text");
            }
        }

        private void ClearTextSelection()
        {
            if (_selectRect is not null)
            {
                _activeCanvas.Children.Remove(_selectRect);
                _selectRect = null;
            }
            _selectedText = null;
        }

        private void ExtractTextFromRegion(int pageIdx, Rect canvasBounds)
        {
            if (_currentFile is null || pageIdx < 0) return;
            if (!_renderDims.ContainsKey(pageIdx)) return;

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1); // PdfPig is 1-based

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = pdfW / renderW;
                double sy = pdfH / renderH;

                // Convert canvas rect to PDF coordinates (flip Y - PDF origin is bottom-left)
                double pdfLeft = canvasBounds.Left * sx;
                double pdfRight = canvasBounds.Right * sx;
                double pdfTop = pdfH - (canvasBounds.Top * sy);
                double pdfBottom = pdfH - (canvasBounds.Bottom * sy);
                // pdfTop > pdfBottom because of Y flip
                double pdfMinY = Math.Min(pdfTop, pdfBottom);
                double pdfMaxY = Math.Max(pdfTop, pdfBottom);

                var words = page.GetWords()
                    .Where(w =>
                    {
                        var bb = w.BoundingBox;
                        double cx = (bb.Left + bb.Right) / 2;
                        double cy = (bb.Bottom + bb.Top) / 2;
                        return cx >= pdfLeft && cx <= pdfRight && cy >= pdfMinY && cy <= pdfMaxY;
                    })
                    .ToList();

                if (words.Count == 0)
                {
                    SetStatus("No text found in selection");
                    ClearTextSelection();
                    return;
                }

                _selectedText = WordsToText(words);

                Clipboard.SetText(_selectedText);
                int wordCount = words.Count;
                SetStatus($"Copied {wordCount} word(s) to clipboard");
            }
            catch (Exception ex)
            {
                SetStatus($"Text extraction error: {ex.Message}");
                ClearTextSelection();
            }
        }

        // ============================================================
        // Search (Ctrl+F)
        // ============================================================

        /// <summary>
        /// Converts a collection of PdfPig words to a properly ordered string.
        /// Sorts top-to-bottom then left-to-right, groups into lines using a
        /// dynamic threshold (~40% of average word height) so words at slightly
        /// different baselines still land on the correct line.
        /// </summary>
        private static string WordsToText(IEnumerable<UglyToad.PdfPig.Content.Word> source)
        {
            var words = source
                .OrderByDescending(w => w.BoundingBox.Top)
                .ThenBy(w => w.BoundingBox.Left)
                .ToList();
            if (words.Count == 0) return string.Empty;

            // Dynamic threshold: 40% of average word height, minimum 4 PDF units
            double avgH   = words.Average(w => w.BoundingBox.Height);
            double thresh = Math.Max(4.0, avgH * 0.4);

            var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
            double lineY = double.MaxValue;
            foreach (var w in words)
            {
                if (Math.Abs(w.BoundingBox.Top - lineY) > thresh)
                {
                    lines.Add([]);
                    lineY = w.BoundingBox.Top;
                }
                lines[^1].Add(w);
            }

            // Re-sort each line by X in case the top-Y sort caused any grouping
            // to pull words into the wrong order within a line.
            return string.Join("\n", lines.Select(l =>
                string.Join(" ", l.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
        }

        private void ToggleSearchBar()
        {
            if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                return;
            }
            ShowSearchBar();
        }

        private void ShowSearchBar()
        {
            if (_searchBar is null)
            {
                // Build search bar programmatically and inject into the preview area grid
                _searchBox = new TextBox
                {
                    Width = 200,
                    Height = 26,
                    FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                    FontSize = 13,
                    Background = (SolidColorBrush)FindResource("BgCanvas"),
                    Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                    CaretBrush = (SolidColorBrush)FindResource("TextPrimary"),
                    BorderBrush = (SolidColorBrush)FindResource("AccentBorder"), SelectionBrush = AccentBrush(),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                _searchBox.KeyDown += SearchBox_KeyDown;
                _searchBox.TextChanged += SearchBox_TextChanged;

                // Custom template so the default WPF blue focus/hover border never shows; keep our themed border.
                var tbTemplate = new ControlTemplate(typeof(TextBox));
                var tbBorder = new FrameworkElementFactory(typeof(Border));
                tbBorder.SetValue(Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(Control.BackgroundProperty));
                tbBorder.SetValue(Border.BorderBrushProperty, new System.Windows.TemplateBindingExtension(Control.BorderBrushProperty));
                tbBorder.SetValue(Border.BorderThicknessProperty, new System.Windows.TemplateBindingExtension(Control.BorderThicknessProperty));
                tbBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
                var tbHost = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
                tbHost.SetValue(ScrollViewer.PaddingProperty, new System.Windows.TemplateBindingExtension(Control.PaddingProperty));
                tbHost.SetValue(ScrollViewer.VerticalAlignmentProperty, VerticalAlignment.Center);
                tbBorder.AppendChild(tbHost);
                tbTemplate.VisualTree = tbBorder;
                _searchBox.Template = tbTemplate;
                _searchBox.FocusVisualStyle = null;

                // Fixed width + centered so the result count never resizes the bar.
                _searchStatus = new TextBlock
                {
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    Width = 84,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(6, 0, 4, 0)
                };

                // Small VSCode-style prev / next / close buttons. Hover tooltips carry the shortcuts.
                Button SearchNavBtn(string glyph, string tip, Action onClick)
                {
                    var b = new Button
                    {
                        Content    = glyph,
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize   = 12,
                        Width = 26, Height = 24,
                        Padding    = new Thickness(0),   // ToolbarButton's 10,6 padding clips the glyph in a 26px button
                        Style      = (Style)FindResource("ToolbarButton"),
                        ToolTip    = tip
                    };
                    b.Click += (_, _) => onClick();
                    return b;
                }
                var prevBtn  = SearchNavBtn("\ue70e", "Previous Match (Shift+Enter)", SearchPrevResult); // ChevronUp
                var nextBtn  = SearchNavBtn("\ue70d", "Next Match (Enter)", SearchNextResult);            // ChevronDown
                var closeBtn = SearchNavBtn("\ue711", "Close (Esc)", CloseSearchBar);                     // Cancel

                var searchIcon = new TextBlock
                {
                    Text = "",  // Segoe MDL2 Search / magnifying glass
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 12,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    IsHitTestVisible = false
                };

                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 6, 8, 6)
                };
                panel.Children.Add(searchIcon);
                panel.Children.Add(_searchBox);
                panel.Children.Add(_searchStatus);
                panel.Children.Add(prevBtn);
                panel.Children.Add(nextBtn);
                panel.Children.Add(closeBtn);

                _searchBar = new Border
                {
                    Background = (SolidColorBrush)FindResource("BgPanel"),
                    BorderBrush = (SolidColorBrush)FindResource("AccentBorder"),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    CornerRadius = new CornerRadius(0, 0, 4, 4),
                    Padding = new Thickness(4),
                    Child = GrainWrap(panel),
                    Margin = new Thickness(0, 0, 16, 0),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.55 }
                };

                // Add to the preview area grid (parent of ScrollViewer)
                var previewGrid = PagePreviewPanel.Parent as Grid;
                if (previewGrid is not null)
                {
                    Panel.SetZIndex(_searchBar, 100);
                    previewGrid.Children.Add(_searchBar);
                }
            }

            _searchBar.Visibility = Visibility.Visible;
            _searchBox!.Text = "";
            if (_searchStatus != null) _searchStatus.Text = "";
            _searchBox.Focus();
            Keyboard.Focus(_searchBox);
        }

        private void CloseSearchBar()
        {
            if (_searchBar is not null)
                _searchBar.Visibility = Visibility.Collapsed;
            ClearSearchHighlights();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                    SearchPrevResult();
                else
                    SearchNextResult();
                e.Handled = true;
            }
        }

        private System.Windows.Threading.DispatcherTimer? _searchDebounce;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = _searchBox?.Text ?? "";
            if (text.Length < 2)
            {
                _searchDebounce?.Stop();
                ClearSearchHighlights();
                _allSearchRects.Clear();
                _searchResultPages.Clear();
                _searchPageCursor = -1;
                return;
            }
            // Debounce: wait for a brief pause in typing before searching, so the first keystrokes
            // on a large document don't lock the UI while it searches partial queries.
            if (_searchDebounce is null)
            {
                _searchDebounce = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(250) };
                _searchDebounce.Tick += (_, _) =>
                {
                    _searchDebounce!.Stop();
                    var q = _searchBox?.Text ?? "";
                    if (q.Length >= 2) RunSearch(q);
                };
            }
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        private readonly SearchService _searchService = new();

        private void RunSearch(string query)
        {
            ClearSearchHighlights();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;

            if (string.IsNullOrWhiteSpace(query) || _currentFile is null)
            {
                if (_searchStatus != null) _searchStatus.Text = "";
                return;
            }

            try
            {
                var sr = _searchService.Search(_currentFile, query);

                foreach (var kvp in sr.PageRects)
                    _allSearchRects[kvp.Key] = kvp.Value;
                _searchResultPages.AddRange(sr.ResultPages);

                if (_searchResultPages.Count == 0)
                {
                    if (_searchStatus != null) _searchStatus.Text = "No matches";
                    return;
                }

                int startPage = PageList.SelectedIndex;
                _searchPageCursor = _searchResultPages.FindIndex(p => p >= startPage);
                if (_searchPageCursor < 0) _searchPageCursor = 0;

                _searchTotalHits = sr.TotalHits;
                UpdateSearchStatus();

                int targetPage = _searchResultPages[_searchPageCursor];
                if (PageList.SelectedIndex != targetPage)
                    PageList.SelectedIndex = targetPage;
                else
                    HighlightSearchResultsOnCurrentPage();
            }
            catch
            {
                if (_searchStatus != null) _searchStatus.Text = "Search error";
            }
        }

        private void HighlightSearchResultsOnCurrentPage()
        {
            ClearSearchHighlights();
            int curPage = PageList.SelectedIndex;
            if (!_allSearchRects.ContainsKey(curPage)) return;
            if (!_renderDims.ContainsKey(curPage)) return;

            var (renderW, renderH) = _renderDims[curPage];

            try
            {
                using var pigDoc = PdfPigDoc.Open(_currentFile!);
                var page = pigDoc.GetPage(curPage + 1);
                double pdfW = page.Width;
                double pdfH = page.Height;
                double sx = renderW / pdfW;
                double sy = renderH / pdfH;

                foreach (var (left, bottom, right, top) in _allSearchRects[curPage])
                    AddSearchHighlight(left, bottom, right, top, sx, sy, renderH);
            }
            catch { }
        }

        private int _searchTotalHits;

        // Compact VSCode-style count ("2 / 5"); full detail lives in the tooltip.
        private void UpdateSearchStatus()
        {
            if (_searchStatus is null) return;
            if (_searchResultPages.Count == 0)
            {
                _searchStatus.Text = "No matches";
                _searchStatus.ToolTip = null;
                return;
            }
            _searchStatus.Text = $"{_searchPageCursor + 1} / {_searchResultPages.Count}";
            _searchStatus.ToolTip = $"{_searchTotalHits} match{(_searchTotalHits != 1 ? "es" : "")} on {_searchResultPages.Count} page{(_searchResultPages.Count != 1 ? "s" : "")}";
        }

        private void SearchNextResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor + 1) % _searchResultPages.Count;
            UpdateSearchStatus();
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void SearchPrevResult()
        {
            if (_searchResultPages.Count == 0) return;
            _searchPageCursor = (_searchPageCursor - 1 + _searchResultPages.Count) % _searchResultPages.Count;
            UpdateSearchStatus();
            int targetPage = _searchResultPages[_searchPageCursor];
            if (PageList.SelectedIndex != targetPage)
                PageList.SelectedIndex = targetPage;
            else
                HighlightSearchResultsOnCurrentPage();
        }

        private void AddSearchHighlight(double left, double bottom, double right, double top,
            double sx, double sy, double renderH)
        {
            double cx = left  * sx;
            double cy = renderH - (top * sy);
            double cw = (right - left) * sx;
            double ch = (top - bottom) * sy;
            var rect = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(80, 255, 165, 0)),
                Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 165, 0)),
                StrokeThickness = 1,
                Width = Math.Max(cw, 4),
                Height = Math.Max(ch, 4),
                IsHitTestVisible = false,
                Tag = "SearchHighlight"
            };
            Canvas.SetLeft(rect, cx);
            Canvas.SetTop(rect, cy);
            _annotationCanvas.Children.Add(rect);
        }

        private void ClearSearchHighlights()
        {
            var toRemove = _annotationCanvas.Children.OfType<Rectangle>()
                .Where(r => r.Tag is string s && s == "SearchHighlight").ToList();
            foreach (var r in toRemove)
                _annotationCanvas.Children.Remove(r);
            if (_searchStatus is not null)
                _searchStatus.Text = "";
        }

        // ============================================================
        // Inline text editing (double-click)
        // ============================================================

        private void EditTextAtPosition(Point canvasPos, int pageIdx)
        {
            if (_currentFile is null || !_renderDims.ContainsKey(pageIdx)) return;

            // Commit any existing edit first
            if (_activeTextBox is not null)
            {
                CommitActiveTextBox();
                return;
            }

            // Re-edit a user-placed text annotation: lift it into an editable box
            // pre-filled with its content, size (shown in points), and color.
            if (_annotations.TryGetValue(pageIdx, out var placedPage))
            {
                var placed = placedPage.OfType<TextAnnotation>()
                    .LastOrDefault(a => HitTestAnnotation(a, canvasPos, out _));
                if (placed is not null)
                {
                    var pcol = placed.GetColor();
                    _textColor = pcol;
                    _textOpacity = pcol.A;   // keep the opacity slider in sync with the edited text
                    _textFillColor = placed.GetFill();   // and the fill swatches in sync with the box
                    double syp = 1.0;
                    if (_doc is not null && _renderDims.TryGetValue(pageIdx, out var prd) && prd.h > 0)
                        syp = _doc.Pages[pageIdx].Height.Point / prd.h;
                    _textFontSize = Math.Max(1, Math.Round(placed.FontSize * syp));

                    _reeditOriginal = placed;
                    placedPage.Remove(placed);
                    RenderAllAnnotations(pageIdx);

                    var ptb = new TextBox
                    {
                        Text = placed.Content,
                        Background = TextEditBackground(),
                        Foreground = new SolidColorBrush(pcol),
                        BorderBrush = (SolidColorBrush)FindResource("Accent"), SelectionBrush = AccentBrush(),
                        CaretBrush = new SolidColorBrush(pcol),
                        Template = FlatTextBoxTemplate(),
                        BorderThickness = new Thickness(1),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = placed.FontSize,
                        Width = placed.Width > 0 ? placed.Width : TextBoxDefaultWidth,
                        MinHeight = 24,
                        Padding = new Thickness(2),
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Tag = pageIdx
                    };
                    Canvas.SetLeft(ptb, placed.Position.X);
                    Canvas.SetTop(ptb, placed.Position.Y);
                    _activeCanvas.Children.Add(ptb);
                    _activeTextBox = ptb;
                    ptb.PreviewKeyDown += TextBox_PreviewKeyDown;
                    ptb.Loaded += (s, ev) => { ptb.Focus(); Keyboard.Focus(ptb); ptb.SelectAll(); ptb.LostFocus += TextBox_LostFocus; AttachTextEditResizeHandles(ptb); };
                    ShowTextSettings();
                    SetStatus("Editing text — change size/color above, Enter to save");
                    return;
                }
            }

            // The click landed on an existing text cover but not its replacement text (handled above).
            // Don't start a fresh detection over an edit that already exists - that would stack a second
            // cover+text. Bail so the user grabs the existing text/cover instead of duplicating it.
            if (_annotations.TryGetValue(pageIdx, out var coverPage)
                && coverPage.OfType<CoverAnnotation>().Any(c => { var b = c.Bounds; b.Inflate(6, 6); return b.Contains(canvasPos); }))
            {
                SetStatus("Already an edit here - click its text to re-edit, or drag the cover");
                return;
            }

            try
            {
                var (renderW, renderH) = _renderDims[pageIdx];

                using var pigDoc = PdfPigDoc.Open(_currentFile);
                if (pageIdx >= pigDoc.NumberOfPages) return;
                var page = pigDoc.GetPage(pageIdx + 1);

                double pdfW = page.Width;
                double pdfH = page.Height;
                double sxInv = (double)renderW / pdfW; // pdf->canvas
                double syInv = (double)renderH / pdfH;

                // Convert all words to canvas coordinates upfront
                var canvasWords = page.GetWords().Select(w =>
                {
                    double cx = w.BoundingBox.Left * sxInv;
                    double cy = renderH - (w.BoundingBox.Top * syInv);
                    double cw = (w.BoundingBox.Right - w.BoundingBox.Left) * sxInv;
                    double ch = (w.BoundingBox.Top - w.BoundingBox.Bottom) * syInv;
                    return new { Word = w, Rect = new Rect(cx, cy, cw, ch) };
                }).ToList();

                if (canvasWords.Count == 0)
                {
                    // Scanned / image-only page: no text layer to detect. Fall back to a manual edit -
                    // drop a cover + empty text box at the click so the user can white out the scanned
                    // text and type over it by hand (resize the cover to fit).
                    double mf = Math.Max(_textFontSize * syInv, 8);   // current text size in canvas units
                    StartCoverTextEdit(pageIdx, new Rect(canvasPos.X, canvasPos.Y, 200, mf * 1.35), "", mf, "Segoe UI", syInv);
                    return;
                }

                // Find words on the same line as the click (Y overlap with tolerance)
                var clickY = canvasPos.Y;
                var lineWords = canvasWords
                    .Where(cw => clickY >= cw.Rect.Top - 3 && clickY <= cw.Rect.Bottom + 3)
                    .OrderBy(cw => cw.Rect.Left)  // strictly left-to-right
                    .ToList();

                if (lineWords.Count == 0)
                {
                    // Try nearest line within 20px
                    var nearest = canvasWords
                        .OrderBy(cw => Math.Abs((cw.Rect.Top + cw.Rect.Bottom) / 2 - clickY))
                        .First();
                    double nearMidY = (nearest.Rect.Top + nearest.Rect.Bottom) / 2;
                    lineWords = [..canvasWords
                        .Where(cw => Math.Abs((cw.Rect.Top + cw.Rect.Bottom) / 2 - nearMidY) < 5)
                        .OrderBy(cw => cw.Rect.Left)];
                }

                if (lineWords.Count == 0)
                {
                    SetStatus("No text line found at this position");
                    return;
                }

                // Narrow to the contiguous run of words around the click. Words at the same Y in a
                // second column are separated by a large horizontal gap, so stop there instead of
                // merging both columns into one edit (the "weird text" / page-spanning edit).
                if (lineWords.Count > 1)
                {
                    int ci = 0; double bestDx = double.MaxValue;
                    for (int i = 0; i < lineWords.Count; i++)
                    {
                        var r = lineWords[i].Rect;
                        double dx = canvasPos.X < r.Left ? r.Left - canvasPos.X
                                  : canvasPos.X > r.Right ? canvasPos.X - r.Right : 0;
                        if (dx < bestDx) { bestDx = dx; ci = i; }
                    }
                    double gapMax = Math.Max(lineWords[ci].Rect.Height * 1.5, 24);   // word spacing is small; a column gap is large
                    int lo = ci, hi = ci;
                    while (lo > 0 && lineWords[lo].Rect.Left - lineWords[lo - 1].Rect.Right <= gapMax) lo--;
                    while (hi < lineWords.Count - 1 && lineWords[hi + 1].Rect.Left - lineWords[hi].Rect.Right <= gapMax) hi++;
                    lineWords = lineWords.GetRange(lo, hi - lo + 1);
                }

                // Compute bounding box in canvas space
                double cLeft = lineWords.Min(w => w.Rect.Left);
                double cTop = lineWords.Min(w => w.Rect.Top);
                double cRight = lineWords.Max(w => w.Rect.Right);
                double cBottom = lineWords.Max(w => w.Rect.Bottom);
                double cWidth = cRight - cLeft;
                double cHeight = cBottom - cTop;

                string lineText = string.Join(" ", lineWords.Select(w => w.Word.Text));

                // If this line is already covered by an edit, don't detect it again - that just stacks a
                // duplicate cover+text on top of the existing one. The original PDF text under a cover is
                // "consumed": re-edit by clicking the replacement text instead.
                var lineRect = new Rect(cLeft, cTop, Math.Max(1, cWidth), Math.Max(1, cHeight));
                if (_annotations.TryGetValue(pageIdx, out var coveredPage)
                    && coveredPage.OfType<CoverAnnotation>().Any(c => c.Bounds.IntersectsWith(lineRect)))
                {
                    SetStatus("This line is already edited - click its text to change it");
                    return;
                }

                // Get actual font info from PdfPig letter data
                double canvasFontSize = cHeight * 0.75; // fallback
                string fontName = "Segoe UI"; // fallback
                var firstWord = lineWords.First().Word;
                try
                {
                    if (firstWord.Letters.Count > 0)
                    {
                        var letter = firstWord.Letters[0];
                        double pdfFontPts = letter.FontSize;
                        canvasFontSize = pdfFontPts * syInv;

                        // Try to get font name from letter
                        string? rawFont = null;
                        try { rawFont = letter.FontName; } catch { }
                        if (string.IsNullOrEmpty(rawFont))
                        {
                            // Some PdfPig versions use different property paths
                            try { rawFont = firstWord.FontName; } catch { }
                        }
                        if (!string.IsNullOrEmpty(rawFont))
                        {
                            string fontStr = rawFont!;
                            // Strip PDF subset prefix (e.g. "ABCDEF+FontName" -> "FontName")
                            if (fontStr.Contains('+'))
                                fontStr = fontStr[(fontStr.IndexOf('+') + 1)..];
                            // Clean common suffixes
                            fontStr = fontStr.Replace(",Bold", "").Replace(",Italic", "")
                                             .Replace("-Bold", "").Replace("-Italic", "")
                                             .Replace("-Roman", "").Replace("-Regular", "");
                            if (!string.IsNullOrWhiteSpace(fontStr))
                                fontName = fontStr;
                        }
                    }
                }
                catch { /* use fallbacks */ }

                // Drop the cover + editable text box for the detected line. Detected size carries the
                // EditTextSizeCorrection (WPF renders the source point size ~25% large); manual edits don't.
                StartCoverTextEdit(pageIdx, new Rect(cLeft, cTop, cWidth, cHeight), lineText,
                    Math.Max(canvasFontSize * EditTextSizeCorrection, 8), fontName, syInv);
            }
            catch (Exception ex)
            {
                SetStatus($"Text edit error: {ex.Message}");
            }
        }

        // Drops an opaque cover at the given line and opens an editable text box on top of it - the two
        // halves of an in-place edit. Used for a detected PDF-text line and, on a scanned page with no
        // text layer, for a manual edit at the click point. boxFontCanvas is the on-canvas font size;
        // the cover fill and text ink are sampled from the page so the edit blends in.
        private void StartCoverTextEdit(int pageIdx, Rect lineRect, string text, double boxFontCanvas, string fontName, double syInv)
        {
            double cLeft = lineRect.X, cTop = lineRect.Y, cWidth = lineRect.Width, cHeight = lineRect.Height;
            // Pair id shared with the replacement text - the cover renders dashed while paired.
            var cover = new CoverAnnotation { PageIndex = pageIdx, PairId = Guid.NewGuid().ToString("N"),
                Bounds = new Rect(cLeft - 3, cTop - 3, cWidth + 6, cHeight + 6) };
            var sampleRect = new Rect(cLeft, cTop, cWidth, cHeight);
            Color coverBg  = SampleCoverColor(pageIdx, sampleRect);
            Color inkColor = SampleTextColor(pageIdx, sampleRect, coverBg);
            cover.SetColor(coverBg);
            _textColor = inkColor; _textOpacity = inkColor.A;
            _textFontSize = Math.Max(1, Math.Round(boxFontCanvas / syInv));   // canvas units -> points
            _pendingEditWasDirty = _isDirty;   // capture before the cover dirties the doc
            if (!_annotations.ContainsKey(pageIdx)) _annotations[pageIdx] = [];
            _annotations[pageIdx].Add(cover);
            _pendingCover = cover;
            MarkDirty();
            RenderAllAnnotations(pageIdx);

            var tb = new TextBox
            {
                Text = text,
                Background = Brushes.Transparent,   // the opaque cover behind supplies the backdrop
                Foreground = new SolidColorBrush(inkColor),
                BorderBrush = (SolidColorBrush)FindResource("Accent"), SelectionBrush = AccentBrush(),
                CaretBrush = new SolidColorBrush(inkColor),
                Template = FlatTextBoxTemplate(),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily(fontName),
                FontSize = boxFontCanvas,
                Width = Math.Max(cWidth + 20, 80),
                MinHeight = 24,
                Padding = new Thickness(2, 0, 2, 0),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Tag = pageIdx
            };
            Canvas.SetLeft(tb, cLeft);
            Canvas.SetTop(tb, cTop);
            _activeCanvas.Children.Add(tb);
            _activeTextBox = tb;
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.Loaded += (s, ev) => { tb.Focus(); Keyboard.Focus(tb); tb.SelectAll(); tb.LostFocus += TextBox_LostFocus; AttachTextEditResizeHandles(tb); };
            ShowTextSettings();
            SetStatus(string.IsNullOrEmpty(text)
                ? "Type your text, then drag the cover over the original — Enter to save, Escape to cancel"
                : "Editing text — change size/color above, Enter to save, Escape to cancel");
        }

        // ============================================================
        // Text box handling
        // ============================================================

        // A flat TextBox template (just a themed border hosting the text) so the OS default focus border
        // and selection chrome - the stray WPF "blue" - never show on the in-canvas text editor.
        private static ControlTemplate FlatTextBoxTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
                { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            return new ControlTemplate(typeof(TextBox)) { VisualTree = b };
        }

        // Background shown WHILE editing a text box: the chosen fill if one is set, otherwise a faint
        // translucent neutral gray. Gray (not white) so the empty editable box stays visible on both
        // light/white pages and dark pages; it's only shown during editing and never committed.
        private Brush TextEditBackground()
            => _textFillColor.A > 0 ? new SolidColorBrush(_textFillColor)
                                    : new SolidColorBrush(Color.FromArgb(64, 128, 128, 128));

        // True when 'pos' (in _activeCanvas coordinates) falls inside the text box currently being
        // edited AND that box lives on _activeCanvas. Used so a click inside the box doesn't get
        // treated as a request to place a new one (the Grid-view "box jumps to cursor" bug).
        private bool ClickInsideActiveTextBox(Point pos)
        {
            if (_activeTextBox is null || !ReferenceEquals(_activeTextBox.Parent, _activeCanvas)) return false;
            double x = Canvas.GetLeft(_activeTextBox), y = Canvas.GetTop(_activeTextBox);
            if (double.IsNaN(x) || double.IsNaN(y)) return false;
            double w = _activeTextBox.ActualWidth  > 0 ? _activeTextBox.ActualWidth  : _activeTextBox.Width;
            double h = _activeTextBox.ActualHeight > 0 ? _activeTextBox.ActualHeight : Math.Max(_activeTextBox.MinHeight, 24);
            return pos.X >= x && pos.X <= x + w && pos.Y >= y && pos.Y <= y + h;
        }

        private void PlaceTextBox(Point pos, int pageIdx)
        {
            // _textFontSize is a point size; convert to the page's canvas (render-dim) units so
            // it renders and exports as real points. DrawAnnotationsOnDocument multiplies by
            // sy = page.Height.Point / renderH, so dividing by sy here makes "14" export as 14pt.
            double fontCanvas = _textFontSize;
            if (_doc is not null && _renderDims.TryGetValue(pageIdx, out var rdims) && rdims.h > 0)
            {
                double sy = _doc.Pages[pageIdx].Height.Point / rdims.h;
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            // A default-size box dropped at the click point. Width is fixed (text wraps to it) and the
            // box auto-grows downward as you type; resize the width later via the corner handles.
            var tb = new TextBox
            {
                Background = TextEditBackground(),
                Foreground = new SolidColorBrush(_textColor),
                BorderBrush = (SolidColorBrush)FindResource("Accent"), SelectionBrush = AccentBrush(),
                CaretBrush = new SolidColorBrush(_textColor),
                Template = FlatTextBoxTemplate(),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = fontCanvas,
                Width = TextBoxDefaultWidth,
                MinHeight = 24,
                Padding = new Thickness(2),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Tag = pageIdx
            };
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, pos.Y);
            _activeCanvas.Children.Add(tb);
            _activeTextBox = tb;
            tb.PreviewKeyDown += TextBox_PreviewKeyDown;
            tb.LostFocus += TextBox_LostFocus;
            // Focus the box and attach its live resize handles once laid out. Loaded fires on first
            // placement; a dispatcher fallback covers re-entry (Text tool -> Select -> Text again),
            // where Loaded may have already run - without it the new box silently took no typing and
            // showed no handles. Activate is idempotent (guards against double focus/handle attach).
            void Activate()
            {
                if (!ReferenceEquals(_activeTextBox, tb)) return;
                tb.Focus();
                Keyboard.Focus(tb);
                if (!ReferenceEquals(_tehBox, tb)) AttachTextEditResizeHandles(tb);
            }
            tb.Loaded += (s, e) => Activate();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(Activate));
        }

        // ── Live resize handles around the editing TextBox ──────────────────────────────
        // Corner squares the user can drag to resize the box mid-edit, then keep typing. The
        // box auto-grows in height until a handle is dragged, after which the height is free-form.
        private void AttachTextEditResizeHandles(TextBox tb)
        {
            RemoveTextEditHandles();
            _tehBox = tb;
            double inv = 1.0;
            if (_activeCanvas.LayoutTransform is ScaleTransform sc && sc.ScaleX > 0.0001) inv = 1.0 / sc.ScaleX;
            double hs = 12 * inv;
            foreach (string tag in new[] { "NW", "NE", "SE", "SW" })
            {
                var hd = new Rectangle
                {
                    Width = hs, Height = hs,
                    Fill = AccentBrush(),
                    Stroke = Brushes.White, StrokeThickness = 1 * inv,
                    Cursor = (tag is "NW" or "SE") ? Cursors.SizeNWSE : Cursors.SizeNESW,
                    Focusable = false,   // so grabbing a handle does not blur (and commit) the TextBox
                    Tag = tag
                };
                Panel.SetZIndex(hd, 200);
                // Hit detection + drag are handled in the canvas gesture handlers (which run as
                // PreviewMouseLeftButtonDown and would otherwise intercept the click), mirroring the
                // committed-annotation resize handles.
                _textEditHandles.Add(hd);
                _activeCanvas.Children.Add(hd);
            }
            tb.SizeChanged += TextEditBox_SizeChanged;
            LayoutTextEditHandles();
        }

        private void TextEditBox_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTextEditHandles();

        private void LayoutTextEditHandles()
        {
            if (_tehBox is null || _textEditHandles.Count == 0) return;
            double x = Canvas.GetLeft(_tehBox), y = Canvas.GetTop(_tehBox);
            double w = _tehBox.ActualWidth > 0 ? _tehBox.ActualWidth : _tehBox.Width;
            double h = _tehBox.ActualHeight;
            foreach (var hd in _textEditHandles)
            {
                double hsz = hd.Width;
                (double cx, double cy) = (hd.Tag as string) switch
                {
                    "NW" => (x,     y),
                    "NE" => (x + w, y),
                    "SW" => (x,     y + h),
                    _    => (x + w, y + h)   // SE
                };
                Canvas.SetLeft(hd, cx - hsz / 2);
                Canvas.SetTop(hd, cy - hsz / 2);
            }
        }

        private void RemoveTextEditHandles()
        {
            if (_tehBox is not null) _tehBox.SizeChanged -= TextEditBox_SizeChanged;
            foreach (var hd in _textEditHandles) RemoveFromParent(hd);
            _textEditHandles.Clear();
            _tehBox = null;
            _draggingTextEditHandle = false;
        }

        // Remove a canvas child from whatever Panel actually parents it, instead of assuming it lives
        // on _activeCanvas. In continuous/grid view _activeCanvas follows the mouse to whichever page
        // was last clicked, so a text-edit box, its whiteout, or its handles - placed earlier on a
        // different page's canvas - would otherwise survive removal and become orphaned: still painted,
        // but unreachable by Delete, Clear All, or resize. Its live Parent is always the correct host.
        private static void RemoveFromParent(UIElement? el)
        {
            if (el is FrameworkElement fe && fe.Parent is Panel p)
                p.Children.Remove(el);
        }

        // Hit-test a live text-edit handle at the given canvas point; returns its corner tag or null.
        private string? TextEditHandleAt(Point pos)
        {
            foreach (var hd in _textEditHandles)
            {
                double hx = Canvas.GetLeft(hd), hy = Canvas.GetTop(hd);
                if (pos.X >= hx && pos.X <= hx + hd.Width &&
                    pos.Y >= hy && pos.Y <= hy + hd.Height)
                    return hd.Tag as string ?? "SE";
            }
            return null;
        }

        // Attached as PreviewKeyDown (tunneling) so Enter is caught before the TextBox inserts a line
        // break: Enter commits, Shift+Enter falls through to make a newline (the box is AcceptsReturn).
        private void TextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                RemoveTextEditHandles();
                if (_activeTextBox is not null)
                {
                    RemoveFromParent(_activeTextBox);
                    _activeTextBox = null;
                }
                // Escaping an existing-text edit drops the cover too (it was placed un-undone).
                if (_pendingCover is not null) DiscardPendingCover();
                if (_reeditOriginal is not null)
                {
                    int rp = _reeditOriginal.PageIndex;
                    if (!_annotations.TryGetValue(rp, out var rlist)) { rlist = []; _annotations[rp] = rlist; }
                    rlist.Add(_reeditOriginal);
                    _reeditOriginal = null;
                    RenderAllAnnotations(rp);
                }
                if (_currentTool != EditTool.Text) HideTextSettings();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                CommitActiveTextBox();
                e.Handled = true;
            }
        }

        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Don't commit while a resize handle is being dragged (the box temporarily loses focus).
            if (_draggingTextEditHandle) return;
            // Commit if the box has content, or (for an existing-text edit) even when emptied, so the
            // pending cover is resolved instead of lingering when the user clicks away from a blank edit.
            if (_activeTextBox is not null && (!string.IsNullOrWhiteSpace(_activeTextBox.Text) || _pendingCover is not null))
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Keep the edit box open if focus moved into the size/color bar so the
                    // user can restyle (the Size ComboBox takes focus; color swatches do not).
                    if (_textSettingsBar is not null && Keyboard.FocusedElement is DependencyObject fe
                        && IsDescendantOf(fe, _textSettingsBar))
                        return;
                    CommitActiveTextBox();
                }),
                System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void CommitActiveTextBox()
        {
            if (_activeTextBox is null) return;
            var tb = _activeTextBox;
            _activeTextBox = null;
            RemoveTextEditHandles();
            string reeditPair = _reeditOriginal?.PairId ?? "";   // preserve a re-edited text's cover pairing
            _reeditOriginal = null;   // committing replaces any annotation being re-edited

            string content = tb.Text.Trim();
            int pageIdx = tb.Tag is int idx ? idx : PageList.SelectedIndex;
            double x = Canvas.GetLeft(tb);
            double y = Canvas.GetTop(tb);

            // Remove the editing box from whatever canvas actually parents it. _activeCanvas may have
            // moved to another page when the user clicked away to commit (continuous/grid), and a
            // re-looked-up page canvas can be a different instance than the one the box was placed on,
            // either of which leaves the box orphaned (visible, but immune to Delete/Clear All). Its
            // live Parent is the correct host.
            RemoveFromParent(tb);

            if (!string.IsNullOrEmpty(content))
            {
                double boxW = (!double.IsNaN(tb.Width) && tb.Width > 0) ? tb.Width
                            : (tb.ActualWidth > 0 ? tb.ActualWidth : TextBoxDefaultWidth);
                var ta = new TextAnnotation
                {
                    PageIndex = pageIdx,
                    Position = new Point(x, y),
                    Content = content,
                    FontSize = tb.FontSize,
                    Width = boxW
                };
                ta.SetColor(tb.Foreground is SolidColorBrush scb ? scb.Color : Colors.Black);
                // A cover-paired edit gets no fill of its own - the opaque cover behind it is the backdrop.
                ta.SetFill(_pendingCover is not null ? Colors.Transparent : _textFillColor);
                // Free-form height if the box was manually resized; otherwise fit to the wrapped text.
                ta.Height = (!double.IsNaN(tb.Height) && tb.Height > 0)
                    ? tb.Height
                    : MeasureTextBoxHeight(content, boxW, tb.FontSize);
                // Keep the placed box fully on-page so its corners (and resize handles) stay reachable.
                ta.Position = ClampRectToPage(pageIdx, new Rect(ta.Position, new Size(ta.Width, ta.Height))).Location;
                // Carry the pairing so the cover knows its partner text exists (renders dashed).
                ta.PairId = _pendingCover is not null ? _pendingCover.PairId : reeditPair;
                if (_pendingCover is not null)
                {
                    // Existing-text edit: the cover is already in _annotations. Add the text beside it and
                    // push ONE grouped undo so a single Ctrl+Z right after cancels the whole edit. After
                    // this, cover and text are independent annotations (move/resize/recolor separately).
                    _annotations[pageIdx].Add(ta);
                    _undoStack.Push(new UndoEntry(UndoKind.AnnotationGroup, pageIdx,
                        WasDirty: _pendingEditWasDirty, AnnotGroup: [_pendingCover, ta]));
                    _pendingCover = null;
                    MarkDirty();
                }
                else
                {
                    AddAnnotation(ta);
                }
                RenderAllAnnotations(pageIdx);   // redraw on the correct page's canvas
            }
            else if (_pendingCover is not null)
            {
                // Edit left empty - abandon it and drop the cover (added without its own undo entry).
                DiscardPendingCover();
            }
            if (_currentTool != EditTool.Text) HideTextSettings();
        }

        // Remove the not-yet-committed cover when an existing-text edit is cancelled or left empty. The
        // cover was added straight to _annotations without an undo entry, so just drop it and repaint.
        private void DiscardPendingCover()
        {
            if (_pendingCover is null) return;
            int pg = _pendingCover.PageIndex;
            if (_annotations.TryGetValue(pg, out var list)) list.Remove(_pendingCover);
            _pendingCover = null;
            MarkDirty(_pendingEditWasDirty);
            RenderAllAnnotations(pg);
        }

        // ── Cover background sampling ───────────────────────────────────────────────
        // Reads the page background color around an existing-text line so a cover blends into colored
        // headers/panels instead of showing a white box. Best-effort: returns white on any failure.

        // The page's rendered bitmap: the Image sibling of its overlay canvas (continuous/grid/two-page),
        // or the single-view PageImage. View-mode independent so sampling works everywhere.
        private System.Windows.Media.Imaging.BitmapSource? PageBitmapFor(int pageIdx)
        {
            if (_continuousCanvases.TryGetValue(pageIdx, out var overlay) && overlay.Parent is Panel mp)
                foreach (var ch in mp.Children)
                    if (ch is Image im && im.Source is System.Windows.Media.Imaging.BitmapSource bs) return bs;
            if (pageIdx == PageList.SelectedIndex && FindName("PageImage") is Image pgi
                && pgi.Source is System.Windows.Media.Imaging.BitmapSource pbs) return pbs;
            return null;
        }

        private static Color ReadBgraPixel(System.Windows.Media.Imaging.BitmapSource bmp, int x, int y)
        {
            x = Math.Max(0, Math.Min(x, bmp.PixelWidth - 1));
            y = Math.Max(0, Math.Min(y, bmp.PixelHeight - 1));
            var px = new byte[4];
            bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);   // Bgra32: B,G,R,A
            // Composite over white (the PDF page background) so transparent pixels - common on repaired
            // or scanned renders - read as white, not black. Returns an opaque color for sampling.
            double a = px[3] / 255.0;
            byte r = (byte)(px[2] * a + 255 * (1 - a));
            byte g = (byte)(px[1] * a + 255 * (1 - a));
            byte b = (byte)(px[0] * a + 255 * (1 - a));
            return Color.FromRgb(r, g, b);
        }

        /// <summary>Background color around a text line, in canvas (render-dim) coordinates. White on failure.</summary>
        private Color SampleCoverColor(int pageIdx, Rect textBounds)
        {
            try
            {
                var bmp = PageBitmapFor(pageIdx);
                if (bmp is null || !_renderDims.TryGetValue(pageIdx, out var rd) || rd.w <= 0 || rd.h <= 0)
                    return Colors.White;
                double sx = bmp.PixelWidth  / (double)rd.w;   // render-dim -> bitmap pixels
                double sy = bmp.PixelHeight / (double)rd.h;
                // Sample the whitespace just above and below the line (usually pure background) at a few
                // x offsets; take the median by luminance to shrug off a stray glyph or anti-aliased edge.
                double gap = Math.Max(3.0, textBounds.Height * 0.4);
                var cols = new List<Color>();
                foreach (double f in new[] { 0.2, 0.5, 0.8 })
                {
                    double x = textBounds.Left + textBounds.Width * f;
                    cols.Add(ReadBgraPixel(bmp, (int)Math.Round(x * sx), (int)Math.Round((textBounds.Top - gap) * sy)));
                    cols.Add(ReadBgraPixel(bmp, (int)Math.Round(x * sx), (int)Math.Round((textBounds.Bottom + gap) * sy)));
                }
                if (cols.Count == 0) return Colors.White;
                cols.Sort((a, b) => (0.299 * a.R + 0.587 * a.G + 0.114 * a.B)
                                    .CompareTo(0.299 * b.R + 0.587 * b.G + 0.114 * b.B));
                return cols[cols.Count / 2];
            }
            catch { return Colors.White; }
        }

        private static double ColorDist(Color a, Color b)
        {
            double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            return Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        /// <summary>The text "ink" color of a line: the color inside the glyph box farthest from the
        /// page background. Averages the purest-ink samples so anti-aliased edges don't desaturate it.
        /// Black on failure or when no real contrast is found.</summary>
        private Color SampleTextColor(int pageIdx, Rect textBounds, Color bg)
        {
            try
            {
                var bmp = PageBitmapFor(pageIdx);
                if (bmp is null || !_renderDims.TryGetValue(pageIdx, out var rd) || rd.w <= 0 || rd.h <= 0)
                    return Colors.Black;
                double sx = bmp.PixelWidth  / (double)rd.w;
                double sy = bmp.PixelHeight / (double)rd.h;
                int cols = 16, rows = Math.Max(3, (int)Math.Min(8, textBounds.Height / 3));
                var scored = new List<(double dist, Color c)>();
                for (int ix = 0; ix < cols; ix++)
                    for (int iy = 0; iy < rows; iy++)
                    {
                        double x = textBounds.Left + textBounds.Width  * (ix + 0.5) / cols;
                        double y = textBounds.Top  + textBounds.Height * (iy + 0.5) / rows;
                        var c = ReadBgraPixel(bmp, (int)Math.Round(x * sx), (int)Math.Round(y * sy));
                        scored.Add((ColorDist(c, bg), c));
                    }
                if (scored.Count == 0) return Colors.Black;
                scored.Sort((a, b) => b.dist.CompareTo(a.dist));   // most ink-like first
                double maxDist = scored[0].dist;
                if (maxDist < 24) return Colors.Black;             // no real contrast -> default
                double thresh = maxDist * 0.7;                     // purest-ink cluster only
                double r = 0, g = 0, bl = 0; int n = 0;
                foreach (var (dist, c) in scored) { if (dist < thresh) break; r += c.R; g += c.G; bl += c.B; n++; }
                return n == 0 ? Colors.Black : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(bl / n));
            }
            catch { return Colors.Black; }
        }

        // ============================================================
        // Keyboard shortcuts
        // ============================================================

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            // Don't intercept keys when typing in any TextBox (typewriter tool or form field)
            if (e.OriginalSource is TextBox) return;
            if (_activeTextBox is not null && _activeTextBox.IsFocused) return;

            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopySelectedText();
                e.Handled = true;
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
            {
                PasteFromClipboard();
                e.Handled = true;
            }
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Prefer selecting all annotations (shows where everything is, makes stacked annotations
                // editable); fall back to selecting page text when there are none on screen.
                if (!SelectAllAnnotations()) SelectAllText();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ToggleSearchBar();
                e.Handled = true;
            }
            // TEMPORARY: Ctrl+Shift+F12 signs the currently-open PDF with the Desktop test cert.
            // Remove before release (see Services/Signing/SigningSmokeTest.cs).
            else if (e.Key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                Services.Signing.SigningSmokeTest.RunOnFile(_currentFile);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                ApplyCrop([PageList.SelectedIndex]);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _currentTool == EditTool.Crop && _cropConfirmBar is not null)
            {
                HideCropConfirmBar();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && ShortcutOverlay.Visibility == Visibility.Visible)
            {
                FadeOverlayOut(ShortcutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && AboutOverlay.Visibility == Visibility.Visible)
            {
                FadeOverlayOut(AboutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SettingsOverlay.Visibility == Visibility.Visible)
            {
                SlideSettingsClosed();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _searchBar is not null && _searchBar.Visibility == Visibility.Visible)
            {
                CloseSearchBar();
                e.Handled = true;
            }
            else if (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
                else FadeOverlayIn(ShortcutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.F1)
            {
                // Toggle the shortcuts overlay (conventional Help key, alongside Ctrl+?).
                if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
                else FadeOverlayIn(ShortcutOverlay);
                e.Handled = true;
            }
            else if (e.Key == Key.F2)
            {
                // Toggle the About dialog.
                if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
                else ShowAboutOverlay();
                e.Handled = true;
            }
            else if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Print_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && (_selectedAnnotation is not null || _selectedSet.Count > 0))
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!e.IsRepeat) Undo_Click(this, e);   // ignore key auto-repeat so one press = one undo
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                SaveAs_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SaveInPlace();
                e.Handled = true;
            }
            else if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CloseTab(_active);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                CycleTab(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CycleTab(1);
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Open_Click(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                NewDocument();
                e.Handled = true;
            }
            else if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SidebarToggle_Click(this, e);   // collapse / restore the sidebar
                e.Handled = true;
            }
            // Bare-key tool switches. Only when a document is open, no modifier is held, and no
            // overlay is up (and not while typing - guarded at the top of this handler).
            else if (Keyboard.Modifiers == ModifierKeys.None && _doc is not null
                     && ShortcutOverlay.Visibility != Visibility.Visible
                     && AboutOverlay.Visibility    != Visibility.Visible
                     && SettingsOverlay.Visibility != Visibility.Visible
                     && TryToolShortcut(e.Key))
            {
                e.Handled = true;
            }
            else if ((e.Key == Key.Left || e.Key == Key.Up) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_doc is not null && PageList.SelectedIndex > 0)
                {
                    PageList.SelectedIndex--;
                    e.Handled = true;
                }
            }
            else if ((e.Key == Key.Right || e.Key == Key.Down) && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (_doc is not null && PageList.SelectedIndex < _doc.PageCount - 1)
                {
                    PageList.SelectedIndex++;
                    e.Handled = true;
                }
            }
            else if ((e.Key == Key.OemPlus || e.Key == Key.Add) && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep);
                e.Handled = true;
            }
            else if ((e.Key == Key.OemMinus || e.Key == Key.Subtract) && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (_viewMode == ViewMode.Grid) GridZoomStep(true); else SetZoom(_zoomLevel - ZoomStep);
                e.Handled = true;
            }
            else if (e.Key == Key.D0 && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SetZoom(1.0);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                // No overlay active — ESC exits the app
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Space && !_spaceHeld)
            {
                _spaceHeld = true;
                PagePreviewPanel.Cursor = Cursors.Hand;
                e.Handled = true;
            }
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            base.OnPreviewKeyUp(e);
            if (e.Key == Key.Space && _spaceHeld)
            {
                _spaceHeld = false;
                if (!_isPanning)
                    PagePreviewPanel.Cursor = Cursors.Arrow;
                e.Handled = true;
            }
        }

        // Maps a bare key to an editing tool. Returns false for any other key so the caller's
        // shortcut chain continues. Mirrors the toolbar tool buttons exactly - Signature routes
        // through its button handler so the signature picker opens (a bare SetTool would only arm
        // the tool without showing the menu).
        private bool TryToolShortcut(Key key)
        {
            switch (key)
            {
                // Tools are reachable by their toolbar position (1-9, left to right); the original letter
                // keys stay as fallbacks. Both the number-row and numpad digits map.
                case Key.V: case Key.D1: case Key.NumPad1: SetTool(EditTool.Select);        return true;
                case Key.T: case Key.D2: case Key.NumPad2: SetTool(EditTool.Text);          return true;
                case Key.U: case Key.D3: case Key.NumPad3: SetTool(EditTool.Underline);     return true;
                case Key.K: case Key.D4: case Key.NumPad4: SetTool(EditTool.Strikethrough); return true;
                case Key.H: case Key.D5: case Key.NumPad5: SetTool(EditTool.Highlight);     return true;
                case Key.D: case Key.D6: case Key.NumPad6: SetTool(EditTool.Draw);          return true;
                case Key.I: case Key.D7: case Key.NumPad7: SetTool(EditTool.Image);         return true;
                case Key.G: case Key.D8: case Key.NumPad8: ToolSignature_Click(this, new RoutedEventArgs()); return true;
                case Key.C: case Key.D9: case Key.NumPad9: SetTool(EditTool.Crop);          return true;
                default:    return false;
            }
        }

        // Appends each tool's toolbar position (1-9) to its tooltip, e.g. "Highlight (5)". Re-resolves the
        // localized base text so a language switch keeps the right wording (re-run from SelectLocale).
        private void ApplyToolNumberTooltips()
        {
            void Set(System.Windows.Controls.Button btn, string key, int n)
            {
                if (btn != null && TryFindResource(key) is string s) btn.ToolTip = $"{s} ({n})";
            }
            Set(ToolSelectBtn,    "Str_TT_SelectTool",    1);
            Set(ToolTextBtn,      "Str_TT_TextTool",      2);
            Set(ToolUnderlineBtn, "Str_TT_UnderlineTool", 3);
            Set(ToolStrikeBtn,    "Str_TT_StrikeTool",    4);
            Set(ToolHighlightBtn, "Str_TT_HighlightTool", 5);
            Set(ToolDrawBtn,      "Str_TT_DrawTool",      6);
            Set(ToolImageBtn,     "Str_TT_ImageTool",     7);
            Set(ToolSignatureBtn, "Str_TT_SignatureTool", 8);
            Set(ToolCropBtn,      "Str_TT_CropTool",      9);
        }

        // ============================================================
        // Annotation management
        // ============================================================

        private void AddAnnotation(PageAnnotation annotation)
        {
            if (!_annotations.ContainsKey(annotation.PageIndex))
                _annotations[annotation.PageIndex] = [];
            _annotations[annotation.PageIndex].Add(annotation);
            _undoStack.Push(new UndoEntry(UndoKind.Annotation, annotation.PageIndex, WasDirty: _isDirty, Annot: annotation));
            MarkDirty();
        }

        /// <summary>
        /// Saves the current in-memory document bytes onto the undo stack so that
        /// document-level operations (crop, delete page, merge, reorder) can be undone.
        /// Must be called BEFORE modifying _doc.
        /// </summary>
        private void PushDocUndo()
        {
            if (_doc is null) return;
            using var ms = new System.IO.MemoryStream();
            _doc.Save(ms);
            _undoStack.Push(new UndoEntry(UndoKind.Document, DocBytes: ms.ToArray(), WasDirty: _isDirty));
        }

        // Height (canvas units) that the wrapped text needs at the given box width and font size, so a
        // text box can auto-grow to fit its content. Includes the 2px inner padding used everywhere.
        private static double MeasureTextBoxHeight(string text, double width, double fontSize)
        {
            double inner = Math.Max(1, width - 4);   // minus left+right padding (2 + 2)
            var ft = new FormattedText(
                string.IsNullOrEmpty(text) ? " " : text,
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), Math.Max(1, fontSize), Brushes.Black, 1.0)
            { MaxTextWidth = inner };
            return Math.Ceiling(ft.Height) + 4;      // plus top + bottom padding
        }

        private void RenderTextAnnotation(TextAnnotation ta)
        {
            // A fixed W x H box: optional fill background, text wrapped to the width and clipped to the
            // height (so a free-form-resized box behaves like an image/crop frame).
            var tb = new TextBlock
            {
                Text = ta.Content,
                Foreground = new SolidColorBrush(ta.GetColor()),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = ta.FontSize,
                Padding = new Thickness(2),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };
            // Crisp glyphs: pixel-snapped layout + grayscale AA (ClearType can't subpixel on the
            // transparent overlay, and the default left the placed text looking aliased).
            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(tb, TextRenderingMode.Grayscale);
            var box = new Border
            {
                Width = Math.Max(1, ta.Width),
                Height = Math.Max(1, ta.Height),
                Background = ta.HasFill ? new SolidColorBrush(ta.GetFill()) : Brushes.Transparent,
                ClipToBounds = true,
                IsHitTestVisible = false,
                Child = tb
            };
            Canvas.SetLeft(box, ta.Position.X);
            Canvas.SetTop(box, ta.Position.Y);
            _activeCanvas.Children.Add(box);
        }

        private void RenderAllAnnotations(int pageIndex)
        {
            // Resolve this page's annotation surface from the unified per-page overlay map, which
            // every multi-page view populates; fall back to the single-page canvas. View-mode
            // independent on purpose so the tools behave identically in all four modes.
            _activeCanvas = _continuousCanvases.TryGetValue(pageIndex, out var pageCanvas)
                ? pageCanvas : _annotationCanvas;
            _activeCanvas.Children.Clear();

            if (_annotations.TryGetValue(pageIndex, out var annotList))
            foreach (var annot in annotList)
            {
                switch (annot)
                {
                    case TextAnnotation ta:
                        RenderTextAnnotation(ta);
                        break;
                    case CoverAnnotation cov:
                        var covRect = new Rectangle
                        {
                            Fill = new SolidColorBrush(cov.GetColor()),
                            Width = cov.Bounds.Width, Height = cov.Bounds.Height
                        };
                        // While being typed into, dash-outline the cover so it's visible behind the live
                        // text box. Otherwise just the opaque fill - its outline only appears on selection
                        // (drawn as selection chrome), so a deselected cover stays clean. Screen-only; the
                        // flattened/saved PDF draws just the fill.
                        if (ReferenceEquals(cov, _pendingCover))
                        {
                            covRect.Stroke = DarkerAccentBrush();
                            covRect.StrokeThickness = 1;
                            covRect.StrokeDashArray = new DoubleCollection { 4, 3 };
                        }
                        Canvas.SetLeft(covRect, cov.Bounds.X);
                        Canvas.SetTop(covRect, cov.Bounds.Y);
                        _activeCanvas.Children.Add(covRect);
                        break;
                    case HighlightAnnotation ha:
                        var hr = ha.DrawRect();
                        var rect = new Rectangle
                        {
                            Fill = new SolidColorBrush(ha.GetColor()),
                            Width = hr.Width,
                            Height = hr.Height
                        };
                        Canvas.SetLeft(rect, hr.X);
                        Canvas.SetTop(rect, hr.Y);
                        _activeCanvas.Children.Add(rect);
                        break;
                    case InkAnnotation ia:
                        if (ia.Points.Count < 2) continue;
                        var poly = new Polyline
                        {
                            Stroke = new SolidColorBrush(ia.GetColor()),
                            StrokeThickness = ia.StrokeWidth,
                            StrokeLineJoin = PenLineJoin.Round,
                            StrokeStartLineCap = PenLineCap.Round,
                            StrokeEndLineCap = PenLineCap.Round
                        };
                        foreach (var pt in ia.Points) poly.Points.Add(pt);
                        _activeCanvas.Children.Add(poly);
                        break;
                    case SignatureAnnotation sa:
                        if (sa.ImageData is not null)
                        {
                            // Image-based signature (decoded once, then cached on the annotation)
                            var bmp = GetAnnotationBitmap(sa, sa.ImageData);
                            if (bmp != null)
                            {
                                var imgCtrl = new System.Windows.Controls.Image
                                {
                                    Source = bmp,
                                    Width = sa.SourceWidth * sa.Scale,
                                    Height = sa.SourceHeight * sa.Scale,
                                    Stretch = System.Windows.Media.Stretch.Uniform,
                                    IsHitTestVisible = false
                                };
                                Canvas.SetLeft(imgCtrl, sa.Position.X);
                                Canvas.SetTop(imgCtrl, sa.Position.Y);
                                _activeCanvas.Children.Add(imgCtrl);
                            }
                        }
                        else
                        {
                            foreach (var stroke in sa.Strokes)
                            {
                                if (stroke.Count < 2) continue;
                                var sigPoly = new Polyline
                                {
                                    Stroke = Brushes.Black,
                                    StrokeThickness = sa.StrokeWidth * sa.Scale,
                                    StrokeLineJoin = PenLineJoin.Round,
                                    StrokeStartLineCap = PenLineCap.Round,
                                    StrokeEndLineCap = PenLineCap.Round
                                };
                                foreach (var pt in stroke)
                                    sigPoly.Points.Add(new Point(
                                        sa.Position.X + pt.X * sa.Scale,
                                        sa.Position.Y + pt.Y * sa.Scale));
                                _activeCanvas.Children.Add(sigPoly);
                            }
                        }
                        break;

                    case ImageAnnotation ia:
                        var iaBmp = GetAnnotationBitmap(ia, ia.ImageData);
                        if (iaBmp != null)
                        {
                            var iaCtrl = new System.Windows.Controls.Image
                            {
                                Source = iaBmp,
                                Width = ia.SourceWidth * ia.Scale,
                                Height = ia.SourceHeight * ia.Scale,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(iaCtrl, ia.Position.X);
                            Canvas.SetTop(iaCtrl, ia.Position.Y);
                            _activeCanvas.Children.Add(iaCtrl);
                        }
                        break;
                }
            }

            // Re-add form field overlays — RenderAllAnnotations clears the canvas so they must be restored.
            if (_renderDims.TryGetValue(pageIndex, out var dims))
                RenderFormFields(pageIndex, dims.w, dims.h);
        }

        // Decode a placed annotation's Base64 image once and cache the frozen result on the annotation,
        // so repeated renders (e.g. every mousemove of a resize-drag) reuse it instead of re-decoding.
        private static System.Windows.Media.Imaging.BitmapSource? GetAnnotationBitmap(PlacedAnnotation a, string? data)
        {
            if (a.CachedBitmap != null) return a.CachedBitmap;
            if (string.IsNullOrEmpty(data)) return null;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = new System.IO.MemoryStream(Convert.FromBase64String(data));
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                a.CachedBitmap = bmp;
                return bmp;
            }
            catch { return null; }
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            // Commit any in-progress text box first so its annotation is on the undo stack and the
            // order is deterministic. Otherwise the text box commits asynchronously (LostFocus fires
            // when a re-render clears the canvas), which races the undo and makes a press appear to
            // do nothing - the cause of "second Ctrl+Z did nothing" after placing several texts.
            if (_activeTextBox is not null)
                CommitActiveTextBox();

            if (_undoStack.Count == 0)
            {
                SetStatus("Nothing to undo");
                return;
            }

            var entry = _undoStack.Pop();

            if (entry.Kind == UndoKind.Annotation)
            {
                int pageIdx = entry.PageIdx;
                if (_annotations.TryGetValue(pageIdx, out var pageList) && pageList.Count > 0)
                {
                    // Remove the exact annotation this entry recorded (not just the last one in the
                    // list), so undo stays correct even when annotations were re-edited or reordered.
                    if (entry.Annot is not null)
                        pageList.Remove(entry.Annot);
                    else
                        pageList.RemoveAt(pageList.Count - 1);
                }
                ClearSelection();
                RenderAllAnnotations(pageIdx);
                MarkDirty(entry.WasDirty);
                SetStatus("Undid last annotation");
            }
            else if (entry.Kind == UndoKind.AnnotationGroup && entry.AnnotGroup is not null)
            {
                // A grouped edit (text cover + replacement text). Remove the exact annotations recorded,
                // so one Ctrl+Z cancels the whole edit.
                int pageIdx = entry.PageIdx;
                if (_annotations.TryGetValue(pageIdx, out var pageList))
                    foreach (var a in entry.AnnotGroup) pageList.Remove(a);
                ClearSelection();
                RenderAllAnnotations(pageIdx);
                MarkDirty(entry.WasDirty);
                SetStatus("Undid text edit");
            }
            else if (entry.Kind == UndoKind.StampBatch && entry.Pages is not null)
            {
                // Page-number stamping adds one annotation per page as a single action; remove the
                // last annotation from each stamped page in one undo.
                foreach (int p in entry.Pages)
                    if (_annotations.TryGetValue(p, out var list) && list.Count > 0)
                        list.RemoveAt(list.Count - 1);
                ClearSelection();
                foreach (int p in entry.Pages)
                    if (_continuousCanvases.ContainsKey(p) || p == PageList.SelectedIndex)
                        RenderAllAnnotations(p);
                MarkDirty(entry.WasDirty);
                SetStatus("Removed stamped page numbers");
            }
            else if (entry.Kind == UndoKind.ClearAnnotations && entry.AnnotSnapshot is not null)
            {
                // Restore every page's annotations that the clear removed, in one undo.
                foreach (var kv in entry.AnnotSnapshot)
                    _annotations[kv.Key] = [.. kv.Value];
                ClearSelection();
                RenderAnnotationsOnAllVisiblePages();
                MarkDirty(entry.WasDirty);
                SetStatus("Restored cleared annotations");
            }
            else // Document snapshot
            {
                if (entry.DocBytes is null) return;
                int selectedIdx = PageList.SelectedIndex;
                var tempPath = App.MakeTempFile("undo");
                System.IO.File.WriteAllBytes(tempPath, entry.DocBytes);
                _doc?.Close();
                // PdfSharpCore can write a snapshot whose xref offset points at the xref table,
                // producing "Unexpected token 'xref'" on reopen. Repair via Import (preserves
                // rotations) then PDFium, mirroring the save/reload path, instead of crashing.
                try
                {
                    _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                }
                catch (Exception undoOpenEx) when (IsXRefException(undoOpenEx))
                {
                    var fixedPath = App.MakeTempFile("undofixed");
                    if (!TryImportRepairToPath(tempPath, fixedPath)
                        && !TryPdfiumSaveWithZeroRotations(tempPath, fixedPath))
                        throw;
                    tempPath = fixedPath;
                    _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                }
                _currentFile = tempPath;
                _annotations.Clear();
                _renderDims.Clear();
                ClearSelection();
                MarkDirty(entry.WasDirty);
                RefreshPageList();
                if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                    PageList.SelectedIndex = selectedIdx;
                else if (PageList.Items.Count > 0)
                    PageList.SelectedIndex = 0;
                // Re-render the current view so the main page(s) reflect the restored document.
                // RefreshPageList only updates the sidebar, and re-selecting the same page does not
                // fire SelectionChanged, so grid/two-page tiles would otherwise stay stale.
                int reIdx = PageList.SelectedIndex;
                if (_viewMode == ViewMode.Continuous)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                        (Action)(() => SetupContinuousView(reIdx)));
                else
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                    {
                        RenderPage(_viewMode == ViewMode.Grid ? 0 : reIdx);
                        ReapplyGridOrFit();
                    }));
                SetStatus("Undid document change");
            }
        }

        // Re-renders annotations on every page that currently has a visible surface: each overlay
        // tracked by a multi-page view, plus the single-page canvas. RenderAllAnnotations re-adds
        // form fields, so forms survive the refresh.
        private void RenderAnnotationsOnAllVisiblePages()
        {
            if (_continuousCanvases.Count > 0)
                foreach (var p in _continuousCanvases.Keys.ToList())
                    RenderAllAnnotations(p);
            else if (PageList.SelectedIndex >= 0)
                RenderAllAnnotations(PageList.SelectedIndex);
        }

        // Context-menu "Clear Page Annotations": removes annotations on the current page only.
        private void ClearAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTextBox is not null) CommitActiveTextBox();
            int pageIdx = PageList.SelectedIndex;
            if (pageIdx < 0) return;
            if (_annotations.TryGetValue(pageIdx, out var list) && list.Count > 0)
            {
                // Snapshot this page so the clear is a single undo, then drop the annotations.
                var snap = new Dictionary<int, List<PageAnnotation>> { [pageIdx] = [.. list] };
                _undoStack.Push(new UndoEntry(UndoKind.ClearAnnotations, WasDirty: _isDirty, AnnotSnapshot: snap));
                _annotations.Remove(pageIdx);
                MarkDirty();
            }
            ClearSelection();
            // Redraw the page on whichever surface it lives on (overlay in multi-page views, the
            // single canvas otherwise) so the cleared page actually updates in continuous/grid mode.
            RenderAllAnnotations(pageIdx);
            SetStatus("Cleared annotations on this page");
        }

        // Toolbar "Clear All Annotations": removes annotations across the whole document in one undo.
        private void ClearAllAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) return;
            if (_activeTextBox is not null) CommitActiveTextBox();

            int total = _annotations.Values.Sum(l => l.Count);
            if (total == 0) { SetStatus("No annotations to clear"); return; }

            // Snapshot every page so a single Ctrl+Z restores the whole document's annotations.
            var snapshot = new Dictionary<int, List<PageAnnotation>>();
            foreach (var kv in _annotations)
                if (kv.Value.Count > 0) snapshot[kv.Key] = [.. kv.Value];
            _undoStack.Push(new UndoEntry(UndoKind.ClearAnnotations, WasDirty: _isDirty, AnnotSnapshot: snapshot));

            _annotations.Clear();
            ClearSelection();
            RenderAnnotationsOnAllVisiblePages();
            MarkDirty();
            SetStatus($"Cleared all annotations ({total})");
        }

        // Stamps a page number onto every page as a text annotation (so it renders, saves, and
        // flattens like any other annotation). One undo removes the whole batch.
        private void StampPageNumbers()
        {
            if (_doc is null) { SetStatus("Open a document first"); return; }

            var dlg = new StampNumbersDialog(this);
            if (dlg.ShowDialog() != true) return;

            int start    = dlg.StartNumber;
            string fmt   = dlg.Format;
            double ptSize = dlg.FontSizePt;
            int posH     = dlg.PosH;   // 0 left, 1 center, 2 right
            int posV     = dlg.PosV;   // 0 top, 2 bottom
            int n        = _doc.PageCount;
            bool wasDirty = _isDirty;
            double ppd   = VisualTreeHelper.GetDpi(this).PixelsPerDip;

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
                    Position  = new Point(x, y),
                    Content   = text,
                    FontSize  = fontCanvas
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

        // ============================================================
        // Dirty / unsaved-change tracking
        // ============================================================

        private void MarkDirty(bool dirty = true)
        {
            _isDirty = dirty;
            if (_saveAsBtnRef != null)
            {
                if (dirty)
                {
                    // Deeper orange = unsaved. The old #FFA500 washed out on the light theme's white
                    // toolbar; this reads on light and dark. A soft dark halo (ShadowDepth 0) outlines
                    // the glyph so it pops on light backgrounds and stays invisible on dark ones.
                    _saveAsBtnRef.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x73, 0x00));
                    _saveAsBtnRef.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Colors.Black, BlurRadius = 5, ShadowDepth = 0, Opacity = 0.55
                    };
                }
                else
                {
                    // AccentLogo is green (visible) in every theme and tracks theme switches
                    _saveAsBtnRef.SetResourceReference(Control.ForegroundProperty, "AccentLogo");
                    _saveAsBtnRef.Effect = null;
                }
            }
        }

        // ============================================================
        // Close file (Ctrl+W) — returns to drop-zone state
        // ============================================================

        private void CloseFile()
        {
            if (_doc is null) return;
            if (_isDirty)
            {
                var res = KillerDialog.Show(this,
                    Loc("Str_Dlg_UnsavedClose"),
                    "KillerPDF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }
            _doc.Close();
            _doc = null;
            _currentFile = null;
            App.RemoveSetting("LastFile");   // don't reopen a manually-closed file on next launch (Issue #75)
            _activeTextBox = null;   // cancel any in-progress typewriter edit before canvas clear
            RemoveTextEditHandles();
            _annotations.Clear();
            _undoStack.Clear();
            _renderDims.Clear();
            _formTextValues.Clear();
            _formCheckValues.Clear();
            _formFontSizes.Clear();
            _formRadioValues.Clear();
            _allSearchRects.Clear();
            _searchResultPages.Clear();
            _searchPageCursor = -1;
            _thumbCts?.Cancel();
            PageList.ItemsSource = null;
            if (FindName("PageImage") is System.Windows.Controls.Image img) img.Source = null;
            _annotationCanvas.Children.Clear();
            FileNameLabel.Text = "";
            DropZone.Visibility = Visibility.Visible;
            PopulateRecentFilesList();   // refresh the empty-state recent list
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

        private void CloseFile_Click(object sender, RoutedEventArgs e) => CloseTab(_active);

        // ============================================================
        // File toolbar handlers
        // ============================================================

        private void New_Click(object sender, RoutedEventArgs e) => NewDocument();

        private void NewDocument()
        {
            // A new blank document opens in its own tab; other open tabs keep their state, so
            // there's no need to prompt about unsaved changes here.
            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                var newDoc = new PdfDocument();
                newDoc.AddPage(); // one blank A4 page

                var tempPath = App.MakeTempFile("new");
                newDoc.Save(tempPath);
                newDoc.Close();

                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
                FinishOpenFile("Untitled.pdf", tempPath);
                SetStatus("New blank document");
                CaptureSessionState(_active!);
                SetTool(_currentTool);   // sync the tool UI to this (new) tab's tool
                RebuildTabStrip();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, $"Could not create new document:\n{ex.Message}",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Open PDF" };
            if (dlg.ShowDialog(this) == true) OpenInNewTab(dlg.FileName);
        }

        // Dropdown next to the Open button: the recent-files list.
        private void OpenRecent_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);

            var recents = App.GetRecentFiles();
            if (recents.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Menu_RecentNone"), IsEnabled = false });
            }
            else
            {
                foreach (var p in recents)
                {
                    string path = p;   // capture
                    var item = MakeMenuItem(System.IO.Path.GetFileName(path), (_, _) =>
                    {
                        if (System.IO.File.Exists(path)) OpenInNewTab(path);
                        else KillerDialog.Show(this, $"File not found:\n{path}", "KillerPDF",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                    item.ToolTip = path;

                    // Header = filename then a small X right after it (kept tight - no right whitespace).
                    var rmBtn = new Button
                    {
                        Content = "\ue711",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 9,
                        Width = 18, Height = 18,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0),
                        // No local Foreground - it would override the DangerCloseButton hover trigger.
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        Padding = new Thickness(0),
                        Style = (Style)FindResource("DangerCloseButton"),
                        ToolTip = "Remove from list"
                    };
                    rmBtn.Click += (_, ev) =>
                    {
                        ev.Handled = true;
                        App.RemoveRecentFile(path);
                        menu.Items.Remove(item);   // drop just this row in place - no rebuild, no blink
                        if (!menu.Items.OfType<MenuItem>().Any(mi => mi.Header is Grid))
                            menu.IsOpen = false;   // nothing left to show
                    };
                    // Filename (fills) + X right-aligned. Trim the MenuItem's default 40px right padding
                    // so the X sits near the edge instead of floating in whitespace.
                    // Negative right margin overlaps the template's empty InputGestureText column
                    // (it reserves ~24px), so the X lands near the real right edge instead of floating.
                    var hdr = new Grid { Margin = new Thickness(0, 0, -24, 0) };
                    hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var nameText = new TextBlock { Text = System.IO.Path.GetFileName(path), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                    Grid.SetColumn(nameText, 0);
                    Grid.SetColumn(rmBtn, 1);
                    hdr.Children.Add(nameText);
                    hdr.Children.Add(rmBtn);
                    item.Header = hdr;
                    item.Padding = new Thickness(20, 6, 8, 6);

                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_ClearList"), (_, _) => App.ClearRecentFiles()));
            }

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Fills the empty-state "Recent" list with clickable filenames (hidden when there are none).
        private void PopulateRecentFilesList()
        {
            if (RecentFilesList is null || RecentFilesBox is null) return;
            RecentFilesList.Items.Clear();
            var recents = App.GetRecentFiles();
            if (recents.Count == 0) { RecentFilesBox.Visibility = Visibility.Collapsed; return; }
            RecentFilesBox.Visibility = Visibility.Visible;
            var fam = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI");
            foreach (var p in recents)
            {
                string path = p;   // capture
                bool exists = System.IO.File.Exists(path);
                string dir = System.IO.Path.GetDirectoryName(path) ?? "";
                string dateStr = exists
                    ? $"{System.IO.File.GetLastWriteTime(path):MMM d, yyyy}"
                    : "missing";

                var name = new TextBlock
                {
                    Text         = System.IO.Path.GetFileName(path),
                    FontFamily   = fam,
                    FontSize     = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                // DynamicResource so the color tracks theme switches (FindResource would freeze
                // whatever theme was active when the list was built).
                name.SetResourceReference(TextBlock.ForegroundProperty, exists ? "TextPrimary" : "TextDim");

                // File path line (slightly brighter) sits above the date line (slightly dimmer).
                var pathTb = new TextBlock
                {
                    Text         = dir,
                    FontFamily   = fam,
                    FontSize     = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin       = new Thickness(0, 2, 0, 0)
                };
                pathTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

                var dateTb = new TextBlock
                {
                    Text         = dateStr,
                    FontFamily   = fam,
                    FontSize     = 11,
                    Opacity      = 0.6,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin       = new Thickness(0, 1, 0, 0)
                };
                dateTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

                var stack = new StackPanel();
                stack.Children.Add(name);
                stack.Children.Add(pathTb);
                stack.Children.Add(dateTb);

                // Per-row remove button: a small X that fades in on hover and drops just this
                // entry from the recents list (it does not touch the file on disk).
                var delIcon = new TextBlock
                {
                    Text              = "",   // close (X) glyph below set via code
                    FontFamily        = new FontFamily("Segoe MDL2 Assets"),
                    FontSize          = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                delIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                delIcon.Text = "";   // Segoe MDL2 ChromeClose (X)
                var del = new Border
                {
                    Width             = 22,
                    Height            = 22,
                    Background        = System.Windows.Media.Brushes.Transparent,
                    CornerRadius      = new CornerRadius(4),
                    Cursor            = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity           = 0,   // hidden until the row is hovered
                    Child             = delIcon,
                    ToolTip           = Loc("Str_Menu_RemoveFromRecents")
                };
                del.MouseEnter += (_, _) => { delIcon.SetResourceReference(TextBlock.ForegroundProperty, "DangerRed"); delIcon.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Colors.Black, BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.5 }; };
                del.MouseLeave += (_, _) => { delIcon.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary"); delIcon.Effect = null; };
                del.MouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true;   // don't open the file
                    App.RemoveRecentFile(path);
                    PopulateRecentFilesList();
                };

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(stack, 0);
                Grid.SetColumn(del, 1);
                rowGrid.Children.Add(stack);
                rowGrid.Children.Add(del);

                var row = new Border
                {
                    Background    = System.Windows.Media.Brushes.Transparent,
                    CornerRadius  = new CornerRadius(4),
                    Padding       = new Thickness(8, 6, 8, 6),
                    Margin        = new Thickness(0, 1, 0, 1),
                    Cursor        = Cursors.Hand,
                    Child         = rowGrid,
                    ToolTip       = path
                };
                row.MouseEnter += (_, _) => { row.Background = (SolidColorBrush)FindResource("BgHover"); del.Opacity = 1; };
                row.MouseLeave += (_, _) => { row.Background = System.Windows.Media.Brushes.Transparent; del.Opacity = 0; };
                row.MouseLeftButtonDown += (_, ev) =>
                {
                    ev.Handled = true;   // don't bubble to the DropZone "click to browse" handler
                    if (System.IO.File.Exists(path)) OpenInNewTab(path);
                    else KillerDialog.Show(this, $"File not found:\n{path}", "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                };
                RecentFilesList.Items.Add(row);
            }
        }

        // Dropdown next to the Save button: explicit Save / Save As.
        private void SaveMenu_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);

            if (_doc is null)
            {
                menu.Items.Add(new MenuItem { Header = Loc("Str_Menu_SaveNothing"), IsEnabled = false });
            }
            else
            {
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_Save"), (_, _) => SaveInPlace(), "Ctrl+S"));
                menu.Items.Add(MakeMenuItem(Loc("Str_Menu_SaveAs"), (s2, e2) => SaveAs_Click(s2, e2), "Ctrl+Shift+S"));
                menu.Items.Add(new Separator());
                // TODO localize "Digital Signature..." once the dialog is finalized.
                menu.Items.Add(MakeMenuItem("Digital Signature...", (_, _) => OpenSignDialog()));
            }

            menu.PlacementTarget = (UIElement)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // Cryptographic certificate signing (the real digital signature, not the drawn stamp tool).
        private void OpenSignDialog()
        {
            if (_doc is null || string.IsNullOrEmpty(_currentFile))
            {
                KillerDialog.Show(this, "Open a PDF first.");
                return;
            }
            new SignDocumentDialog(this, _currentFile!).ShowDialog();
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var dlg = new OpenFileDialog { Filter = "PDF files|*.pdf", Title = "Select PDF to merge", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                foreach (var file in dlg.FileNames)
                {
                    int pageOffset = doc.PageCount;

                    // Open twice: Import mode for AddPage, ReadOnly for catalog access.
                    using var srcRead = PdfReader.Open(file, PdfDocumentOpenMode.ReadOnly);
                    var namedDestMap = BuildNamedDestMap(srcRead);

                    using var src = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    for (int i = 0; i < src.PageCount; i++)
                        doc.AddPage(src.Pages[i]);

                    // Rewrite named-destination links in the newly added pages so they
                    // resolve correctly after the catalog is not imported.
                    if (namedDestMap.Count > 0)
                        RewriteNamedDestLinks(doc, pageOffset, namedDestMap);
                }
                SaveTempAndReload();
                SetStatus($"Merged {dlg.FileNames.Length} file(s) - {_doc?.PageCount} total pages");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Merge failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Builds a map of named destination string → 0-based page index from a source document's
        /// /Dests dictionary and /Names /Dests name tree.
        /// </summary>
        private Dictionary<string, int> BuildNamedDestMap(PdfDocument src)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                var catalog = src.Internals.Catalog;

                // Legacy flat /Dests dictionary
                var destsDict = catalog.Elements.GetDictionary("/Dests");
                if (destsDict != null)
                {
                    foreach (var key in destsDict.Elements.Keys)
                    {
                        PdfItem? val = DerefItem(destsDict.Elements[key] ?? new PdfInteger(-1));
                        int? idx = ResolveDestPageIndexInDoc(src, val);
                        if (idx.HasValue) map[key.TrimStart('/')] = idx.Value;
                    }
                }

                // Modern /Names /Dests name tree
                var namesDict = catalog.Elements.GetDictionary("/Names");
                var destTree  = namesDict?.Elements.GetDictionary("/Dests");
                if (destTree != null)
                    WalkNameTree(src, destTree, map);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"BuildNamedDestMap: {ex}"); }
            return map;
        }

        private void WalkNameTree(PdfDocument src, PdfDictionary node, Dictionary<string, int> map)
        {
            var namesArr = node.Elements.GetArray("/Names");
            if (namesArr != null)
            {
                for (int i = 0; i + 1 < namesArr.Elements.Count; i += 2)
                {
                    var keyItem = namesArr.Elements[i];
                    string key  = keyItem is PdfString ks ? ks.Value : keyItem?.ToString()?.TrimStart('/') ?? "";
                    if (string.IsNullOrEmpty(key)) continue;
                    PdfItem? val = DerefItem(namesArr.Elements[i + 1]);
                    int? idx = ResolveDestPageIndexInDoc(src, val);
                    if (idx.HasValue) map[key] = idx.Value;
                }
            }

            var kids = node.Elements.GetArray("/Kids");
            if (kids != null)
            {
                for (int i = 0; i < kids.Elements.Count; i++)
                {
                    if (DerefItem(kids.Elements[i]) is PdfDictionary kid)
                        WalkNameTree(src, kid, map);
                }
            }
        }

        /// <summary>
        /// Resolves a destination value (PdfArray or PdfDictionary with /D) to a page index
        /// within the given source document by matching the page object number.
        /// </summary>
        private static int? ResolveDestPageIndexInDoc(PdfDocument src, PdfItem? val)
        {
            PdfArray? arr = val as PdfArray;
            if (arr is null && val is PdfDictionary vd)
                arr = vd.Elements.GetArray("/D");
            if (arr is null || arr.Elements.Count == 0) return null;

            var first = arr.Elements[0];
            int objNum = GetObjectNumber(first);
            if (objNum > 0)
            {
                for (int i = 0; i < src.PageCount; i++)
                {
                    var pgRef = src.Pages[i].Reference;
                    if (pgRef != null && pgRef.ObjectNumber == objNum) return i;
                }
            }
            else if (first is PdfInteger pi && pi.Value >= 0 && pi.Value < src.PageCount)
            {
                return pi.Value;
            }
            return null;
        }

        /// <summary>
        /// Walks all link annotations in pages [pageOffset, doc.PageCount) and rewrites any
        /// named-destination /D values to explicit [pageRef /Fit] arrays using the merged
        /// document's page references. This is needed because PdfSharpCore's import does not
        /// copy the source document's /Names /Dests catalog entries.
        /// </summary>
        private static void RewriteNamedDestLinks(PdfDocument doc, int pageOffset,
            Dictionary<string, int> namedDestMap)
        {
            for (int pi = pageOffset; pi < doc.PageCount; pi++)
            {
                try
                {
                    var page    = doc.Pages[pi];
                    var annotsArr = page.Elements.GetArray("/Annots");
                    if (annotsArr is null) continue;

                    for (int ai = 0; ai < annotsArr.Elements.Count; ai++)
                    {
                        PdfItem? elem = annotsArr.Elements[ai];
                        PdfDictionary? ann = elem as PdfDictionary
                            ?? (DerefItemStatic(elem) as PdfDictionary);
                        if (ann is null) continue;

                        var subtype = ann.Elements["/Subtype"]?.ToString() ?? "";
                        if (!subtype.Contains("Link")) continue;

                        // Check /A /D (GoTo action)
                        var actionDict = ann.Elements.GetDictionary("/A");
                        if (actionDict != null)
                        {
                            var s = actionDict.Elements["/S"]?.ToString() ?? "";
                            if (s.Contains("GoTo"))
                            {
                                var destItem = actionDict.Elements["/D"];
                                string? name = ExtractDestName(destItem);
                                if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                                {
                                    int targetIdx = pageOffset + srcIdx;
                                    if (targetIdx < doc.PageCount)
                                        actionDict.Elements["/D"] = MakeExplicitDest(doc, targetIdx);
                                }
                            }
                        }
                        else
                        {
                            // Bare /Dest on annotation
                            var destItem = ann.Elements["/Dest"];
                            string? name = ExtractDestName(destItem);
                            if (name != null && namedDestMap.TryGetValue(name, out int srcIdx))
                            {
                                int targetIdx = pageOffset + srcIdx;
                                if (targetIdx < doc.PageCount)
                                    ann.Elements["/Dest"] = MakeExplicitDest(doc, targetIdx);
                            }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RewriteNamedDestLinks p{pi}: {ex}"); }
            }
        }

        private static string? ExtractDestName(PdfItem? item)
        {
            if (item is null) return null;
            if (item is PdfString ps) return ps.Value;
            if (item is PdfName   pn) return pn.Value.TrimStart('/');
            return null;
        }

        private static PdfArray MakeExplicitDest(PdfDocument doc, int pageIndex)
        {
            var arr = new PdfArray(doc);
            arr.Elements.Add(doc.Pages[pageIndex].Reference);
            arr.Elements.Add(new PdfName("/Fit"));
            return arr;
        }

        // Static version of DerefItem for use in static helpers.
        private static PdfItem DerefItemStatic(PdfItem item)
        {
            var valueProp = item.GetType().GetProperty("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueProp?.GetValue(item) is PdfObject resolved) return resolved;
            return item;
        }

        private void Split_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var currentFile = _currentFile;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, "Select pages to extract."); return; }
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save extracted pages as",
                                           CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                using var importDoc = PdfReader.Open(currentFile, PdfDocumentOpenMode.Import);
                var newDoc = new PdfDocument();
                foreach (var idx in indices.OrderBy(i => i))
                    newDoc.AddPage(importDoc.Pages[idx]);
                newDoc.Save(dlg.FileName);
                SetStatus(string.Format(Loc("Str_Extracted"), indices.Count, System.IO.Path.GetFileName(dlg.FileName)));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Split failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            var selected = PageList.SelectedItems;
            if (selected.Count == 0) { KillerDialog.Show(this, "Select pages to delete."); return; }
            var result = KillerDialog.Show(this, $"Delete {selected.Count} {(selected.Count == 1 ? "page" : "pages")}?", "KillerPDF",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                var indices = new List<int>();
                foreach (PageThumbnailVm vm in selected) indices.Add(vm.PageIndex);
                foreach (var idx in indices.OrderByDescending(i => i))
                    doc.Pages.RemoveAt(idx);
                SaveTempAndReload();
                SetStatus(string.Format(Loc("Str_Deleted"), indices.Count, _doc?.PageCount));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Delete failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InsertBlankPage_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            var doc = _doc;
            int insertAfter = PageList.SelectedIndex >= 0 ? PageList.SelectedIndex : doc.PageCount - 1;
            try
            {
                var blank = new PdfPage { Width = XUnit.FromPoint(595), Height = XUnit.FromPoint(842) };
                doc.Pages.Insert(insertAfter + 1, blank);
                SaveTempAndReload();
                PageList.SelectedIndex = insertAfter + 1;
                SetStatus($"Inserted blank page at position {insertAfter + 2}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Insert failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex <= 0) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx - 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx - 1;
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || PageList.SelectedIndex < 0 || PageList.SelectedIndex >= _doc.PageCount - 1) return;
            var doc = _doc;
            int idx = PageList.SelectedIndex;
            var page = doc.Pages[idx];
            doc.Pages.RemoveAt(idx);
            doc.Pages.Insert(idx + 1, page);
            SaveTempAndReload();
            PageList.SelectedIndex = idx + 1;
        }

        private void SaveInPlace()
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            // Save back to the user's real file. After a page edit (crop/rotate) _currentFile is a
            // temp working copy, so the real path is kept in _originalFile. If there is no real path
            // (e.g. a repaired temp-backed open), fall back to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(this, new RoutedEventArgs()); return; }
            CommitActiveTextBox();
            string saveTarget = _originalFile!;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                WriteFormValuesToDocument();
                // Always strip link annotation borders regardless of user annotation count
                // so mailto/URI links don't appear as strikethrough lines in other viewers.
                StripLinkAnnotationBorders(_doc);

                if (hasAnnotations)
                {
                    // Save a clean copy of the doc (without burned annotations), burn
                    // annotations into the real file, then restore the in-memory doc
                    // from the clean copy so future saves don't double-burn.
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    DrawAnnotationsOnDocument();
                    _doc.Save(saveTarget);
                    _doc.Close();
                    try
                    {
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    catch (Exception saveOpenEx) when (IsXRefException(saveOpenEx))
                    {
                        var fixedPath = App.MakeTempFile("savefixed");
                        if (!TryImportRepairToPath(tempClean, fixedPath)
                            && !TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                            throw;
                        tempClean = fixedPath;
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    _currentFile = tempClean;
                }
                else
                {
                    _doc.Save(saveTarget);
                }

                MarkDirty(false);
                SetStatus($"Saved - {System.IO.Path.GetFileName(saveTarget)}");
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Save failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            // No real path yet (repaired temp-backed open) -> go straight to Save As.
            if (string.IsNullOrEmpty(_originalFile)) { SaveAs_Click(sender, e); return; }
            var name = System.IO.Path.GetFileName(_originalFile);
            var choice = KillerDialog.Show(this, $"Overwrite {name}?", "Save",
                                           MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Yes)      SaveInPlace();
            else if (choice == MessageBoxResult.No)  SaveAs_Click(sender, e);
            // Cancel or closed: do nothing.
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save PDF as",
                                           CheckFileExists = false, CheckPathExists = true };
            string? seed = _originalFile ?? _currentFile;
            if (!string.IsNullOrEmpty(seed))
            {
                dlg.FileName = System.IO.Path.GetFileName(seed);
                var seedDir = System.IO.Path.GetDirectoryName(_originalFile ?? "");
                if (!string.IsNullOrEmpty(seedDir) && System.IO.Directory.Exists(seedDir))
                    dlg.InitialDirectory = seedDir;
            }
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
                WriteFormValuesToDocument();
                // Always strip link annotation borders regardless of user annotation count.
                StripLinkAnnotationBorders(_doc);

                if (hasAnnotations)
                {
                    var tempClean = App.MakeTempFile("clean");
                    _doc.Save(tempClean);
                    DrawAnnotationsOnDocument();
                    _doc.Save(dlg.FileName);
                    _doc.Close();
                    try
                    {
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    catch (Exception saveOpenEx) when (IsXRefException(saveOpenEx))
                    {
                        var fixedPath = App.MakeTempFile("savefixed");
                        if (!TryImportRepairToPath(tempClean, fixedPath)
                            && !TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                            throw;
                        tempClean = fixedPath;
                        _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                    }
                    _currentFile = tempClean;
                    _originalFile = dlg.FileName;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    MarkDirty(false);
                    SetStatus($"Saved with annotations to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
                else
                {
                    _doc.Save(dlg.FileName);
                    _originalFile = dlg.FileName;
                    FileNameLabel.Text = System.IO.Path.GetFileName(dlg.FileName);
                    MarkDirty(false);
                    SetStatus($"Saved to {System.IO.Path.GetFileName(dlg.FileName)}");
                }
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, $"Save failed:\n{ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SaveFlattened_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();
            var dlg = new SaveFileDialog { Filter = "PDF files|*.pdf", Title = "Save Flattened PDF",
                                           CheckFileExists = false, CheckPathExists = true };
            if (dlg.ShowDialog(this) != true) return;

            // Burn any pending annotations into a temp source for rasterization
            // (must happen on UI thread before we go async)
            string sourcePath;
            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            if (hasAnnotations)
            {
                var tempClean  = App.MakeTempFile("clean");
                var tempBurned = App.MakeTempFile("burned");
                _doc.Save(tempClean);
                DrawAnnotationsOnDocument();
                _doc.Save(tempBurned);
                _doc.Close();
                try
                {
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                catch (Exception saveOpenEx) when (IsXRefException(saveOpenEx))
                {
                    var fixedPath = App.MakeTempFile("savefixed");
                    if (!TryImportRepairToPath(tempClean, fixedPath)
                        && !TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                        throw;
                    tempClean = fixedPath;
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                _currentFile = tempClean;
                sourcePath = tempBurned;
            }
            else
            {
                var temp = App.MakeTempFile("src");
                _doc.Save(temp);
                sourcePath = temp;
            }

            int pageCount = _doc.PageCount;

            // Snapshot per-page dimensions (CropBox-aware) before going off-thread
            var pageDims = new (double widthPt, double heightPt)[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                var p = _doc.Pages[i];
                pageDims[i] = (p.Width.Point, p.Height.Point);
            }

            // Show a progress overlay so the user knows we're working
            var overlay = ShowFlattenProgress(pageCount);
            string outputPath = dlg.FileName;

            try
            {
                // Rasterize on a background thread — keeps the UI responsive
                await Task.Run(() =>
                {
                    // Rasterize pages across CPU cores. Docnet/PDFium is not thread-safe, so the
                    // pdfium render is serialized behind a lock; the PNG encode (GDI+) runs in
                    // parallel. Pages are assembled into the PDF afterwards, in order.
                    //
                    // The source document is opened ONCE here. The old code re-opened it inside
                    // the per-page loop, re-parsing the whole file on every page (O(pages) full
                    // document parses) - the dominant cost on large files. A single scaling
                    // factor renders each page at its own size at 150 DPI (150/72), so the doc
                    // no longer needs reopening to apply per-page pixel dimensions.
                    var pngPages = new byte[pageCount][];
                    var docGate  = new object();
                    int done     = 0;
                    var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
                    using var flattenReader = DocLib.Instance.GetDocReader(sourcePath, new PageDimensions(150.0 / 72.0));
                    Parallel.For(0, pageCount, po, i =>
                    {
                        byte[] bgra; int rw, rh;
                        lock (docGate)
                        {
                            using var pr = flattenReader.GetPageReader(i);
                            bgra = pr.GetImage();
                            rw   = pr.GetPageWidth();
                            rh   = pr.GetPageHeight();
                        }
                        // Encode BGRA to PNG (GDI+) outside the lock so it parallelizes.
                        pngPages[i] = RenderToPng(bgra, rw, rh);

                        int n = System.Threading.Interlocked.Increment(ref done);
                        Dispatcher.BeginInvoke(new Action(() => UpdateFlattenProgress(overlay, n, pageCount)));
                    });

                    // Assemble the output PDF in page order (PdfSharp is single-threaded).
                    var outDoc = new PdfDocument();
                    try
                    {
                        for (int i = 0; i < pageCount; i++)
                        {
                            var newPage = outDoc.AddPage();
                            newPage.Width  = XUnit.FromPoint(pageDims[i].widthPt);
                            newPage.Height = XUnit.FromPoint(pageDims[i].heightPt);
                            using var xi  = XImage.FromStream(() => new MemoryStream(pngPages[i]));
                            using var gfx = XGraphics.FromPdfPage(newPage);
                            gfx.DrawImage(xi, 0, 0, newPage.Width.Point, newPage.Height.Point);
                        }
                        outDoc.Save(outputPath);
                    }
                    finally
                    {
                        outDoc.Dispose();
                    }
                });

                MarkDirty(false);
                SetStatus($"Flattened PDF saved to {System.IO.Path.GetFileName(outputPath)}");
            }
            catch (Exception ex)
            {
                try { KillerDialog.Show(this, $"Flatten failed:\n{ex.GetType().Name}: {ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { /* dialog failed; overlay still removed in finally */ }
            }
            finally
            {
                try { HideFlattenProgress(overlay); } catch { /* ensure overlay never leaks */ }
            }
        }

        // ---- flatten progress overlay helpers ----

        private Border ShowFlattenProgress(int pageCount, string verb = "Flattening")
        {
            var progressText = new TextBlock
            {
                Text       = $"{verb} page 0 of {pageCount}...",
                Foreground = Brushes.White,
                FontSize   = 14,
                Tag        = verb   // stored so UpdateFlattenProgress can read it
            };
            var panel = new StackPanel
            {
                Orientation         = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            panel.Children.Add(progressText);

            var overlay = new Border
            {
                Background        = new SolidColorBrush(Color.FromArgb(200, 0x1a, 0x1a, 0x1a)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment   = VerticalAlignment.Stretch,
                Child             = panel,
                Tag               = "FlattenOverlay"
            };
            Panel.SetZIndex(overlay, 999);

            // Attach to the root grid
            if (Content is Grid rootGrid)
                rootGrid.Children.Add(overlay);

            return overlay;
        }

        private static void UpdateFlattenProgress(Border overlay, int current, int total)
        {
            if (overlay.Child is StackPanel panel)
                foreach (var child in panel.Children)
                    if (child is TextBlock tb && tb.Tag is string verb)
                        tb.Text = $"{verb} page {current} of {total}...";
        }

        private void HideFlattenProgress(Border overlay)
        {
            if (Content is Grid rootGrid)
                rootGrid.Children.Remove(overlay);
        }

        // ---- generic busy overlay (indeterminate spinner) for blocking background work ----

        /// <summary>
        /// Dims the window and shows a spinning ring plus a message while a background task runs.
        /// Returned Border is passed to HideBusyOverlay when the work completes.
        /// </summary>
        private Border ShowBusyOverlay(string message)
        {
            var spinner = new System.Windows.Shapes.Ellipse
            {
                Width = 34, Height = 34,
                Stroke = AccentBrush(),
                StrokeThickness = 3,
                StrokeDashArray = [5.5, 3.5], // dashed ring reads as "spinning"
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var rot = new RotateTransform();
            spinner.RenderTransform = rot;
            rot.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9)))
                { RepeatBehavior = RepeatBehavior.Forever });

            var text = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            panel.Children.Add(spinner);
            panel.Children.Add(text);

            var overlay = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(190, 0x12, 0x12, 0x12)),
                Child = panel
            };
            Panel.SetZIndex(overlay, 10050); // above the Settings/Shortcuts/About overlays

            // Cover the whole window for a uniform dim, but let the user drag the window by pressing
            // anywhere on the overlay - so a long operation (e.g. repair) doesn't trap the window in place.
            overlay.Cursor = Cursors.SizeAll;
            overlay.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
            };
            if (SettingsOverlay?.Parent is Grid host)
            {
                if (host.RowDefinitions.Count > 0) Grid.SetRowSpan(overlay, host.RowDefinitions.Count);
                host.Children.Add(overlay);
            }
            else
            {
                RootClipGrid?.Children.Add(overlay);
            }
            return overlay;
        }

        private static void HideBusyOverlay(Border overlay)
            => (overlay.Parent as Panel)?.Children.Remove(overlay);

        /// <summary>
        /// Encodes raw BGRA pixel data from pdfium to PNG without touching the UI thread.
        /// GDI+ Format32bppArgb is BGRA in memory — matches pdfium output exactly.
        /// </summary>
        private static byte[] RenderToPng(byte[] bgra, int width, int height)
        {
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                using var bmp = new System.Drawing.Bitmap(
                    width, height, width * 4,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb,
                    pin.AddrOfPinnedObject());
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            finally { pin.Free(); }
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, "Open a PDF first."); return; }
            CommitActiveTextBox();

            // Burn pending annotations into a temp copy on the UI thread before going off-thread
            bool hasAnnotations = _annotations.Values.Any(list => list.Count > 0);
            string printPath;
            string? tempFlattened = null;
            if (hasAnnotations)
            {
                var tempClean = App.MakeTempFile("clean");
                _doc.Save(tempClean);
                // The source PDF may have opened read-only (owner-password or non-standard-xref
                // fallbacks), and XGraphics can't draw on a non-modifiable doc - that crashed the
                // print flow. Reopen the clean snapshot in Modify and burn the annotations onto that
                // throwaway copy; the live doc is reopened clean again below, so the burn never
                // persists in the editing session.
                _doc.Close();
                try
                {
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                catch (Exception printOpenEx) when (IsXRefException(printOpenEx))
                {
                    // PdfSharpCore can write a snapshot whose xref offset confuses its own reader
                    // ("Unexpected token 'xref'"). Repair via Import then PDFium, same as save/undo,
                    // instead of crashing the print flow.
                    var fixedPath = App.MakeTempFile("printfixed");
                    if (!TryImportRepairToPath(tempClean, fixedPath)
                        && !TryPdfiumSaveWithZeroRotations(tempClean, fixedPath))
                        throw;
                    tempClean = fixedPath;
                    _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                }
                DrawAnnotationsOnDocument();
                printPath = App.MakeTempFile("print");
                _doc.Save(printPath);
                tempFlattened = printPath;
                // Reopen a fresh clean copy so the live doc keeps annotations as editable overlays.
                _doc.Close();
                _doc = PdfReader.Open(tempClean, PdfDocumentOpenMode.Modify);
                _currentFile = tempClean;
            }
            else
            {
                printPath = _currentFile;
            }

            int pageCount = _doc.PageCount;

            // Each page's true physical size in DIPs (96/inch) so the dialog can offer an exact
            // "actual size" / custom scale. Computed on the UI thread (PdfSharp isn't thread-safe).
            var pageDipW = new double[pageCount];
            var pageDipH = new double[pageCount];
            for (int i = 0; i < pageCount; i++)
            {
                double pw = _doc.Pages[i].Width.Point;
                double ph = _doc.Pages[i].Height.Point;
                if (_pageRotations.TryGetValue(i, out int rot) && (rot == 90 || rot == 270))
                    (pw, ph) = (ph, pw);
                pageDipW[i] = pw * 96.0 / 72.0;
                pageDipH[i] = ph * 96.0 / 72.0;
            }

            // Open the preview window immediately. Pages rasterize on a background thread and
            // stream in via SetRenderedPage, so the window appears at once and the app stays
            // responsive on large files. WPF's OS PrintDialog can't show a preview, so KillerPDF
            // renders it and drives printing itself.
            var preview = new PrintPreviewWindow(this, pageCount, pageDipW, pageDipH);
            string  renderPath = printPath;
            string? cleanup    = tempFlattened;

            _ = Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(renderPath, new PageDimensions(1536, 1536));
                    for (int i = 0; i < pageCount; i++)
                    {
                        if (preview.Cancelled) return;
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        byte[] png = RenderToPng(pr.GetImage(), w, h);
                        BitmapSource src;
                        using (var ms = new MemoryStream(png))
                            src = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        src.Freeze();   // frozen so it can cross back to the UI thread
                        int ci = i;
                        try { preview.Dispatcher.Invoke(() => preview.SetRenderedPage(ci, src, w, h)); }
                        catch { return; }   // window closed mid-render
                    }
                    if (!preview.Cancelled)
                        try { preview.Dispatcher.Invoke(preview.FinishLoading); } catch { }
                }
                catch (Exception ex)
                {
                    try { preview.Dispatcher.Invoke(() => preview.LoadFailed(ex.Message)); } catch { }
                }
                finally
                {
                    if (cleanup != null) try { System.IO.File.Delete(cleanup); } catch { }
                }
            });

            try
            {
                if (preview.ShowDialog() == true)
                    SetStatus(string.Format(Loc("Str_Printed"), preview.PrintedPageCount));
            }
            catch (Exception ex)
            {
                try { KillerDialog.Show(this, $"Print failed:\n{ex.GetType().Name}: {ex.Message}", "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error); }
                catch { }
            }
        }

        // ============================================================
        // Save annotations to PDF
        // ============================================================

        private void DrawAnnotationsOnDocument()
        {
            if (_doc is null) return;

            // Strip link annotation borders so they don't render as colored rectangles
            // (e.g. strikethrough-like lines) in other PDF viewers.
            StripLinkAnnotationBorders(_doc);

            foreach (var kvp in _annotations)
            {
                int pageIdx = kvp.Key;
                var annots = kvp.Value;
                if (annots.Count == 0 || pageIdx >= _doc.PageCount) continue;
                if (!_renderDims.ContainsKey(pageIdx)) continue;

                var page = _doc.Pages[pageIdx];
                var (renderW, renderH) = _renderDims[pageIdx];
                double sx = page.Width.Point / renderW;
                double sy = page.Height.Point / renderH;

                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                foreach (var annot in annots)
                {
                    switch (annot)
                    {
                        case TextAnnotation ta:
                        {
                            double tboxX = ta.Position.X * sx;
                            double tboxY = ta.Position.Y * sy;
                            double tboxW = ta.Width * sx;
                            double tboxH = ta.Height * sy;
                            // Background fill (whiteout) first, behind the text.
                            if (ta.HasFill)
                            {
                                var fc = ta.GetFill();
                                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(fc.A, fc.R, fc.G, fc.B)),
                                    tboxX, tboxY, Math.Max(1, tboxW), Math.Max(1, tboxH));
                            }
                            var font = new XFont("Segoe UI", ta.FontSize * sy);
                            var taColor = ta.GetColor();
                            var taBrush = new XSolidBrush(XColor.FromArgb(taColor.A, taColor.R, taColor.G, taColor.B));
                            // Wrap inside the box, matching the on-screen TextWrapping=Wrap. The 2px editor
                            // padding is scaled into the layout rect so wrap points line up with the canvas.
                            double padX = 2 * sx, padY = 2 * sy;
                            var layoutRect = new XRect(tboxX + padX, tboxY + padY,
                                                       Math.Max(1, tboxW - 2 * padX), Math.Max(1, tboxH));
                            var tf = new PdfSharpCore.Drawing.Layout.XTextFormatter(gfx);
                            tf.DrawString(ta.Content, font, taBrush, layoutRect);
                            break;
                        }

                        case HighlightAnnotation ha:
                            var hc = ha.GetColor();
                            var hBrush = new XSolidBrush(XColor.FromArgb(hc.A, hc.R, hc.G, hc.B));
                            var hdr = ha.DrawRect();
                            gfx.DrawRectangle(hBrush,
                                hdr.X * sx, hdr.Y * sy,
                                hdr.Width * sx, hdr.Height * sy);
                            break;

                        case InkAnnotation ia:
                            if (ia.Points.Count < 2) break;
                            var ic = ia.GetColor();
                            var pen = new XPen(XColor.FromArgb(ic.A, ic.R, ic.G, ic.B), ia.StrokeWidth * sx)
                            {
                                LineJoin = XLineJoin.Round,
                                LineCap = XLineCap.Round
                            };
                            for (int i = 0; i < ia.Points.Count - 1; i++)
                            {
                                gfx.DrawLine(pen,
                                    ia.Points[i].X * sx, ia.Points[i].Y * sy,
                                    ia.Points[i + 1].X * sx, ia.Points[i + 1].Y * sy);
                            }
                            break;

                        case SignatureAnnotation sa:
                            if (sa.ImageData is not null)
                            {
                                try
                                {
                                    var imgBytes = Convert.FromBase64String(sa.ImageData);
                                    var xImg = XImage.FromStream(() => new System.IO.MemoryStream(imgBytes));
                                    double imgX = sa.Position.X * sx;
                                    double imgY = sa.Position.Y * sy;
                                    double imgW = sa.SourceWidth * sa.Scale * sx;
                                    double imgH = sa.SourceHeight * sa.Scale * sy;
                                    gfx.DrawImage(xImg, imgX, imgY, imgW, imgH);
                                }
                                catch { /* skip broken image */ }
                            }
                            else
                            {
                                var sigPen = new XPen(XColors.Black, sa.StrokeWidth * sa.Scale * sx)
                                {
                                    LineJoin = XLineJoin.Round,
                                    LineCap = XLineCap.Round
                                };
                                foreach (var stroke in sa.Strokes)
                                {
                                    for (int i = 0; i < stroke.Count - 1; i++)
                                    {
                                        double x1 = (sa.Position.X + stroke[i].X * sa.Scale) * sx;
                                        double y1 = (sa.Position.Y + stroke[i].Y * sa.Scale) * sy;
                                        double x2 = (sa.Position.X + stroke[i + 1].X * sa.Scale) * sx;
                                        double y2 = (sa.Position.Y + stroke[i + 1].Y * sa.Scale) * sy;
                                        gfx.DrawLine(sigPen, x1, y1, x2, y2);
                                    }
                                }
                            }
                            break;

                        case ImageAnnotation ia:
                            try
                            {
                                var iaBytes = Convert.FromBase64String(ia.ImageData);
                                var xia = XImage.FromStream(() => new System.IO.MemoryStream(iaBytes));
                                double iaX = ia.Position.X * sx;
                                double iaY = ia.Position.Y * sy;
                                double iaW = ia.SourceWidth * ia.Scale * sx;
                                double iaH = ia.SourceHeight * ia.Scale * sy;
                                gfx.DrawImage(xia, iaX, iaY, iaW, iaH);
                            }
                            catch { /* skip broken image */ }
                            break;
                    }
                }
            }
        }

        // ============================================================
        // Bitmap rotation helper
        // ============================================================

        /// <summary>
        /// Rotates a raw BGRA (4 bytes/pixel) bitmap clockwise by degrees.
        /// Used because Docnet's FPDF_RenderPageBitmapWithMatrix uses a pure-scaling
        /// matrix, so PDFium renders the page in its MediaBox orientation (no rotation).
        /// We strip /Rotate from the temp file so content is never clipped, then rotate
        /// the pixel buffer here to match the intended visual orientation.
        /// </summary>
        internal static (byte[] bytes, int w, int h) RotateBitmapStatic(byte[] src, int w, int h, int degrees)
            => RotateBitmap(src, w, h, degrees);

        private static (byte[] bytes, int w, int h) RotateBitmap(byte[] src, int w, int h, int degrees)
        {
            degrees = ((degrees % 360) + 360) % 360;
            if (degrees == 0) return (src, w, h);
            int newW = (degrees == 90 || degrees == 270) ? h : w;
            int newH = (degrees == 90 || degrees == 270) ? w : h;
            byte[] dst = new byte[newW * newH * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int srcIdx = (y * w + x) * 4;
                    int dstX, dstY;
                    switch (degrees)
                    {
                        case 90:  dstX = h - 1 - y; dstY = x;         break; // CW
                        case 180: dstX = w - 1 - x; dstY = h - 1 - y; break;
                        default:  dstX = y;          dstY = w - 1 - x; break; // 270 CW
                    }
                    int dstIdx = (dstY * newW + dstX) * 4;
                    dst[dstIdx]     = src[srcIdx];
                    dst[dstIdx + 1] = src[srcIdx + 1];
                    dst[dstIdx + 2] = src[srcIdx + 2];
                    dst[dstIdx + 3] = src[srcIdx + 3];
                }
            }
            return (dst, newW, newH);
        }

        // ============================================================
        // Temp save/reload
        // ============================================================

        private void SaveTempAndReload(bool keepAnnotations = false)
        {
            if (_doc is null || _currentFile is null) return;
            // Overlay annotations are unsaved, still-editable user work. Callers that don't change
            // page identity (crop) pass keepAnnotations:true so annotations on other pages survive
            // the reload and stay selectable/movable; they are re-rendered after the doc reopens.
            if (!keepAnnotations) _annotations.Clear();
            _renderDims.Clear();
            ClearSelection();
            MarkDirty();
            var doc = _doc;
            int selectedIdx = PageList.SelectedIndex;

            // Capture page rotations, then strip them from the document before saving.
            // Docnet uses FPDF_GetPageWidth/Height (MediaBox, no rotation) to size the bitmap,
            // then renders with PDFium's page CTM which *does* include /Rotate.  For 90°/270°
            // the rendered landscape content overflows the portrait-sized bitmap and gets clipped.
            // Stripping /Rotate to 0 before saving means Docnet renders clean unrotated content
            // that fits the bitmap; RotateBitmap is applied in each render path instead.
            _pageRotations.Clear();
            for (int i = 0; i < doc.PageCount; i++)
            {
                int rot = ((doc.Pages[i].Rotate % 360) + 360) % 360;
                _pageRotations[i] = rot;
                doc.Pages[i].Rotate = 0;
            }

            var tempPath = App.MakeTempFile("temp");
            try
            {
                doc.Save(tempPath);
                doc.Close();
            }
            catch (Exception saveEx) when (IsXRefException(saveEx))
            {
                // PdfSharpCore fails to re-save encrypted PDFs (e.g. owner-restricted RC4 files)
                // because it encounters cross-reference tokens while serialising dirty objects.
                // Primary fallback: use PDFium (already initialised for the page preview) to
                // load the source, strip all /Rotate values, remove encryption, and save.
                // Secondary fallback: PdfSharpCore Import mode (works on some non-encrypted xref
                // issues but fails on encrypted files; kept as a last resort).
                doc.Close();
                _doc = null;
                if (!TryPdfiumSaveWithZeroRotations(_currentFile!, tempPath) &&
                    !TryImportRepairToPath(_currentFile!, tempPath, stripRotations: true))
                    throw; // re-throw original if both fallbacks fail
            }
            // PdfSharpCore sometimes saves a file where one object's xref offset points at the
            // xref table itself (object N offset = xref table position). When PdfSharp then tries
            // to re-open that file in Modify mode it seeks to the xref table, reads the keyword
            // "xref" as a token in an object context, and throws "Unexpected token 'xref'".
            // Fix: catch the reopen failure, pipe the saved file through PDFium (which has
            // robust error recovery and will rewrite a correct xref), then retry the open.
            try
            {
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            }
            catch (Exception openEx) when (IsXRefException(openEx))
            {
                var fixedPath = App.MakeTempFile("fixed");
                if (!TryPdfiumSaveWithZeroRotations(tempPath, fixedPath))
                    throw; // PDFium also failed — re-throw original reopen error
                tempPath = fixedPath;
                _doc = PdfReader.Open(tempPath, PdfDocumentOpenMode.Modify);
            }
            _currentFile = tempPath;

            // Restore rotations in the reopened in-memory doc so saves, form fields,
            // and all other operations see the correct rotation values.
            foreach (var kv in _pageRotations)
                _doc.Pages[kv.Key].Rotate = kv.Value;

            RefreshPageList();
            if (selectedIdx >= 0 && selectedIdx < PageList.Items.Count)
                PageList.SelectedIndex = selectedIdx;
            else if (PageList.Items.Count > 0)
                PageList.SelectedIndex = 0;

            // In Continuous view the strip caches one rendered slot per page. After a
            // page-modifying reload (e.g. crop) it must be rebuilt so the main view reflects the
            // new pages; the slot-sizing in RenderContinuousPages makes cropped pages fit cleanly.
            if (_viewMode == ViewMode.Continuous)
            {
                int contIdx = PageList.SelectedIndex;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => SetupContinuousView(contIdx)));
                return;
            }

            // Refit synchronously so the first rendered frame uses the correct zoom.
            PagePreviewPanel.ScrollToHorizontalOffset(0);
            ReapplyGridOrFit();

            // Deferred refit after layout settles for accurate ActualWidth.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
            {
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                ReapplyGridOrFit();
            }));
        }

        // ============================================================
        // Zoom
        // ============================================================

        private void PagePreview_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (_viewMode == ViewMode.Grid) { GridZoomStep(e.Delta < 0); return; }

                // Capture cursor position and scroll offsets BEFORE zoom changes so we can
                // compute the new offsets that keep the point under the cursor stationary.
                Point cursorInViewport = e.GetPosition(PagePreviewPanel);
                double oldZoom = _zoomLevel;
                double oldHOff = PagePreviewPanel.HorizontalOffset;
                double oldVOff = PagePreviewPanel.VerticalOffset;

                SetZoom(e.Delta > 0 ? _zoomLevel + ZoomStep : _zoomLevel - ZoomStep);

                // After layout settles, reposition the scroll so the cursor point stays fixed.
                // Formula: newOffset = (oldOffset + cursorPos) * (newZoom / oldZoom) - cursorPos
                double ratio   = _zoomLevel / oldZoom;
                double newHOff = (oldHOff + cursorInViewport.X) * ratio - cursorInViewport.X;
                double newVOff = (oldVOff + cursorInViewport.Y) * ratio - cursorInViewport.Y;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)(() =>
                {
                    PagePreviewPanel.ScrollToHorizontalOffset(Math.Max(0, newHOff));
                    PagePreviewPanel.ScrollToVerticalOffset(Math.Max(0, newVOff));
                }));
                return;
            }

            // Regular scroll. Grid and Continuous are a single scroll over the WHOLE document, so the
            // wheel must never be hijacked for page navigation there - just let the ScrollViewer
            // scroll. This is the fix for grid refusing to scroll: right after a zoom/column change
            // the extent can momentarily measure as zero, and the old page-nav fallback below would
            // fire instead of scrolling (and stick until a theme/view-mode switch forced a re-measure).
            if (_viewMode == ViewMode.Grid || _viewMode == ViewMode.Continuous)
                return;

            // Single / Two-Page: a page often fits the viewport, so at the scroll boundary fall
            // through to page navigation so the user can reach adjacent pages without the sidebar.
            if (PagePreviewPanel.ScrollableHeight <= 0)
            {
                e.Handled = true;
                NavigatePageByWheel(e.Delta);
                return;
            }

            bool atTop    = PagePreviewPanel.VerticalOffset <= 0;
            bool atBottom = PagePreviewPanel.VerticalOffset >= PagePreviewPanel.ScrollableHeight - 1;
            if ((atTop && e.Delta > 0) || (atBottom && e.Delta < 0))
            {
                e.Handled = true;
                NavigatePageByWheel(e.Delta);
            }
            // Otherwise let the ScrollViewer scroll naturally.
        }

        private void NavigatePageByWheel(int delta)
        {
            if (_doc is null) return;
            int cur = PageList.SelectedIndex;
            if (delta > 0 && cur > 0)
                PageList.SelectedIndex = cur - 1;
            else if (delta < 0 && cur < _doc.PageCount - 1)
                PageList.SelectedIndex = cur + 1;
        }

        private void ApplyZoom(bool lite = false)
        {
            if (_pageContentGrid.LayoutTransform is ScaleTransform st)
            {
                st.ScaleX = _zoomLevel;
                st.ScaleY = _zoomLevel;
            }
            SyncZoomBox();   // keep the toolbar box in step (FitToWidth/FitToPage don't call SetZoom)
            // Live-resize path: the ScaleTransform above already grew/shrank the existing render to
            // match the new size - smooth and flicker-free. Skip the bitmap re-render and tile rebuild;
            // PagePreviewPanel_SizeChanged debounces one crisp re-render once the drag settles, instead
            // of thrashing it on every size tick (which is what made the page blink during a resize).
            if (lite) return;
            // Recalculate how many pages fit after zoom changes.
            // Use RefreshPageView so link overlays are re-added after RenderAdditionalPages
            // calls ClearSecondaryPages (which wipes them).
            int applyIdx = PageList.SelectedIndex;
            if (applyIdx >= 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => RefreshPageView(applyIdx));

            // If the user has zoomed in past ~10% of the last render, queue a deferred re-render at
            // higher resolution so text re-sharpens quickly (especially on high-DPI displays, where
            // the upscaled bitmap shows blur sooner). The timer debounces rapid Ctrl+scroll.
            // Skipped in Grid: this re-renders via the selected page (not page 0) and, once the render
            // hits its pixel cap when zoomed in, shifts page 0's render width - which is the basis for
            // the grid's column math. That desync locks Ctrl+scroll to a 1<->2 column toggle. The grid
            // is an overview and doesn't need the re-sharpen.
            if (applyIdx >= 0 && _zoomLevel > _lastRenderZoom * 1.10 && _doc is not null
                && _viewMode != ViewMode.Grid)
            {
                if (_rerenderTimer is null)
                {
                    _rerenderTimer = new System.Windows.Threading.DispatcherTimer
                        { Interval = TimeSpan.FromMilliseconds(250) };
                    _rerenderTimer.Tick += (_, _) =>
                    {
                        _rerenderTimer!.Stop();
                        // Never re-render the primary in Grid (it would shift page 0's width basis and
                        // desync the column math); guards a timer started just before a switch into grid.
                        if (_doc is not null && _viewMode != ViewMode.Grid && PageList.SelectedIndex >= 0)
                            RenderPage(PageList.SelectedIndex);
                    };
                }
                _rerenderTimer.Stop();
                _rerenderTimer.Start();
            }
        }

        private void ResetZoom() => SetZoom(1.0);

        // Grid zoom snaps to "fit N pages across the viewport", so zooming steps through clean
        // columns (1, 2, 3, ... per row) instead of arbitrary percentages. N rises as you zoom out
        // and keeps going for larger documents until the page size hits the zoom floor.
        private double GridZoomForN(int n)
        {
            if (n < 1) n = 1;
            double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
            double vw  = PagePreviewPanel.ActualWidth;   // SAME width + slot the layout uses
            if (vw <= 0 || rdW <= 0) return _zoomLevel;
            // RenderAdditionalPages lays out pages in slots of (rdW + 12) within (ActualWidth - 24);
            // invert that so "fit n" produces exactly n columns with no gap.
            return (vw - 24.0) / (n * (rdW + 12.0));
        }

        private void GridZoomStep(bool zoomOut)
        {
            double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
            double vw  = PagePreviewPanel.ActualWidth;
            if (vw <= 0 || rdW <= 0) { SetZoom(zoomOut ? _zoomLevel - ZoomStep : _zoomLevel + ZoomStep); return; }
            // Current columns, computed the SAME way RenderAdditionalPages computes pagesPerRow.
            // +1e-6 guards against floating-point underflow: when _zoomLevel is exactly GridZoomForN(n),
            // (vw-24)/(z*(rdW+12)) computes as n minus a tiny epsilon and would floor to n-1, making the
            // column count read one low - which locked zoom into a 1<->2 column toggle.
            int curN = Math.Max(1, (int)Math.Floor((vw - 24.0) / (_zoomLevel * (rdW + 12.0)) + 1e-6));
            int newN = Math.Max(1, zoomOut ? curN + 1 : curN - 1);
            // If the column count is already at the limit the clamped zoom is unchanged, so
            // skip the re-render entirely - otherwise every Ctrl+Scroll reloads all tiles
            // without changing anything.
            double target = Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(newN)));
            if (Math.Abs(target - _zoomLevel) < 1e-4) return;
            SetZoom(target);   // already clamped to [ZoomMin, ZoomMax]
        }

        /// <summary>
        /// Central zoom-change entry point for buttons, keyboard shortcuts, and the dropdown.
        /// Clamps to [ZoomMin, ZoomMax], applies the scale, syncs the combo box, and updates
        /// the status bar. Does NOT apply a fit mode — call FitToWidth / FitToPage for that.
        /// </summary>
        // The internal _zoomLevel scales each page's layout box. In Continuous mode that box is
        // the page's natural DIP width, so _zoomLevel already reads as true zoom (1.0 = 100%).
        // In Single/Two-Page/Grid the box is the render-dimension bitmap (~2x natural width), so
        // the raw _zoomLevel reads about half the real size. DisplayZoomFactor converts to true
        // zoom for everything shown to (or typed by) the user; the internal value is unchanged.
        private double DisplayZoomFactor()
        {
            if (_viewMode == ViewMode.Continuous || _doc is null) return 1.0;
            int idx = _viewMode == ViewMode.Grid ? 0 : Math.Max(0, PageList.SelectedIndex);
            if (idx < 0 || idx >= _doc.PageCount) return 1.0;
            if (!_renderDims.TryGetValue(idx, out var d) || d.w <= 0) return 1.0;
            double wpt = _doc.Pages[idx].Width.Point, hpt = _doc.Pages[idx].Height.Point;
            if (_pageRotations.TryGetValue(idx, out int r) && (r == 90 || r == 270)) wpt = hpt;
            double naturalW = wpt * 96.0 / 72.0;
            if (naturalW <= 0) return 1.0;
            return d.w / naturalW;
        }
        private double DisplayZoomPct() => _zoomLevel * DisplayZoomFactor() * 100.0;

        private void SetZoom(double level)
        {
            _fitMode   = FitMode.None;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, level));
            ApplyZoom();
            SyncZoomBox();
            if (_doc != null && PageList.SelectedIndex >= 0)
                SetStatus($"Page {PageList.SelectedIndex + 1} of {_doc.PageCount} - {DisplayZoomPct():F0}%");
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)  { if (_viewMode == ViewMode.Grid) GridZoomStep(false); else SetZoom(_zoomLevel + ZoomStep); }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (_viewMode == ViewMode.Grid) GridZoomStep(true);  else SetZoom(_zoomLevel - ZoomStep); }

        private void SyncZoomBox()
        {
            if (_zoomBox is null) return;
            _zoomBox.SelectionChanged -= ZoomBox_SelectionChanged;

            // When a fit mode is active, show the "Fit Width"/"Fit Page" entry rather than a raw
            // percentage so the box matches the status bar.
            string? fitTag = _fitMode == FitMode.Width ? "fitwidth"
                           : _fitMode == FitMode.Page  ? "fitpage"
                           : null;
            if (fitTag != null)
            {
                foreach (ComboBoxItem item in _zoomBox.Items)
                {
                    if (item.Tag?.ToString() == fitTag)
                    {
                        _zoomBox.SelectedItem = item;
                        _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
                        return;
                    }
                }
            }

            string target = $"{DisplayZoomPct():F0}%";
            foreach (ComboBoxItem item in _zoomBox.Items)
            {
                if (item.Content?.ToString() == target)
                {
                    _zoomBox.SelectedItem = item;
                    _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
                    return;
                }
            }
            // No preset match - clear dropdown selection and show free-form percentage
            _zoomBox.SelectedItem = null;
            _zoomBox.Text = target;
            _zoomBox.SelectionChanged += ZoomBox_SelectionChanged;
        }

        private void ZoomBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_zoomBox?.SelectedItem is not ComboBoxItem item) return;
            // Editable combos highlight the shown value after a pick (looks like selected text);
            // collapse that selection to just the caret once the value settles.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
            {
                if (_zoomBox.Template?.FindName("PART_EditableTextBox", _zoomBox) is TextBox etb)
                    etb.Select(etb.Text.Length, 0);
            }));
            string? tag = item.Tag?.ToString();
            if (tag is null) return;

            if (tag == "fitwidth") { FitToWidth(); return; }
            if (tag == "fitpage")  { FitToPage();  return; }

            if (double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double z))
            {
                _fitMode = FitMode.None;
                // Preset tags are true zoom (1.0 = 100%); convert to the internal render-dim scale.
                double zf = DisplayZoomFactor(); if (zf <= 0) zf = 1.0;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, z / zf));
                ApplyZoom();
                if (PageList.SelectedIndex >= 0 && _doc != null)
                    SetStatus($"Page {PageList.SelectedIndex + 1} of {_doc.PageCount} - {DisplayZoomPct():F0}%");
            }
        }

        private void FitToWidth(bool lite = false)
        {
            double viewW = PagePreviewPanel.ActualWidth - 40;
            if (viewW <= 0) return;

            // Continuous mode: pages are laid out at _continuousPageW (natural DIPs width)
            // and scaled by the ScaleTransform on PageContentGrid. PageImage is hidden, so
            // we cannot use its Source as a guard; use _continuousPageW directly instead.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0) return;
                _fitMode   = FitMode.Width;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, viewW / _continuousPageW));
                ApplyZoom(lite);
                int ci = PageList.SelectedIndex;
                if (ci >= 0 && _doc != null)
                    SetStatus(string.Format(Loc("Str_FitWidth"), ci + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
                return;
            }

            if (PageImage.Source is null) return;
            // Use _renderDims rather than PageImage.ActualWidth - the latter can be stale
            // (reporting the previous page's layout size) if WPF layout hasn't fully settled.
            // _renderDims is set synchronously inside RenderPage so it always matches the
            // current page. dipW is zoom-stable: scaledMax scales with zoom while RenderPage
            // divides by zoomFactor, so the two cancel out. Use dipW directly.
            int idx = PageList.SelectedIndex;
            double dipW = (idx >= 0 && _renderDims.TryGetValue(idx, out var dimsW))
                ? dimsW.w
                : (PageImage.ActualWidth > 0 ? PageImage.ActualWidth : 1);
            if (dipW <= 0) return;
            // Two Page mode shows two pages side by side — each page gets roughly half
            // the viewport width (minus a small gap between pages).
            double slotW = _viewMode == ViewMode.TwoPage ? (viewW - 12) / 2 : viewW;
            _fitMode = FitMode.Width;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, slotW / dipW));
            ApplyZoom(lite);
            if (idx >= 0 && _doc != null)
                SetStatus(string.Format(Loc("Str_FitWidth"), idx + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
        }

        private void FitToPage(bool lite = false)
        {
            double viewW = PagePreviewPanel.ActualWidth  - 40;
            double viewH = PagePreviewPanel.ActualHeight - 40;
            if (viewW <= 0 || viewH <= 0) return;

            // Continuous mode: derive the current page's natural height from its PDF aspect
            // ratio and _continuousPageW, then fit both axes.
            if (_viewMode == ViewMode.Continuous)
            {
                if (_continuousPageW <= 0 || _doc is null) return;
                int ci = PageList.SelectedIndex;
                if (ci < 0) return;
                var pdfPage = _doc.Pages[ci];
                double ratio = Math.Max(0.1, pdfPage.Height.Point / Math.Max(1.0, pdfPage.Width.Point));
                double dipH  = _continuousPageW * ratio;
                _fitMode   = FitMode.Page;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                    Math.Min(viewW / _continuousPageW, viewH / dipH)));
                ApplyZoom(lite);
                SetStatus(string.Format(Loc("Str_FitPage"), ci + 1, _doc.PageCount, $"{DisplayZoomPct():F0}"));
                return;
            }

            if (PageImage.Source is null) return;
            int idx = PageList.SelectedIndex;
            // dipW/dipH are zoom-stable (see FitToWidth comment). Use them directly.
            double dipW = (idx >= 0 && _renderDims.TryGetValue(idx, out var dimsP))
                ? dimsP.w
                : (PageImage.ActualWidth > 0 ? PageImage.ActualWidth : 1);
            double dipH2 = (idx >= 0 && _renderDims.TryGetValue(idx, out var dimsP2))
                ? dimsP2.h
                : (PageImage.ActualHeight > 0 ? PageImage.ActualHeight : 1);
            if (dipW <= 0 || dipH2 <= 0) return;
            double slotW2 = _viewMode == ViewMode.TwoPage ? (viewW - 12) / 2 : viewW;
            _fitMode = FitMode.Page;
            _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax,
                Math.Min(slotW2 / dipW, viewH / dipH2)));
            ApplyZoom(lite);
            SetStatus(string.Format(Loc("Str_FitPage"), idx + 1, _doc!.PageCount, $"{DisplayZoomPct():F0}"));
        }

        // Re-fit the main view after a reload. Grid keeps its column-fit (FitToWidth alone would
        // yank it out into a single-page Fit Width view); other modes honor the fit mode.
        private void ReapplyGridOrFit()
        {
            if (_viewMode == ViewMode.Grid)
            {
                double rdW = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
                double vw  = PagePreviewPanel.ActualWidth;
                if (vw > 0 && rdW > 0)
                {
                    int curN = Math.Max(1, (int)Math.Round((vw - 24.0) / (Math.Max(0.01, _zoomLevel) * (rdW + 12.0))));
                    SetZoom(GridZoomForN(curN));
                }
                else ApplyZoom();
                return;
            }
            if (_fitMode == FitMode.Page) FitToPage();
            else FitToWidth();
        }

        private System.Windows.Threading.DispatcherTimer? _resizeRefitTimer;
        private int _gridColumns = 1;   // columns the grid is currently laid out in; held across resizes

        private void PagePreviewPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RepositionAnnotationBars();   // keep the draw/text bar on its anchored edge as the pane resizes
            if (_cropPreviewRect is not null || _cropConfirmBar is not null) return;

            if (_viewMode == ViewMode.Grid)
            {
                // Grid columns depend only on width, so a height-only resize (e.g. dragging the bottom
                // edge) changes nothing - skip it so it doesn't needlessly re-render/blink.
                if (!e.WidthChanged) return;
                // Hold the column count through the resize: scale the already-laid-out tiles via the
                // transform so the same number of columns fills the new width. This is lite-only (no
                // re-render), so it can't toggle tiles/scrollbars into the feedback loop the grid used to
                // fear - and with the vertical scrollbar reserved (above), the width stays stable too.
                if (_doc is null || _gridColumns < 1) return;
                double rdWg = _annotationCanvas.Width > 0 ? _annotationCanvas.Width : 1583;
                if (PagePreviewPanel.ActualWidth <= 0 || rdWg <= 0) return;
                _zoomLevel = Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(_gridColumns)));
                ApplyZoom(lite: true);
                StartResizeSettleTimer();   // crisp re-render once the drag settles
                return;
            }

            // Live: rescale the page(s) already on screen via the ScaleTransform only (lite). This
            // tracks the drag smoothly without re-rendering, so there's no flicker mid-resize.
            if (_fitMode == FitMode.Width) FitToWidth(lite: true);
            else if (_fitMode == FitMode.Page) FitToPage(lite: true);
            StartResizeSettleTimer();
        }

        // Coalesces resize ticks: the crisp re-render runs once, a beat after the last size change.
        private void StartResizeSettleTimer()
        {
            if (_resizeRefitTimer is null)
            {
                _resizeRefitTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(110) };
                _resizeRefitTimer.Tick += (_, _) => { _resizeRefitTimer!.Stop(); OnResizeSettled(); };
            }
            _resizeRefitTimer.Stop();
            _resizeRefitTimer.Start();
        }

        private void OnResizeSettled()
        {
            if (_viewMode == ViewMode.Grid)
            {
                // Crisp re-render at the held column count for the final size (the drag only transform-
                // scaled the tiles). The grid's width is stable (vertical scrollbar reserved), so this
                // settles in one pass instead of looping.
                if (_doc is not null && _gridColumns >= 1)
                    SetZoom(Math.Max(ZoomMin, Math.Min(ZoomMax, GridZoomForN(_gridColumns))));
                return;
            }
            if (_fitMode == FitMode.Width) FitToWidth();
            else if (_fitMode == FitMode.Page) FitToPage();
        }

        private void PagePreviewPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            bool spaceDown = Keyboard.IsKeyDown(Key.Space);
            if (e.ChangedButton == MouseButton.Middle ||
                (e.ChangedButton == MouseButton.Left && spaceDown))
            {
                _isPanning  = true;
                _panStart   = e.GetPosition(PagePreviewPanel);
                _panScrollH = PagePreviewPanel.HorizontalOffset;
                _panScrollV = PagePreviewPanel.VerticalOffset;
                PagePreviewPanel.CaptureMouse();
                PagePreviewPanel.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
            // Crop: allow starting the selection OUTSIDE the page. On-page clicks are handled by
            // the page overlay; here we catch clicks in the margins, route them to the nearest page
            // overlay, and clamp the start point to the page edge so the rect stays on the page.
            else if (e.ChangedButton == MouseButton.Left && !spaceDown
                     && _currentTool == EditTool.Crop && _doc is not null)
            {
                // Resolve the page surface for an off-page crop start in any view mode. On-page
                // clicks are left to the page canvas/overlay; we only handle margin clicks here.
                Canvas? target = null;
                if (_viewMode == ViewMode.Continuous)
                {
                    if (!(e.OriginalSource is DependencyObject osc && IsWithinPageOverlay(osc)))
                    {
                        // Prefer the page centered in the viewport (kept in sync by scrolling) - it's
                        // the page the user is looking at. Fall back to the nearest page by click Y.
                        int pg = PageList.SelectedIndex;
                        if (pg < 0 || !_continuousCanvases.ContainsKey(pg))
                            pg = NearestContinuousPage(e.GetPosition(_continuousPanel).Y);
                        if (pg >= 0) _continuousCanvases.TryGetValue(pg, out target);
                    }
                }
                else
                {
                    // Single / Two-Page / Grid. An on-page click is handled by that page's own
                    // surface: the primary page uses _annotationCanvas, secondary/grid tiles use
                    // their per-page overlay. Only a genuine margin click (on neither) is routed
                    // here, and we fall back to the primary page for it.
                    bool onPrimary = e.OriginalSource is DependencyObject oss && IsDescendantOf(oss, _annotationCanvas);
                    bool onTile    = e.OriginalSource is DependencyObject ost && IsWithinPageOverlay(ost);
                    if (!onPrimary && !onTile)
                        target = _annotationCanvas;
                }
                if (target is not null && target.Width > 0 && target.Height > 0)
                {
                    _activeCanvas = target;
                    // Pin the gesture surface/page so mouse-move/up resolve against this overlay
                    // (a margin crop start doesn't go through Canvas_MouseLeftButtonDown).
                    _gestureCanvas = target;
                    _gesturePage   = target.Tag is int gt ? gt : PageList.SelectedIndex;
                    var p = e.GetPosition(target);
                    p.X = Math.Max(0, Math.Min(target.Width, p.X));
                    p.Y = Math.Max(0, Math.Min(target.Height, p.Y));
                    StartCropDraw(p);
                    e.Handled = true;
                }
            }
        }

        // Begin a crop selection on the active overlay at pos (render-dim coords).
        private void StartCropDraw(Point pos)
        {
            _cropPageIndex = _activeCanvas.Tag is int cpi ? cpi : (_viewMode == ViewMode.Grid ? 0 : PageList.SelectedIndex);
            ClearSelection();
            HideCropConfirmBar();
            _isDrawing = true;
            _drawStart = pos;
            _cropPreviewRect = new Rectangle
            {
                Stroke          = Brushes.White,
                StrokeThickness = 1.5,
                StrokeDashArray = [5, 3],
                Fill            = AccentBrush(55),
                Width = 0, Height = 0,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 3, Opacity = 0.7 },
            };
            Canvas.SetLeft(_cropPreviewRect, pos.X);
            Canvas.SetTop(_cropPreviewRect, pos.Y);
            Panel.SetZIndex(_cropPreviewRect, 1);
            _activeCanvas.Children.Add(_cropPreviewRect);
            _activePreview = _cropPreviewRect;
            _activeCanvas.CaptureMouse();
        }

        private bool IsWithinPageOverlay(DependencyObject node)
        {
            var cur = node;
            while (cur != null)
            {
                if (cur is Canvas c && _continuousCanvases.ContainsValue(c)) return true;
                cur = VisualTreeHelper.GetParent(cur);
            }
            return false;
        }

        private int NearestContinuousPage(double yInPanel)
        {
            int best = -1; double bestDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count && i < _continuousPanel.Children.Count; i++)
            {
                double top = _continuousTops[i];
                double h = ((FrameworkElement)_continuousPanel.Children[i]).Height;
                if (double.IsNaN(h)) h = 0;
                double bottom = top + h;
                double dist = yInPanel < top ? top - yInPanel : (yInPanel > bottom ? yInPanel - bottom : 0);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }

        private void PagePreviewPanel_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(PagePreviewPanel);
            PagePreviewPanel.ScrollToHorizontalOffset(_panScrollH - (pos.X - _panStart.X));
            PagePreviewPanel.ScrollToVerticalOffset  (_panScrollV - (pos.Y - _panStart.Y));
            e.Handled = true;
        }

        private void PagePreviewPanel_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning) return;
            if (e.ChangedButton != MouseButton.Middle && e.ChangedButton != MouseButton.Left) return;
            _isPanning = false;
            PagePreviewPanel.ReleaseMouseCapture();
            PagePreviewPanel.Cursor = _spaceHeld ? Cursors.Hand : Cursors.Arrow;
            e.Handled = true;
        }

        // ============================================================
        // Drag/drop: file open
        // ============================================================

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
                if (files.Length > 0 && files[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    OpenInNewTab(files[0]);
            }
        }

        private void DropZone_Click(object sender, MouseButtonEventArgs e) => Open_Click(sender, e);

        // ============================================================
        // Drag/drop: page reorder
        // ============================================================

        private bool _pageDragArmed;
        private void PageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            // Only arm a page-reorder drag when the press lands on a page thumbnail, not the
            // scrollbar - otherwise grabbing the scrollbar starts a page-move drag (the "insert"
            // cursor) instead of scrolling.
            _pageDragArmed = false;
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.Primitives.ScrollBar) break;
                if (d is ListBoxItem) { _pageDragArmed = true; break; }
            }
        }

        private void PageList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_pageDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (PageList.SelectedIndex >= 0)
                    DragDrop.DoDragDrop(PageList, PageList.SelectedIndex, DragDropEffects.Move);
            }
        }

        private void PageList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(typeof(int)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void PageList_Drop(object sender, DragEventArgs e)
        {
            if (_doc is null || !e.Data.GetDataPresent(typeof(int))) return;
            var doc = _doc;
            int fromIdx = (int)e.Data.GetData(typeof(int))!;
            var pos = e.GetPosition(PageList);
            int toIdx = PageList.Items.Count - 1;
            for (int i = 0; i < PageList.Items.Count; i++)
            {
                if (PageList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                {
                    var itemPos = item.TranslatePoint(new Point(0, item.ActualHeight / 2), PageList);
                    if (pos.Y < itemPos.Y) { toIdx = i; break; }
                }
            }
            if (fromIdx == toIdx) return;
            var page = doc.Pages[fromIdx];
            doc.Pages.RemoveAt(fromIdx);
            if (toIdx > fromIdx) toIdx--;
            doc.Pages.Insert(toIdx, page);
            SaveTempAndReload();
            PageList.SelectedIndex = toIdx;
        }

        // ============================================================
        // Page selection handler
        // ============================================================

        // Lazy accessor — resolves PageList's internal ScrollViewer on first use.
        private ScrollViewer? _sidebarSv;
        private ScrollViewer? SidebarScrollViewer
            => _sidebarSv ??= FindDescendant<ScrollViewer>(PageList);

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) return hit;
                var result = FindDescendant<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void PageList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            SidebarScrollViewer?.ScrollToVerticalOffset(
                SidebarScrollViewer.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        private void PageJumpBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _doc is null) return;
            e.Handled = true;
            if (int.TryParse(_pageJumpBox.Text, out int pg))
            {
                int idx = Math.Max(0, Math.Min(_doc.PageCount - 1, pg - 1));
                PageList.SelectedIndex = idx;
            }
            else
            {
                // Restore current page number if input was invalid
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
            }
            Keyboard.ClearFocus();
        }

        private void PageJumpBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _pageJumpBox.SelectAll();
        }

        private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageList.SelectedIndex >= 0)
            {
                CommitActiveTextBox();
                ClearSelection();
                ClearTextSelection();
                PageList.ScrollIntoView(PageList.SelectedItem);   // keep the sidebar thumbnail in view
                if (_viewMode == ViewMode.Continuous)
                {
                    _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                    ScrollContinuousToPage(PageList.SelectedIndex);
                    return;
                }
                if (_viewMode == ViewMode.Grid)
                {
                    // Grid is a stable overview: selecting a page highlights it but must NOT
                    // re-anchor the grid. It still needs an initial render (open / first display)
                    // when no tiles exist yet; later selections only update the highlight.
                    _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                    if (_pageContentPanel.Children.Count <= 1)
                    {
                        PagePreviewPanel.ScrollToTop();
                        PagePreviewPanel.ScrollToHorizontalOffset(0);
                        RenderPage(0);   // grid primary is always page 0
                        // Default the grid to a clean 3-columns-across fit. Deferred to Loaded so the
                        // viewport width is valid (it can still be 0 mid-open, which would fall back
                        // to a carried-over zoom and show a single large page).
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                            (Action)(() => SetZoom(GridZoomForN(Math.Min(_doc?.PageCount ?? 1, 3)))));
                    }
                    else if (PageList.SelectedIndex < _pageContentPanel.Children.Count
                             && _pageContentPanel.Children[PageList.SelectedIndex] is FrameworkElement gridTile)
                    {
                        // Scroll the chosen page's tile into view (BringIntoView accounts for the zoom transform).
                        gridTile.BringIntoView();
                    }
                    return;
                }
                // Two-page spreads pair (0,1),(2,3),...; clicking either page of the spread that's
                // already shown (or re-selecting the current single page) renders the exact same pixels,
                // so skip the re-render and its flash - just move the page number.
                int targetPrimary = PageList.SelectedIndex;
                if (_viewMode == ViewMode.TwoPage) targetPrimary -= targetPrimary % 2;
                if (targetPrimary == _renderedPrimaryPage && Math.Abs(_zoomLevel - _lastRenderZoom) < 0.0001)
                {
                    _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                    return;
                }
                PagePreviewPanel.ScrollToTop();
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                RenderPage(PageList.SelectedIndex);
                ApplyZoom();
                // Update page jump box
                _pageJumpBox.Text = (PageList.SelectedIndex + 1).ToString();
                // Re-highlight search results on this page if a search is active
                if (_searchBar is not null && _searchBar.Visibility == Visibility.Visible
                    && _allSearchRects.Count > 0)
                    HighlightSearchResultsOnCurrentPage();
            }
        }

        private void ShortcutHelp_Click(object sender, RoutedEventArgs e)
        {
            if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);
            else FadeOverlayIn(ShortcutOverlay);
        }

        private void ShortcutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Click on the dim backdrop closes the overlay.
            FadeOverlayOut(ShortcutOverlay);
        }

        private void ShortcutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Stop the click from bubbling up to the backdrop handler.
            e.Handled = true;
        }

        private void ShortcutOverlayClose_Click(object sender, RoutedEventArgs e)
        {
            FadeOverlayOut(ShortcutOverlay);
        }

        // ── About overlay ───────────────────────────────────────────────

        private void ShowAboutOverlay()
        {
            // Populate dynamic values (SHA256 is slow; run on background thread)
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                               .GetName().Version?.ToString(3) ?? "?";
            var (sigValid, sigSubject, sigThumbprint) = App.GetExeSignerInfo();

            AboutPublisherBlock.Text   = sigValid ? sigSubject : "(not signed or chain failed)";
            AboutThumbprintBlock.Text  = string.IsNullOrEmpty(sigThumbprint) ? "(none)" : sigThumbprint;
            AboutSha256Block.Text      = Loc("Str_About_Computing");

            // Reuse the main window's film-grain texture on the About card.
            if (GrainBrush?.ImageSource != null) AboutGrainBrush.ImageSource = GrainBrush.ImageSource;

            // Logo block: "Killer" in the primary color, "PDF" in the brand green.
            AboutLogoBlock.Inlines.Clear();
            var logoHl = new System.Windows.Documents.Hyperlink { TextDecorations = null };
            logoHl.Inlines.Add(new System.Windows.Documents.Run("Killer")
            {
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
            });
            logoHl.Inlines.Add(new System.Windows.Documents.Run("PDF")
            {
                Foreground = (System.Windows.Media.Brush)FindResource("AccentLogo")
            });
            logoHl.Click += (_, _) =>
                Process.Start(new ProcessStartInfo("https://pdf.killertools.net") { UseShellExecute = true });
            AboutLogoBlock.Inlines.Add(logoHl);

            // Tagline block
            AboutTaglineBlock.Inlines.Clear();
            // Localized tagline. {0} is the (untranslated) brand, so splitting on the placeholder
            // keeps "Killer Tools" a styled, clickable link while the rest translates and the brand
            // can sit anywhere in the sentence the language needs it.
            var taglineDim = (System.Windows.Media.Brush)FindResource("TextSecondary");
            var taglineText = Loc("Str_Tagline");
            int taglineBrand = taglineText.IndexOf("{0}", StringComparison.Ordinal);
            string taglinePre = taglineBrand >= 0 ? taglineText[..taglineBrand] : taglineText;
            string taglineSuf = taglineBrand >= 0 ? taglineText[(taglineBrand + 3)..] : "";
            AboutTaglineBlock.Inlines.Add(new System.Windows.Documents.Run(taglinePre) { Foreground = taglineDim });
            var ktHl = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Killer Tools"))
            {
                Foreground      = (System.Windows.Media.Brush)FindResource("Accent"),
                TextDecorations = null
            };
            ktHl.Click += (_, _) =>
                Process.Start(new ProcessStartInfo("https://killertools.net") { UseShellExecute = true });
            AboutTaglineBlock.Inlines.Add(ktHl);
            AboutTaglineBlock.Inlines.Add(new System.Windows.Documents.Run(taglineSuf) { Foreground = taglineDim });

            // Version block (clickable - opens GitHub release)
            AboutVersionBlock.Inlines.Clear();
            var verHl = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run($"v{version}"))
            {
                Foreground      = (System.Windows.Media.Brush)FindResource("Accent"),
                TextDecorations = null
            };
            verHl.Click += (_, _) =>
                Process.Start(new ProcessStartInfo(
                    $"https://github.com/SteveTheKiller/KillerPDF/releases/tag/v{version}")
                { UseShellExecute = true });
            AboutVersionBlock.Inlines.Add(verHl);

            // Update check: hidden until/unless a newer release is confirmed online
            AboutUpdateButton.Visibility = Visibility.Collapsed;
            FadeOverlayIn(AboutOverlay);
            CheckForUpdateAsync(System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);

            // Compute SHA256 off the UI thread
            System.Threading.Tasks.Task.Run(() =>
            {
                var sha256 = App.GetExeSha256();
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    (Action)(() => AboutSha256Block.Text = sha256));
            });
        }

        private void AboutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            FadeOverlayOut(AboutOverlay);
        }

        private void AboutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        private void AboutOverlayClose_Click(object sender, RoutedEventArgs e)
        {
            FadeOverlayOut(AboutOverlay);
        }

        // Quietly checks GitHub for a newer release when the About dialog opens. Runs only on
        // demand (no background service), times out fast, and silently does nothing if there is
        // no internet or the request fails. Shows the update button only if a newer tag exists.
        private async void CheckForUpdateAsync(Version? current)
        {
            if (current is null) return;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-UpdateCheck");
                var json = await http.GetStringAsync(
                    "https://api.github.com/repos/SteveTheKiller/KillerPDF/releases/latest")
                    .ConfigureAwait(false);

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
                var tag = tagEl.GetString();
                if (string.IsNullOrWhiteSpace(tag)) return;
                if (!Version.TryParse(tag!.TrimStart('v', 'V').Trim(), out var latest)) return;

                var cur = new Version(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
                var lat = new Version(latest.Major, latest.Minor, latest.Build < 0 ? 0 : latest.Build);
                if (lat <= cur) return;

                await Dispatcher.BeginInvoke((Action)(() =>
                {
                    _updateTag = $"v{lat.ToString(3)}";
                    AboutUpdateText.Text = $"Update available: {_updateTag}";
                    AboutUpdateButton.Visibility = Visibility.Visible;
                }));
            }
            catch { /* offline, timeout, or API error - quietly do nothing */ }
        }

        private string? _updateTag;   // "vX.Y.Z" of the available update, set by CheckForUpdateAsync

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e) => DoSelfUpdateAsync();

        // One-click self-update: downloads the released exe, verifies it against the published
        // SHA256SUMS.txt, then hands off to a small batch that waits for this process to exit,
        // swaps the exe in place, and relaunches with the currently-open PDF. Falls back to the
        // releases page if anything fails (offline, checksum mismatch, unwritable location).
        private async void DoSelfUpdateAsync()
        {
            var tag = _updateTag;
            if (string.IsNullOrEmpty(tag)) return;

            if (_isDirty)
            {
                KillerDialog.Show(this, "Please save your changes before updating.",
                    "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = KillerDialog.Show(this,
                $"Download and install KillerPDF {tag}?\n\nThe app will close and reopen automatically.",
                "KillerPDF", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            AboutUpdateButton.IsEnabled = false;
            AboutUpdateText.Text = "Downloading...";

            string? newExe = null;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerPDF-UpdateCheck");

                var exeUrl  = $"https://github.com/SteveTheKiller/KillerPDF/releases/download/{tag}/KillerPDF.exe";
                var sumsUrl = $"https://raw.githubusercontent.com/SteveTheKiller/KillerPDF/{tag}/SHA256SUMS.txt";

                var exeBytes = await http.GetByteArrayAsync(exeUrl);
                var sumsTxt  = await http.GetStringAsync(sumsUrl);

                // Find the expected hash for KillerPDF.exe
                string? expected = null;
                foreach (var line in sumsTxt.Replace("\r", "").Split('\n'))
                {
                    if (line.TrimStart().StartsWith("KillerPDF.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2) expected = parts[^1];
                        break;
                    }
                }
                if (string.IsNullOrEmpty(expected)) throw new Exception("checksum entry not found");

                string actual;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    actual = BitConverter.ToString(sha.ComputeHash(exeBytes)).Replace("-", "");
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("checksum mismatch");

                newExe = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KillerPDF_update_{Guid.NewGuid():N}.exe");
                File.WriteAllBytes(newExe, exeBytes);
            }
            catch
            {
                // Offline, timed out, or verification failed: restore the button and open the
                // releases page so the user can update manually.
                AboutUpdateButton.IsEnabled = true;
                AboutUpdateText.Text = $"Update available: {tag}";
                try { Process.Start(new ProcessStartInfo(
                    "https://github.com/SteveTheKiller/KillerPDF/releases/latest") { UseShellExecute = true }); }
                catch { }
                return;
            }

            // Apply the update after we exit, then relaunch reopening the current PDF.
            try
            {
                var curExe = Process.GetCurrentProcess().MainModule!.FileName;
                var reopen = _originalFile ?? _currentFile;
                var pid    = Process.GetCurrentProcess().Id;
                var relArg = string.IsNullOrEmpty(reopen) ? "" : $" \"{reopen}\"";
                var bat    = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"killerpdf_update_{Guid.NewGuid():N}.bat");

                File.WriteAllText(bat,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                    "if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
                    $"copy /y \"{newExe}\" \"{curExe}\" >nul\r\n" +
                    $"start \"\" \"{curExe}\"{relArg}\r\n" +
                    $"del \"{newExe}\" >nul 2>&1\r\n" +
                    "del \"%~f0\" >nul 2>&1\r\n");

                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });

                Application.Current.Shutdown();
            }
            catch
            {
                AboutUpdateButton.IsEnabled = true;
                AboutUpdateText.Text = $"Update available: {tag}";
            }
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        // ============================================================
        // View Mode
        // ============================================================

        private void SetViewMode(ViewMode mode)
        {
            if (_viewMode == mode) return;
            _viewMode = mode;
            _renderedPrimaryPage = -1;   // spread/layout changes with the mode; force the next render
            _gridScrollToPage = -1;
            App.SetSetting("ViewMode", mode.ToString());

            bool isContinuous = mode == ViewMode.Continuous;
            _pageContentPanel.Visibility = isContinuous ? Visibility.Collapsed : Visibility.Visible;
            _continuousPanel.Visibility  = isContinuous ? Visibility.Visible   : Visibility.Collapsed;

            if (!isContinuous)
            {
                _continuousRenderCts?.Cancel();
                _continuousPanel.Children.Clear();
                _continuousTops.Clear();
                _continuousCanvases.Clear();
            }

            if (_doc is null) return;
            int idx = PageList.SelectedIndex;
            if (mode == ViewMode.Continuous)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => SetupContinuousView(idx));
            }
            else
            {
                _secondaryRenderCts?.Cancel();
                ClearSecondaryPages();
                _pageContentPanel.Width = double.NaN;
                // Drop any scroll offset carried over from the previous mode (especially Continuous,
                // whose large vertical offset would otherwise land the grid mid-document).
                PagePreviewPanel.ScrollToVerticalOffset(0);
                PagePreviewPanel.ScrollToHorizontalOffset(0);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    RenderPage(mode == ViewMode.Grid ? 0 : idx);
                    // Grid: apply a clean column-fit zoom (continuous's zoom is far too large for a
                    // grid, and a non-column zoom leaves a gap). SetZoom -> ApplyZoom defers the
                    // single tile render, so return here instead of calling RefreshPageView again
                    // (a second render would duplicate tiles).
                    if (mode == ViewMode.Grid)
                    {
                        SetZoom(GridZoomForN(Math.Min(_doc!.PageCount, 3)));
                        // The first fit can run before the viewport width has settled (leaving the
                        // grid off-center / at the wrong zoom); re-fit once more after layout settles,
                        // and pin to the top so nothing carries over from the previous mode.
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                            (Action)(() =>
                            {
                                ReapplyGridOrFit();
                                // Selection is preserved across the switch; scroll to that page once
                                // its tile streams in (grid tiles render async). Page 0 stays at top.
                                if (idx > 0) _gridScrollToPage = idx;
                                else
                                {
                                    PagePreviewPanel.ScrollToVerticalOffset(0);
                                    PagePreviewPanel.ScrollToHorizontalOffset(0);
                                }
                            }));
                        return;
                    }
                    // Switching into Single or Two-Page fits the whole page so it isn't left at an
                    // awkward carried-over zoom from another mode.
                    if      (mode == ViewMode.Single || mode == ViewMode.TwoPage) FitToPage();
                    else if (_fitMode == FitMode.Width) FitToWidth();
                    else if (_fitMode == FitMode.Page)  FitToPage();
                    else                                ApplyZoom();
                    RefreshPageView(idx);
                });
            }
        }

        private void ScrollContinuousToPage(int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= _continuousTops.Count) return;
            double target = _continuousTops[pageIndex] * _zoomLevel;
            PagePreviewPanel.ScrollToVerticalOffset(target);
        }

        private void PagePreviewPanel_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // The vertical scrollbar can appear/disappear without a window resize (zoom, page count
            // changes). When it does, re-anchor the annotate bars so a right-docked bar tracks the
            // scrollbar's edge instead of getting covered (or stranded once it's gone).
            bool vis = PagePreviewPanel.ComputedVerticalScrollBarVisibility == Visibility.Visible;
            if (vis != _vScrollVisible)
            {
                _vScrollVisible = vis;
                RepositionAnnotationBars();
            }

            if (_viewMode != ViewMode.Continuous || _continuousTops.Count == 0) return;

            double viewportCenter = (PagePreviewPanel.VerticalOffset + PagePreviewPanel.ViewportHeight * 0.5)
                                    / Math.Max(0.01, _zoomLevel);
            int nearest = 0;
            double minDist = double.MaxValue;
            for (int i = 0; i < _continuousTops.Count; i++)
            {
                if (i >= _continuousPanel.Children.Count) break;
                var slot = (FrameworkElement)_continuousPanel.Children[i];
                double center = _continuousTops[i] + slot.Height * 0.5;
                double dist   = Math.Abs(center - viewportCenter);
                if (dist < minDist) { minDist = dist; nearest = i; }
            }

            if (PageList.SelectedIndex != nearest)
            {
                _pageJumpBox.Text = (nearest + 1).ToString();
                // Update sidebar thumbnail without triggering a full page render
                PageList.SelectionChanged -= PageList_SelectionChanged;
                PageList.SelectedIndex = nearest;
                PageList.SelectionChanged += PageList_SelectionChanged;
            }
        }

        private void SetupContinuousView(int initialPage)
        {
            if (_doc is null) return;
            _continuousRenderCts?.Cancel();
            _continuousPanel.Children.Clear();
            _continuousTops.Clear();
            _continuousCanvases.Clear();

            // Use the PDF's natural page width in WPF DIPs (96 DIP/inch, 72 pt/inch).
            // This is zoom-independent, which is critical: FitToWidth computes
            //   zoom = viewportW / _continuousPageW
            // and if _continuousPageW were derived from the current zoom level the two
            // would cancel and FitToWidth would always return approximately the old zoom.
            var refPage = _doc.Pages[0];
            _continuousPageW = Math.Max(200.0, refPage.Width.Point * (96.0 / 72.0));

            double y = 0;
            for (int i = 0; i < _doc.PageCount; i++)
            {
                _continuousTops.Add(y);
                var pdfPage = _doc.Pages[i];
                double pw = pdfPage.Width.Point, ph = pdfPage.Height.Point;
                if (_pageRotations.TryGetValue(i, out int prot) && (prot == 90 || prot == 270))
                    (pw, ph) = (ph, pw);
                double ratio = Math.Max(0.1, ph / Math.Max(1, pw));
                double slotH = _continuousPageW * ratio;

                // Canonical render-dim space (matches single-page RenderPage: longest side -> 2048)
                // so annotation coordinates are identical in both view modes.
                double maxDim = Math.Max(pw, ph);
                int rdW = Math.Max(1, (int)Math.Round(2048.0 * pw / maxDim));
                int rdH = Math.Max(1, (int)Math.Round(2048.0 * ph / maxDim));
                _renderDims[i] = (rdW, rdH);

                // Per-page annotation overlay: sized in render-dim space, scaled to the slot.
                double slotScale = _continuousPageW / rdW;
                var overlay = new Canvas
                {
                    Width           = rdW,
                    Height          = rdH,
                    Background       = Brushes.Transparent,
                    ClipToBounds     = true,
                    Tag              = i,
                    LayoutTransform  = new System.Windows.Media.ScaleTransform(slotScale, slotScale)
                };
                overlay.PreviewMouseLeftButtonDown += Canvas_MouseLeftButtonDown;
                overlay.MouseMove                  += Canvas_MouseMove;
                overlay.PreviewMouseLeftButtonUp   += Canvas_MouseLeftButtonUp;
                // Right-click opens the same context menu as the primary page (overlays don't
                // inherit _annotationCanvas's ContextMenu), targeting the right-clicked page.
                int rcPage = i;
                overlay.PreviewMouseRightButtonUp += (s, ev) =>
                {
                    if (_viewMode != ViewMode.TwoPage) PageList.SelectedIndex = rcPage;
                    if (_annotationCanvas.ContextMenu is ContextMenu cm)
                    {
                        cm.PlacementTarget = (UIElement)s;
                        cm.IsOpen = true;
                        ev.Handled = true;
                    }
                };
                _continuousCanvases[i] = overlay;

                var pageImg = new Image { Stretch = Stretch.None, Width = _continuousPageW, Height = slotH };
                RenderOptions.SetBitmapScalingMode(pageImg, BitmapScalingMode.HighQuality);

                var slotGrid = new Grid();
                slotGrid.Children.Add(pageImg);
                slotGrid.Children.Add(overlay);

                var placeholder = new Border
                {
                    Width      = _continuousPageW,
                    Height     = slotH,
                    Margin     = new Thickness(0, 0, 0, 12),
                    Background = Application.Current.TryFindResource("Background") as SolidColorBrush
                                 ?? new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                    Tag = i,
                    Child = slotGrid
                };
                int capturedI = i;
                placeholder.PreviewMouseLeftButtonDown += (_, _) => PageList.SelectedIndex = capturedI;
                _continuousPanel.Children.Add(placeholder);
                y += slotH + 12;
            }

            // Paint existing annotations onto the freshly built per-page overlays so they show
            // immediately. Without this they stayed invisible until the next tool/page change
            // happened to trigger a render for that page.
            foreach (var annotPage in _annotations.Keys.ToList())
                if (_continuousCanvases.ContainsKey(annotPage))
                    RenderAllAnnotations(annotPage);

            // Re-apply fit mode now that _continuousPageW is known; default to fit-page (one whole
            // page in view) unless the user explicitly chose fit-width.
            if (_fitMode == FitMode.Width) FitToWidth(); else FitToPage();

            _continuousScrollTarget = initialPage;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ScrollContinuousToPage(initialPage));

            _ = RenderContinuousPages();
        }

        private async System.Threading.Tasks.Task RenderContinuousPages()
        {
            if (_doc is null || _currentFile is null) return;
            _continuousRenderCts?.Cancel();
            _continuousRenderCts = new System.Threading.CancellationTokenSource();
            var cts = _continuousRenderCts;

            string currentFile = _currentFile;
            int pageCount      = _doc.PageCount;
            double targetW     = _continuousPageW;
            int renderW        = Math.Max(800, Math.Min(2048, (int)(targetW * 2)));

            // Capture per-page rotations on the UI thread before going async
            var rotations = new Dictionary<int, int>(_pageRotations);

            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    using var docReader = DocLib.Instance.GetDocReader(
                        currentFile, new PageDimensions(renderW, renderW * 2));

                    for (int i = 0; i < pageCount; i++)
                    {
                        if (cts.IsCancellationRequested) return;
                        using var pr = docReader.GetPageReader(i);
                        int w = pr.GetPageWidth();
                        int h = pr.GetPageHeight();
                        var raw = pr.GetImage();
                        if (w <= 0 || h <= 0 || raw is null) continue;
                        if (rotations.TryGetValue(i, out int rot) && rot != 0)
                            (raw, w, h) = RotateBitmap(raw, w, h, rot);

                        int fi = i, fw = w, fh = h;
                        byte[] bytes = raw;
                        if (cts.IsCancellationRequested) return;
                        // Use the window's own dispatcher, not Application.Current.Dispatcher: during app
                        // shutdown Application.Current goes null and this background render would NRE.
                        Dispatcher.Invoke(() =>
                        {
                            if (cts.IsCancellationRequested || _viewMode != ViewMode.Continuous) return;
                            if (fi >= _continuousPanel.Children.Count) return;

                            var slot = (Border)_continuousPanel.Children[fi];
                            double dipW = slot.Width;
                            double dipH = dipW * fh / fw;
                            double dpiX = 96.0 * fw / dipW;
                            double dpiY = 96.0 * fh / dipH;

                            var bmp = new WriteableBitmap(fw, fh, dpiX, dpiY, PixelFormats.Bgra32, null);
                            bmp.WritePixels(new Int32Rect(0, 0, fw, fh), bytes, fw * 4, 0);
                            bmp.Freeze();

                            if (slot.Child is Grid slotGrid && slotGrid.Children.Count > 0
                                && slotGrid.Children[0] is Image pageImg)
                            {
                                pageImg.Source  = bmp;
                                pageImg.Width   = dipW;
                                pageImg.Height  = dipH;
                                slot.Background = Brushes.White;

                                // Size the slot and overlay from the ACTUAL rendered page so a
                                // cropped page (which renders shorter than its MediaBox estimate)
                                // fills its slot with no white bars. Mirrors single-page view.
                                slot.Height = dipH;
                                double maxF = Math.Max(fw, fh);
                                int rdW = Math.Max(1, (int)Math.Round(2048.0 * fw / maxF));
                                int rdH = Math.Max(1, (int)Math.Round(2048.0 * fh / maxF));
                                _renderDims[fi] = (rdW, rdH);
                                if (slotGrid.Children.Count > 1 && slotGrid.Children[1] is Canvas ov)
                                {
                                    ov.Width  = rdW;
                                    ov.Height = rdH;
                                    ov.LayoutTransform =
                                        new System.Windows.Media.ScaleTransform(dipW / rdW, dipW / rdW);
                                }

                                // Slot heights are now exact; recompute scroll offsets from them.
                                double yy = 0;
                                for (int k = 0; k < _continuousPanel.Children.Count && k < _continuousTops.Count; k++)
                                {
                                    _continuousTops[k] = yy;
                                    double hk = ((FrameworkElement)_continuousPanel.Children[k]).Height;
                                    if (double.IsNaN(hk)) hk = 0;
                                    yy += hk + 12;
                                }

                                // Pages render in order, so when the target page is reached every
                                // page above it has its final height; re-scroll so a crop lands you
                                // back on the same page instead of drifting to the next one.
                                if (_continuousScrollTarget >= 0 && fi >= _continuousScrollTarget)
                                {
                                    int tgt = _continuousScrollTarget;
                                    _continuousScrollTarget = -1;
                                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                                        (Action)(() => ScrollContinuousToPage(tgt)));
                                }

                                RenderAllAnnotations(fi);
                            }
                        });
                    }
                }
                catch { /* render cancelled or doc closed */ }
            }, cts.Token);
        }
    }

    // ============================================================
    // Themed dialog — replaces MessageBox for dark-UI consistency
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
            MessageBoxImage image = MessageBoxImage.None)
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
            // AllowsTransparency windows can't use ClearType. Display mode pixel-snaps the (unscaled)
            // dialog text so it stays crisp, and Grayscale gives smooth anti-aliased edges - the best
            // combination available on a layered window.
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(win, TextRenderingMode.Grayscale);

            var outerBorder = new Border
            {
                Background      = R("BgModal"),
                BorderBrush     = R("AccentBorder"),   // match the app window / Settings card border, not the bright accent
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Margin          = new Thickness(10),    // transparent halo so the drop shadow can render
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.6
                }
            };

            var root = new StackPanel();

            // Title bar
            var titleBar = new Border
            {
                // Transparent so the dialog-wide film grain shows through the title bar too (it sits
                // over the same BgModal surface, so it still reads as one continuous surface).
                Background   = Brushes.Transparent,
                Padding      = new Thickness(16, 10, 16, 10),
                CornerRadius = new CornerRadius(5, 5, 0, 0)
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) win.DragMove(); };
            // When the title is just "KillerPDF", render it as the main window's wordmark - "Killer"
            // in the primary text color and "PDF" in the green logo accent, bold, with a soft shadow.
            if (title == "KillerPDF")
            {
                var wm = new StackPanel { Orientation = Orientation.Horizontal };
                wm.Children.Add(new TextBlock { Text = "Killer", FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"), FontWeight = FontWeights.Bold, FontSize = 15, Foreground = R("TextPrimary") });
                wm.Children.Add(new TextBlock { Text = "PDF",    FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft JhengHei UI, Nirmala UI"), FontWeight = FontWeights.Bold, FontSize = 15, Foreground = R("AccentLogo") });
                wm.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.6 };
                titleBar.Child = wm;
            }
            else
            {
                titleBar.Child = new TextBlock
                {
                    Text       = title,
                    Foreground = R("Accent"),
                    FontWeight = FontWeights.SemiBold,
                    FontSize   = 13,
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
                    Text         = message,
                    Foreground   = R("TextPrimary"),
                    FontSize     = 13,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            root.Children.Add(msgBorder);

            // Buttons
            var btnPanel = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
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
                    btnPanel.Children.Add(MakeBtn("OK",     MessageBoxResult.OK,     accent: true));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
                    break;
                case MessageBoxButton.YesNo:
                    btnPanel.Children.Add(MakeBtn("Yes", MessageBoxResult.Yes, accent: true));
                    btnPanel.Children.Add(MakeBtn("No",  MessageBoxResult.No));
                    break;
                case MessageBoxButton.YesNoCancel:
                    btnPanel.Children.Add(MakeBtn("Yes",    MessageBoxResult.Yes,    accent: true));
                    btnPanel.Children.Add(MakeBtn("No",     MessageBoxResult.No));
                    btnPanel.Children.Add(MakeBtn("Cancel", MessageBoxResult.Cancel));
                    break;
            }

            root.Children.Add(new Border
            {
                Padding = new Thickness(16, 8, 16, 16),
                Child   = btnPanel
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
                    CornerRadius     = new CornerRadius(6),
                    IsHitTestVisible = false,
                    Opacity          = grainOpacity,
                    Background = new System.Windows.Media.ImageBrush(grain)
                    {
                        TileMode      = System.Windows.Media.TileMode.Tile,
                        ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
                        Viewport      = new Rect(0, 0, 256, 256),
                        Stretch       = System.Windows.Media.Stretch.None
                    }
                });
            }
            contentGrid.Children.Add(root);
            outerBorder.Child = contentGrid;

            win.Content = outerBorder;
            win.ShowDialog();
            return result;
        }
    }
}
