# Changelog

All notable changes to KillerPDF are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.8] - 2026-06-25

### Added
- Line tool: drag to draw straight lines, with its own color, opacity, and width.
- Resizable, word-wrapping text boxes (double-click to re-edit) with an optional background fill for whiteouts, color, and opacity.
- Updated highlighter and draw bars with color and opacity controls.
- Select tool moves and resizes any annotation, Shift+click to multi-select, and reopens an annotation's bar to restyle it in place.
- Full RGB color picker on every swatch row: saturation/value square, hue strip, RGB/hex inputs, a screen eyedropper, and an editable palette.
- Tabbed documents: open several PDFs at once, each restoring its page, zoom, and view mode. Drag tabs to re-order.
- Recent files: a dropdown by Open (last 10) and on the start screen, plus a Save / Save As dropdown.
- One-click update from the About dialog when a newer release exists.
- Print options: scale, position, margins, pages per sheet, color / black-and-white, and two-sided.
- Page-number stamping from the right-click menu (start value, format, position, size) as one undo.
- OCR built into the single exe (Tesseract): OCR a whole page or a dragged region to the clipboard, Make Searchable PDF (an invisible text layer over the scan), and Extract All Text to a .txt or .md file. A language picker downloads extra languages on demand, with an optional high-quality model toggle.
- Transform tool: rotate in 90-degree steps or by a fine angle, scale, flip, and straighten a crooked scan by drawing a line along anything that should be level, all with a live preview. Annotations on the page follow the transform.
- "Clear all Data" link in the About window to wipe settings, downloaded OCR language models, and temp files.
- Per-field font size while filling text fields, baked into the saved PDF.
- Digital signatures with a cloud certificate (Certum SimplySign): reusable signatures and initials, and click-to-sign form fields.
- Movable Signatures popup, its position remembered.
- Toolbar style picker: small or large icons, text beside, under, or only.
- Sidebar on the left or right, with the collapse toggle, splitter, and Settings flyout mirroring to match.
- Resizable sidebar: drag the splitter to scale the page list and thumbnails; drag to open or close, or toggle with Ctrl+B.
- Accent colors (red, orange, green, teal, blue, purple) for the Dark, Light, and Black themes, each remembered independently.
- Keyboard shortcuts for tools, views, and panels (F1 shortcuts list, F2 About, Ctrl+V paste, Esc to close, f5-f8 view modes, f11 fullscreen...); the overlay lists them all.
- Bengali, Turkish, Simplified Chinese, German, and French translations (contributors akib-h #79, mrantikadev #76, KaneLeung #82, Dtrieb & Gevlug #93, Thalis-fr #95).

### Changed
- Visual refresh.
- Blood, Greed, and Cyanotic use darker chrome with a lighter document pane; the signature windows are fully themed and reload on theme change.
- Settings is now a slide-out accordion (Language, Theme, Toolbar, View Mode, Sidebar) that stays open after a pick.
- Text-over-text editing drops an opaque cover (fill sampled from the page) with an editable box on top; the pair can be unpaired, and image-only pages get a manual cover and box.
- Grid and Two-Page pages render sharper on high-DPI displays.
- Restored sessions load tabs lazily, and placed images no longer re-decode while being dragged.
- Save Flattened opens the source PDF once instead of per page (Issue #68).
- Internal refactor: the ~15,000-line MainWindow code-behind split into ~40 focused partial-class files, no behavior change.
- Unified the page-rendering pipeline so annotations, search highlights, and tools behave identically across Single, Continuous, Two-Page, and Grid views.
- Crop tool rebuilt as a single docked, slidable bar matching the annotation bars.

### Fixed
- Form fields appear and fill in every view mode, align on pages with an inset CropBox or offset origin, and size their text from the field's own /DA.
- Undo removes one item per press; a held Ctrl+Z no longer fires several at once.
- Clear All Annotations clears every view mode as one undo; right-click Clear Page Annotations targets the correct page.
- Grid view: the wheel keeps scrolling after a zoom or column change, page jumps fit correctly (Issue #78), and annotations commit to the page they were drawn on.
- Opening an encrypted PDF or repairing a damaged one runs on a background thread instead of freezing the window.
- Printing and Save Flattened no longer crash on documents PdfSharpCore can't reopen; they use the same repair fallback as Save.
- A manually-closed PDF no longer reopens on next launch (Issue #75).
- The vertical scrollbar is grabbable again at the window edge.
- Search waits for a pause in typing before running; the Outlines panel scrolls and no longer auto-expands every branch.
- Pressing Esc during a long OCR, repair, or flatten operation asks whether to cancel instead of closing the window.
- Print Preview shows the rendering progress ("Rendering X / Y") on its own line above the page counter, and the preview window stays responsive while pages stream in.

## [1.5.1] - 2026-06-14

### Fixed
- PDFs that opened fine in browsers and Acrobat/Foxit but failed in KillerPDF with "Unexpected EOF" now open. PdfSharpCore rejected them during parsing; KillerPDF now falls back to re-saving the file losslessly through PDFium (which reads them) and opening that copy (Issue #72).
- Files opened from UNC / network shares (including the WSL `\\wsl$` filesystem) are now copied to a local temp before opening, avoiding partial-read failures on network filesystems.
- Grid view now renders every page, and tiles stream in progressively as they render instead of blocking until the whole document is done. Grid was previously capped at the first 26 pages, so longer documents stopped loading partway through.
- Ctrl+Scroll in grid view no longer re-renders every page when the zoom is already at its limit (the column count cannot change), which made large documents reload pointlessly.
- Lowered the minimum zoom from 10% to 5% so grid view can pack more columns (useful for wide/landscape pages) and single-page view can zoom out further.
- Removed a stray horizontal scrollbar (a thin green line) that appeared across the bottom of grid view; grid fits its columns to the window and no longer scrolls sideways.

### Changed
- Save Flattened PDF now rasterizes across multiple CPU cores. PNG encoding runs in parallel; the PDFium render step is serialized because the library is not thread-safe. Large documents flatten faster and the UI stays responsive (Issue #68).

## [1.5.0] - 2026-06-14

### Added
- Localization support (Issue #53 / contributor leox243). Language selector in Settings panel. Ships with English (en-US), Spanish (es), and Traditional Chinese (zh-TW). Theme names, zoom dropdown, fit-mode status, and keyboard shortcut overlay all update with the selected language. Contributor guide at `Strings/TRANSLATING.md`.
- Continuous scroll view mode. Opens all pages in a single vertical strip with progressive async rendering. Page number and sidebar thumbnail track automatically as you scroll.
- Two-page view mode. Displays two pages side-by-side (primary + one secondary). Editing tools are available in this mode.
- Re-edit placed text by double-clicking it with the Select tool. The text re-opens with its current content, size, and color; the size dropdown and color swatches restyle it live while editing.
- Per-monitor DPI v2 support. Window and page re-render correctly when dragging between monitors with different scale factors.
- Zoom +/− toolbar buttons and keyboard shortcuts (Ctrl+=, Ctrl+−, Ctrl+0, Ctrl+Scroll).
- Crop tool improvements (Issue #15): editable CropBox coordinates, page range apply, TrimBox sync, rotation-aware coordinate conversion, draggable confirm bar.
- Settings persistence - window size, zoom, and fit mode saved/restored on launch (Issue #69).
- Global crash handler with structured log files and recovery dialog.
- About dialog (click the version label in the status bar).
- Authenticode install gate, downgrade protection, and pdfium.dll integrity check.
- Theme system: Dark, Light, High Contrast, Blood, Greed, and Cyanotic themes with live switching and settings panel (gear icon)
- Grid view zoom fits a whole number of pages across the window. Ctrl+Scroll steps through column counts (3, 4, 5 and up) and the grid opens at three pages across.
- Built-in print dialog with working print preview. Replaces the Windows print dialog (which showed "This app doesn't support print preview") with a themed dialog that previews each page and exposes printer, orientation, copies, and page-range (for example 1-3,5) settings.

### Changed
- Continuous scroll is now the default view mode for new installs.
- View mode order in Settings: Continuous, Single Page, Two-Page, Grid.
- Settings and keyboard shortcut overlay borders widened to 2px for better visibility.
- Text tool size value is now interpreted as points. A size of 14 renders and exports as roughly 14pt instead of about 5pt of internal render units.
- Placing an image now switches to the Select tool with the image selected, so you can immediately drag to reposition or use the corner handle to resize instead of the next click reopening the image picker (matching signature placement).
- Extracted SignatureStore and SearchService into Services/ with unit tests (KillerPDF.Tests).
- Encrypted PDF temp files written to `%LOCALAPPDATA%\KillerPDF\Temp\` instead of `%TEMP%`.
- Reopens last file on startup; ESC closes the app when no overlay is active (Issue #69).
- Grid view mode moved from a toolbar toggle to the Settings panel alongside Theme and Language. Four modes: Single Page, Continuous, Two-Page, Grid. Selection persists across sessions.
- Switching to Single or Two-Page view fits the page to the window, Continuous opens fit-to-width, and Grid opens at its column-fit default, rather than carrying the previous mode's zoom level.
- Annotation toolbars (text and draw size/color) now appear at the top-right under the toolbar buttons instead of the top-left.
- Four corner resize handles on placed images and signatures. Drag any corner to resize with the opposite corner held fixed. Handles are larger and render at the same on-screen size in every view mode.

### Fixed
- Stale debug string appearing in status bar after Fit Width in single-page mode.
- Text edit box closed when changing the font size, because the size dropdown took keyboard focus and triggered a commit. Focus moving into the size or color bar no longer commits the edit.
- Crop confirm bar was scaled down with page zoom, making it unreadable at low zoom levels. Selection rectangle improvements.
- Save Flattened PDF now runs on a background thread (Issue #68).
- Cropped pages rasterize at CropBox size instead of document-wide maximum (Issue #68).
- Temp files cleaned up on close, crash, and startup.
- Undo of a document change (crop, rotate, page operations) now re-renders the active view, so a page no longer keeps showing its pre-undo state while the sidebar shows the correct version.

---

## [1.4.3] - 2026-06-08

### Fixed
- Encrypted PDFs (owner-restricted RC4) no longer fail with "Unexpected token 'xref'" when rotating pages. PdfSharpCore can silently produce a broken cross-reference entry after saving encrypted files; KillerPDF now pipes the file through PDFium to repair the XRef and retries the open automatically.
- Page view now fits to page after a rotation so the full rotated page is visible without manual rezoom.
- Mailto and other link annotations with visible borders (e.g. colored rectangles that looked like strikethroughs) no longer render those borders in saved PDFs. KillerPDF strips `/AP`, `/C`, and `/BS` from link annotations and sets an invisible border on save.
- Right-click a link annotation to remove it from the PDF entirely ("Remove Link from PDF"). Previously, clearing annotations only removed the KillerPDF overlay; the native PDF link remained active.
- Right-click a mailto link to copy just the email address; right-click an http/https link to copy the URL.

---

## [1.4.2] - 2026-06-06

### Added
- PDF form filling. Interactive PDF forms now render their fields (text inputs, checkboxes, radio buttons) as live controls. Fill them in directly and save - field values are written back into the PDF.
- PDF outline (bookmark) support (Issue #63). A new OUTLINES tab in the sidebar displays the document's bookmark tree. Click any entry to jump to that page. The sidebar auto-fits its width to the longest entry on open and can be dragged wider; switching back to PAGES snaps to the pages-mode width.

### Fixed
- Page rotation no longer reverts after saving. Rotations applied via the sidebar context menu now persist correctly through the save pipeline.
- Copied text words were out of order on PDFs where glyphs are stored in non-reading order (Issue #66). Text extraction now sorts words by position and uses a dynamic line-grouping threshold so both drag-select and Select All produce correctly ordered output.
- PDFs with malformed or non-standard XRef tables now open in read-only mode instead of showing "Invalid entry in XRef table" and failing entirely.

---

## [1.4.1] - 2026-05-21

### Added
- Page number jump box in toolbar. Type a page number and press Enter to navigate directly to that page.
- Signature auto-selects after placing so you can immediately reposition or resize without switching tools.
- Zoom to Width / Fit Page now re-applies when the window is resized.
- Middle mouse button panning. Hold middle mouse and drag to pan the view in any direction.
- Multi-page grid view toggle (toolbar button left of the zoom dropdown). Switch between seeing all pages in a scrollable grid and a focused single-page view. Defaults to grid view on open.
- Ctrl+S saves directly to the current file without a dialog. Ctrl+Shift+S opens Save As.
- Arrow key navigation: Left/Up goes to the previous page, Right/Down goes to the next page.
- Keyboard shortcut overlay. Press Ctrl+? to show a full shortcut reference. Dismiss with Escape or by clicking outside the panel.
- Crop tool improvements: corner drag handles to resize the selection after drawing without having to redraw; Enter applies the crop to the current page; Escape cancels; Remove Crop / Remove All buttons in the confirm bar clear an existing CropBox from one page or all pages.

### Fixed
- Fit to Width and Fit Page zoomed incorrectly on HiDPI (4K) displays.
- Pages appeared blurry at higher zoom levels on HiDPI displays.
- Signature position drifted after saving.
- Memory spike (6+ GB) when opening large PDFs on HiDPI displays.
- Navigating pages caused multi-second UI lag on documents with many pages.
- Scroll wheel now navigates to the previous page when scrolled to the top of a page, and to the next page when scrolled to the bottom.

---

## [1.4.0] - 2026-05-16

### Added
- Rotate page (Issue #52). Right-click any page in the sidebar to rotate it 90° clockwise or counter-clockwise. Works on multi-page selections.
- Insert Image tool (Issue #50). Click the toolbar button, then click anywhere on the page to place a PNG, JPG, BMP, GIF, or TIFF as a resizable annotation. Drag the green corner handle to resize; burned into the PDF on save.
- PDF link annotation support (Issue #47). Clicking hyperlinks and internal cross-references in a PDF now navigates to the target page or opens the URL in the default browser. Works on both the primary page and all secondary pages in multi-page grid view.
- New Blank Document (Ctrl+N, toolbar button). Creates a single blank A4 page as a new working document. Prompts to discard unsaved changes if a dirty file is open.
- Typewriter tool font size picker. When the Text tool is active, a settings bar appears showing size presets (8–72pt) and a color palette. Size and color are stored per-annotation and applied when flattening to PDF.
- Insert Blank Page. Right-clicking any page in the sidebar now shows a context menu with page-level operations: insert a blank A4 page, move up/down, extract, or delete.
- Signature resize. Placed signatures now show a green drag handle in the bottom-right corner. Dragging it scales the signature proportionally; releasing commits the new size.
- Multi-page grid view. When viewing a page, subsequent pages render as a tiled grid to the right and below, allowing context across multiple pages at once.
- Fit to Width on open. Files now auto-zoom to fill the viewer width on open instead of opening at 100% and clipping wide pages.

### Fixed
- Scroll wheel in the main viewer no longer triggers page navigation. Previously, at low zoom levels where the page fit entirely in the viewport, every scroll tick caused a full page re-render.
- Page selection no longer flashes centered before jerking left. The layout width is now managed exclusively in the Dispatcher callback, eliminating the double layout pass that caused the visual artifact.
- "Back to TOC" and other internal links on secondary pages now navigate to the correct target instead of advancing to the next sequential page.
- Clicking an internal link now scrolls the viewer back to the top of the target page so links pointing to page tops (e.g. TOC back-links) land correctly.
- Internal PDF links now survive a merge. When merging PDFs, named destinations from the source document's catalog are resolved and rewritten as explicit page-object references in the merged document, so TOC and cross-reference links continue to work after merging.
- Multi-page grid content is now centered in the viewport instead of left-aligned. Panel width is snapped to a whole number of page-width slots so HorizontalAlignment=Center has room to work.
- Sidebar page list no longer shows empty space after the last page. The list now ends at the final page entry with no trailing dead zone.

### Changed
- Theme updated to match killertools.net: accent green changed from `#4ade80` to `#1ea54c`, backgrounds shifted to `#333333`/`#3a3a3a`, sidebar darkened to `#222222`, toolbar and title bar at `#222222`. Film grain overlay added to the main content area. Footer text lightened for readability.
- Sidebar scroll is now handled by an outer ScrollViewer wrapping the page list, allowing the list to size to its content rather than stretching to fill the panel height.

## [1.3.2] - 2026-05-11

### Fixed
- Windows Program Compatibility Assistant popup on first launch. Added an app manifest declaring Windows 10/11 compatibility, which suppresses PCA when the app writes to uninstall registry keys.
- "Set as default PDF viewer" prompt now only appears if KillerPDF is not already the default handler. Previously showed on every install/update regardless.
- "Set as default PDF viewer" prompt now uses the dark KillerDialog instead of a native Windows message box.

## [1.3.1] - 2026-05-11

### Fixed
- Print no longer fails with "No application is associated with the specified file for this action" on systems where Edge is the default PDF handler. Printing now uses WPF-native rendering and PrintDialog instead of the shell print verb.
- Zoom dropdown selected value no longer shows in blue - selection highlight now uses the accent green.

## [1.3.0] - 2026-05-08

### Added
- Image signatures. Import a PNG, JPG, or BMP as a reusable signature instead of drawing one. Stored alongside drawn signatures and flattens into the PDF on save.
- Close File (Ctrl+W). Close the current document without quitting the app. Prompts if there are unsaved changes.
- Unsaved-changes protection. The title bar marks dirty files with `*` and prompts before closing or opening a new file with unsaved edits.
- Full-document Find. Ctrl+F search now scans the entire PDF and cycles through all matches, not just the current page.
- Zoom preset dropdown with quick presets (50%, 75%, 100%, 125%, 150%, 200%). Scroll-wheel zoom syncs the box, including non-preset levels.

### Fixed
- Scrolling past the bottom of a page now advances to the next page; scrolling past the top goes back.
- Re-dropping a PDF onto the window after a file is already open now works correctly.
- Owner-password-protected PDFs now open correctly (previously only user-password was handled).
- Dragging the title bar while maximized now correctly restores and moves the window.
- Delete confirmation now reads "Delete 1 page?" or "Delete 2 pages?" instead of "Delete N page(s)?".
- Signature delete button showed a rectangle glyph instead of an X.

### Changed
- All dialog boxes are now fully dark-themed via a custom dialog window. No more native Windows popups.
- Create Signature dialog now uses a dark custom chrome title bar with a red X close button.
- Button hover states and page thumbnail hover in the sidebar are now green instead of the default Windows blue.
- Toolbar icons overhauled: Open Folder, Close File, Move Up, Move Down, Extract Pages, and Merge PDFs all use cleaner glyphs.

## [1.2.1] - 2026-05-04

### Changed
- Code signed with Certum certificate. Windows now shows a verified publisher instead of unknown.
- Cleaned up footer.

## [1.2.0] - 2026-04-24

### Added
- Self-installing EXE. Running the downloaded binary now shows an Install / Run dialog. Install copies the EXE to `%LOCALAPPDATA%\Programs\KillerPDF\` (no UAC required), creates Start Menu and optional Desktop shortcuts, registers as a PDF file handler, and adds an uninstall entry to Add/Remove Programs. Uninstall self-deletes via a deferred batch file. Running a newer version from outside the install path shows an Update prompt instead.
- Command-line file argument support so file associations work: `KillerPDF.exe "file.pdf"` opens the file directly.
- Password-protected PDF support. Opening an encrypted PDF now prompts for the password instead of showing a generic error. The decrypted copy is held in a temp file for the session so all rendering and editing works normally.
- Save Flattened PDF (photo icon in toolbar). Rasterizes every page at 150 DPI via PDFium and writes them as embedded images into a new PDF, producing a fully uneditable document. Pending annotations are burned in before rasterization.

## [1.1.1] - 2026-04-18

### Fixed
- Maximize no longer covers the Windows taskbar. Added a `WM_GETMINMAXINFO` hook so the frameless window clamps to the monitor's work area (multi-monitor aware).
- Two `CS8602` nullability warnings in the font-name cleanup path.

## [1.1.0] - 2026-04-16

### Changed
- Retargeted from .NET 8 to .NET Framework 4.8 so end users no longer need to install a separate .NET runtime.
- Forced 64-bit build via `PlatformTarget=x64`.
- Added PolySharp polyfills for modern C# language features on net48.
- Replaced `Math.Clamp` calls with `Math.Min`/`Math.Max` equivalents.

### Added
- Post-publish MSBuild target that automatically bundles a GPL3-compliant source zip alongside the published EXE.
- CHANGELOG.md.

