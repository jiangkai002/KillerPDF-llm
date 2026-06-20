using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF
{
    // Configurable sidebar placement (left or right). The layout uses three columns in
    // MainContentGrid: one sized sidebar column, a 6px splitter, and a star document column.
    // ApplySidebarSide swaps which outer column is the sidebar and repoints _sidebarCol so the
    // existing collapse / resize logic keeps working unchanged.
    public partial class MainWindow
    {
        private void ApplySidebarSide()
        {
            var sidebarColDef = FindName("SidebarCol") as ColumnDefinition;
            var docColDef     = FindName("DocCol") as ColumnDefinition;
            var sbOuter       = FindName("SidebarOuterGrid") as Grid;
            var sbContent     = FindName("SidebarBorder") as Border;
            var sbToggle      = FindName("SidebarToggleStrip") as Border;
            var docPane       = FindName("DocPaneBorder") as Border;
            var sbContentCol  = FindName("SbContentCol") as ColumnDefinition;
            var sbToggleCol   = FindName("SbToggleCol") as ColumnDefinition;
            if (sidebarColDef == null || docColDef == null || sbOuter == null || sbContent == null ||
                sbToggle == null || docPane == null || sbContentCol == null || sbToggleCol == null)
                return;

            // Carry the sized column's current width across a flip (24px when collapsed, else the
            // user's width). A star length means it isn't the sized column yet, so fall back.
            GridLength sized = (_sidebarCol != null && _sidebarCol.Width.GridUnitType == GridUnitType.Pixel)
                ? _sidebarCol.Width
                : new GridLength(180);
            double maxW = _sidebarShowingOutlines ? SidebarMaxOutlines : SidebarMaxPages;

            if (!_sidebarRight)
            {
                // Sidebar on the LEFT (column 0); document fills column 2.
                sidebarColDef.MinWidth = 24; sidebarColDef.MaxWidth = maxW; sidebarColDef.Width = sized;
                docColDef.MinWidth = 0; docColDef.MaxWidth = double.PositiveInfinity;
                docColDef.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(sbOuter, 0);
                Grid.SetColumn(docPane, 2);
                _sidebarCol = sidebarColDef;
                // Toggle strip faces the document: right edge of the sidebar.
                sbContentCol.Width = new GridLength(1, GridUnitType.Star);
                sbToggleCol.Width  = new GridLength(24, GridUnitType.Pixel);
                Grid.SetColumn(sbContent, 0);
                Grid.SetColumn(sbToggle, 1);
            }
            else
            {
                // Sidebar on the RIGHT (column 2); document fills column 0.
                docColDef.MinWidth = 24; docColDef.MaxWidth = maxW; docColDef.Width = sized;
                sidebarColDef.MinWidth = 0; sidebarColDef.MaxWidth = double.PositiveInfinity;
                sidebarColDef.Width = new GridLength(1, GridUnitType.Star);
                Grid.SetColumn(sbOuter, 2);
                Grid.SetColumn(docPane, 0);
                _sidebarCol = docColDef;
                // Toggle strip faces the document: left edge of the sidebar (inner column 0). The
                // inner column defs are fixed in position, so size them by position, not by name.
                sbContentCol.Width = new GridLength(24, GridUnitType.Pixel);   // inner col 0 -> toggle
                sbToggleCol.Width  = new GridLength(1, GridUnitType.Star);     // inner col 1 -> content
                Grid.SetColumn(sbToggle, 0);
                Grid.SetColumn(sbContent, 1);
            }

            // The splitter's 1px divider line faces the document: on its right for a left
            // sidebar, on its left for a right sidebar.
            if (FindName("SidebarSplitter") is GridSplitter splitter)
                splitter.BorderThickness = _sidebarRight ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);

            UpdateSidebarToggleGlyph();
            ApplySettingsPanelSide();
            UpdateTabStripFade();
        }

        // Clip the tab-strip shadow gradient to the document column so it never falls over the
        // sidebar (on whichever side the sidebar sits).
        private void UpdateTabStripFade()
        {
            double edge = (_sidebarCol?.ActualWidth ?? 180) + 6;   // sidebar column + 6px splitter
            var m = _sidebarRight ? new Thickness(0, 0, edge, 0) : new Thickness(edge, 0, 0, 0);
            if (TabStripFade != null) TabStripFade.Margin = m;
            if (FooterFade != null) FooterFade.Margin = m;   // mirrored shadow below the document
            ApplyTabFadeFeather();
        }

        private void ApplyTabFadeFeather()
        {
            FeatherFade(TabStripFade);
            FeatherFade(FooterFade);
        }

        // Soften the sidebar-facing edge of a shadow gradient so it fades out over ~20px instead of
        // ending in a hard vertical line. (The footer fade is flipped vertically, but this horizontal
        // mask is applied before that transform, so the same logic works for both.)
        private void FeatherFade(Border? fade)
        {
            if (fade == null) return;
            double w = fade.ActualWidth;
            if (w <= 1) { fade.OpacityMask = null; return; }
            double f = Math.Max(0.02, Math.Min(0.4, 20.0 / w));
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            if (_sidebarRight)
            {
                mask.GradientStops.Add(new GradientStop(Colors.White, 0));
                mask.GradientStops.Add(new GradientStop(Colors.White, 1 - f));
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            }
            else
            {
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                mask.GradientStops.Add(new GradientStop(Colors.White, f));
                mask.GradientStops.Add(new GradientStop(Colors.White, 1));
            }
            fade.OpacityMask = mask;
        }

        // The collapse arrow points toward where the page-list content goes when toggled, which
        // depends on both the side and the collapsed state.
        private void UpdateSidebarToggleGlyph()
        {
            if (_sidebarToggleBtn == null) return;
            bool pointLeft = _sidebarRight ? _sidebarCollapsed : !_sidebarCollapsed;
            _sidebarToggleBtn.Content = pointLeft ? "" : "";   // ChevronLeft / ChevronRight
        }

        // Mirror the settings slide-out panel so it opens toward the document on whichever side the
        // sidebar sits. PositionSettingsPanel sets the matching margin when the panel is shown.
        private void ApplySettingsPanelSide()
        {
            var ha      = _sidebarRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            var corners = _sidebarRight ? new CornerRadius(6, 0, 0, 6) : new CornerRadius(0, 6, 6, 0);

            if (SettingsPanel != null) SettingsPanel.HorizontalAlignment = ha;

            foreach (var name in new[] { "SettingsCardShadow", "SettingsCardGrain", "SettingsCardBorder" })
            {
                if (FindName(name) is Border b)
                {
                    b.HorizontalAlignment = ha;
                    b.CornerRadius = corners;
                }
            }
            // The bordered card omits its border on the edge that sits against the sidebar.
            if (FindName("SettingsCardBorder") is Border card)
                card.BorderThickness = _sidebarRight ? new Thickness(1, 1, 0, 1) : new Thickness(0, 1, 1, 1);
            // Shadow falls away from the sidebar (toward the document).
            if (FindName("SettingsCardShadow") is Border shadow &&
                shadow.Effect is System.Windows.Media.Effects.DropShadowEffect ds)
                ds.Direction = _sidebarRight ? 180 : 0;
        }

        private void SelectSidebarSide(bool right)
        {
            if (SidebarCurrentLabel != null) SidebarCurrentLabel.Text = right ? "Right" : "Left";
            if (right == _sidebarRight) return;   // no change (e.g. radio sync on panel open)
            _sidebarRight = right;
            App.SetSetting("SidebarSide", right ? "Right" : "Left");
            ApplySidebarSide();
            // Re-anchor the open panel AFTER the flipped layout has measured, so the new sidebar
            // column's ActualWidth (used for the margin) is current.
            if (SettingsOverlay != null && SettingsOverlay.Visibility == Visibility.Visible)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => { ApplySettingsPanelSide(); PositionSettingsPanel(); }));
        }

        private void SidebarLeftRadio_Checked(object sender, RoutedEventArgs e)  => SelectSidebarSide(false);
        private void SidebarRightRadio_Checked(object sender, RoutedEventArgs e) => SelectSidebarSide(true);
    }
}
