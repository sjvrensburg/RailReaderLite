using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using RailReader.Core.Models;
using RailReaderLite.ViewModels;

namespace RailReaderLite.Views;

public partial class MainView : UserControl
{
    /// <summary>Start point of an in-flight drag selection, in image-local
    /// (control) coordinates. Stored in image-local rather than page-point
    /// space so <see cref="OnPagePointerMoved"/> can update the overlay
    /// rectangle without re-running the inverse mapping every frame.
    /// Null when no drag is active.</summary>
    private Point? _selectionStart;

    /// <summary>Free-pan state — set on Ctrl+pointer-down, cleared on
    /// pointer-up. While non-null, pointer-move drags the
    /// ScrollViewer's offset rather than the selection rect. Matches
    /// the desktop rail-reader's "Ctrl+drag temporarily exits rail
    /// mode" gesture.</summary>
    private Point? _panStart;
    private Vector _panInitialOffset;

    private MainViewModel? _subscribedVm;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm is not null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = DataContext as MainViewModel;
        if (_subscribedVm is not null)
            _subscribedVm.PropertyChanged += OnVmPropertyChanged;
        SyncRailOverlay();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ActiveBlockBoundsPagePoints):
            case nameof(MainViewModel.ActiveLineBoundsPagePoints):
            case nameof(MainViewModel.PageImage):
            case nameof(MainViewModel.RenderScale):
            case nameof(MainViewModel.IsRailMode):
                SyncRailOverlay();
                break;
        }
    }

    /// <summary>
    /// Pushes the VM's active-block / active-line page-point rects onto
    /// the Canvas overlay in image-local coords, and scrolls the
    /// ScrollViewer so the active line stays visible. Called whenever
    /// the VM signals a change or a new bitmap lands.
    /// </summary>
    private void SyncRailOverlay()
    {
        if (DataContext is not MainViewModel vm) return;

        // Suspend overlay during free pan — the user is exploring the
        // page; rail visuals would just be in the way.
        if (!vm.IsRailMode || _panStart is not null)
        {
            HideAllRailOverlays();
            return;
        }

        var blockRect = PageRectToCanvas(vm.ActiveBlockBoundsPagePoints);
        if (blockRect is { } b)
        {
            Canvas.SetLeft(ActiveBlockRect, b.X);
            Canvas.SetTop(ActiveBlockRect, b.Y);
            ActiveBlockRect.Width = b.Width;
            ActiveBlockRect.Height = b.Height;
            ActiveBlockRect.IsVisible = true;
        }
        else
        {
            ActiveBlockRect.IsVisible = false;
        }

        var lineRect = PageRectToCanvas(vm.ActiveLineBoundsPagePoints);
        if (lineRect is { } l)
        {
            Canvas.SetLeft(ActiveLineRect, l.X);
            Canvas.SetTop(ActiveLineRect, l.Y);
            ActiveLineRect.Width = l.Width;
            ActiveLineRect.Height = l.Height;
            ActiveLineRect.IsVisible = true;
            UpdateFocusMask(l);
            ScrollToCanvasRect(l);
        }
        else
        {
            ActiveLineRect.IsVisible = false;
            HideFocusMask();
        }
    }

    private void HideAllRailOverlays()
    {
        ActiveBlockRect.IsVisible = false;
        ActiveLineRect.IsVisible = false;
        HideFocusMask();
    }

    private void HideFocusMask()
    {
        FocusMaskTop.IsVisible = false;
        FocusMaskBottom.IsVisible = false;
        FocusMaskLeft.IsVisible = false;
        FocusMaskRight.IsVisible = false;
    }

    /// <summary>
    /// Positions four dim-mask rects around the active line so the
    /// line itself stays bright and everything else gets darkened.
    /// Mask dimensions are derived from the displayed bitmap size
    /// (not the Canvas's reported bounds, which may lag during
    /// resize). The mask sits underneath the active-line rect in
    /// z-order so the line's bright tint stays intact.
    /// </summary>
    private void UpdateFocusMask(Avalonia.Rect line)
    {
        if (PageImageView.Source is not Bitmap bmp)
        {
            HideFocusMask();
            return;
        }
        // Displayed image size — same calc as PageRectToCanvas. Mask
        // covers (0,0) to (displayedW, displayedH) minus the line rect.
        double scaleX = PageImageView.Bounds.Width  / bmp.PixelSize.Width;
        double scaleY = PageImageView.Bounds.Height / bmp.PixelSize.Height;
        double displayScale = Math.Min(scaleX, scaleY);
        if (displayScale <= 0) { HideFocusMask(); return; }
        if (displayScale > 1) displayScale = 1;
        double displayedW = bmp.PixelSize.Width  * displayScale;
        double displayedH = bmp.PixelSize.Height * displayScale;
        double offsetX = (PageImageView.Bounds.Width  - displayedW) / 2.0;
        double offsetY = (PageImageView.Bounds.Height - displayedH) / 2.0;

        // Top strip: from image-top down to line-top.
        Canvas.SetLeft(FocusMaskTop, offsetX);
        Canvas.SetTop (FocusMaskTop, offsetY);
        FocusMaskTop.Width  = displayedW;
        FocusMaskTop.Height = Math.Max(0, line.Y - offsetY);
        FocusMaskTop.IsVisible = FocusMaskTop.Height > 0;

        // Bottom strip: from line-bottom to image-bottom.
        Canvas.SetLeft(FocusMaskBottom, offsetX);
        Canvas.SetTop (FocusMaskBottom, line.Y + line.Height);
        FocusMaskBottom.Width  = displayedW;
        FocusMaskBottom.Height = Math.Max(0, (offsetY + displayedH) - (line.Y + line.Height));
        FocusMaskBottom.IsVisible = FocusMaskBottom.Height > 0;

        // Left strip: image-left to line-left, at line's vertical band.
        Canvas.SetLeft(FocusMaskLeft, offsetX);
        Canvas.SetTop (FocusMaskLeft, line.Y);
        FocusMaskLeft.Width  = Math.Max(0, line.X - offsetX);
        FocusMaskLeft.Height = line.Height;
        FocusMaskLeft.IsVisible = FocusMaskLeft.Width > 0;

        // Right strip: line-right to image-right, at line's vertical band.
        Canvas.SetLeft(FocusMaskRight, line.X + line.Width);
        Canvas.SetTop (FocusMaskRight, line.Y);
        FocusMaskRight.Width  = Math.Max(0, (offsetX + displayedW) - (line.X + line.Width));
        FocusMaskRight.Height = line.Height;
        FocusMaskRight.IsVisible = FocusMaskRight.Width > 0;
    }

    /// <summary>
    /// Maps a page-point rect to the Canvas coordinate system, which
    /// equals the Image's image-local (control) space because both
    /// share a Grid cell with the same alignment. Returns null when
    /// the bitmap isn't loaded yet or the rect is degenerate.
    /// </summary>
    private Avalonia.Rect? PageRectToCanvas(RectF? pageRect)
    {
        if (pageRect is null) return null;
        if (PageImageView.Source is not Bitmap bmp) return null;
        if (DataContext is not MainViewModel vm) return null;
        if (vm.RenderScale <= 0) return null;
        if (PageImageView.Bounds.Width <= 0 || PageImageView.Bounds.Height <= 0) return null;

        // Mirror of LocalToPagePoint: page-point → bitmap-pixel →
        // image-local. With Stretch=None (Zoom > 1), the displayed
        // scale is 1:1 with bitmap pixels. With Stretch=Uniform/DownOnly
        // (Zoom == 1), the bitmap is uniformly down-scaled to fit.
        double scaleX = PageImageView.Bounds.Width  / bmp.PixelSize.Width;
        double scaleY = PageImageView.Bounds.Height / bmp.PixelSize.Height;
        double displayScale = Math.Min(scaleX, scaleY);
        if (displayScale <= 0) return null;
        if (displayScale > 1) displayScale = 1;

        var pr = pageRect.Value;
        double bx1 = pr.Left   * vm.RenderScale;
        double by1 = pr.Top    * vm.RenderScale;
        double bx2 = pr.Right  * vm.RenderScale;
        double by2 = pr.Bottom * vm.RenderScale;

        double displayedW = bmp.PixelSize.Width  * displayScale;
        double displayedH = bmp.PixelSize.Height * displayScale;
        double offsetX = (PageImageView.Bounds.Width  - displayedW) / 2.0;
        double offsetY = (PageImageView.Bounds.Height - displayedH) / 2.0;

        double x = bx1 * displayScale + offsetX;
        double y = by1 * displayScale + offsetY;
        double w = (bx2 - bx1) * displayScale;
        double h = (by2 - by1) * displayScale;
        if (w <= 0 || h <= 0) return null;
        return new Avalonia.Rect(x, y, w, h);
    }

    /// <summary>Vertical fraction of the viewport where the active
    /// line is anchored — 1/3 from the top keeps it visible with
    /// some context above and more reading space below.</summary>
    private const double RailAnchorFraction = 1.0 / 3.0;

    /// <summary>
    /// Anchored-cursor scrolling. Instead of <c>BringIntoView</c>
    /// (which snaps the line to "just visible" and lets it drift
    /// around the viewport as the user advances), we compute the
    /// scroll offset that places the active line at a fixed fraction
    /// of the viewport height. The page scrolls smoothly underneath
    /// while the reading position stays put — much closer to how the
    /// desktop rail-reader feels.
    /// </summary>
    private void ScrollToCanvasRect(Avalonia.Rect canvasRect)
    {
        _ = canvasRect;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            AnchorActiveLine, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void AnchorActiveLine()
    {
        if (!ActiveLineRect.IsVisible) return;
        var sv = PageScroller;
        var rectTopInViewport = ActiveLineRect.TranslatePoint(new Point(0, 0), sv);
        if (rectTopInViewport is null) return;
        // Position in content-space = position in viewport + current offset.
        double lineYInContent = rectTopInViewport.Value.Y + sv.Offset.Y;
        double targetOffsetY = lineYInContent - RailAnchorFraction * sv.Viewport.Height;
        double maxOffset = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        double clamped = Math.Clamp(targetOffsetY, 0, maxOffset);
        _ = AnimateScrollY(clamped);
    }

    private const double ScrollAnimationDurationMs = 180.0;
    private System.Threading.CancellationTokenSource? _scrollCts;

    /// <summary>
    /// Cubic ease-out animated scroll to <paramref name="targetY"/>.
    /// Cancels any in-flight animation so rapid ↓ presses fold into
    /// one smooth motion rather than queuing. Steps at ~16ms (60fps)
    /// — in WASM single-thread that's still cooperative, but
    /// PDF.js running in its Web Worker keeps the UI thread free
    /// enough that frames land on time.
    /// </summary>
    private async Task AnimateScrollY(double targetY)
    {
        _scrollCts?.Cancel();
        _scrollCts = new System.Threading.CancellationTokenSource();
        var ct = _scrollCts.Token;
        var sv = PageScroller;
        double startY = sv.Offset.Y;
        if (Math.Abs(targetY - startY) < 0.5) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested) return;
                double elapsed = sw.Elapsed.TotalMilliseconds;
                double t = Math.Min(1.0, elapsed / ScrollAnimationDurationMs);
                double eased = 1.0 - Math.Pow(1.0 - t, 3.0);  // cubic ease-out
                sv.Offset = new Vector(sv.Offset.X, startY + (targetY - startY) * eased);
                if (t >= 1.0) return;
                await Task.Delay(16, ct);
            }
        }
        catch (TaskCanceledException) { /* superseded */ }
    }

    /// <summary>
    /// Intercepts arrow / Home / End keys at the root when rail mode is
    /// active so the ScrollViewer's default scrolling doesn't fight the
    /// line nav. On the user's first rail-mode keystroke we ask the VM
    /// to enter rail mode near the top of the current viewport — that
    /// way the user continues reading from where they zoomed in rather
    /// than jumping to the first line of the page.
    /// </summary>
    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!vm.IsRailMode) return;
        if (e.Source is TextBox) return;  // don't steal keys from search box

        // Once the UserControl took focus we became the only place
        // arrow keys go — the ScrollViewer's default handler never
        // sees them, so we have to act on ←/→ ourselves. ↑/↓/Home/End
        // are rail-nav; ←/→ are horizontal page-scroll at any zoom.
        bool isRailKey = e.Key is Key.Down or Key.Up or Key.Home or Key.End;
        bool isHScrollKey = e.Key is Key.Left or Key.Right;
        if (!isRailKey && !isHScrollKey) return;

        if (isHScrollKey)
        {
            HScroll(e.Key == Key.Right ? HorizontalScrollStep : -HorizontalScrollStep);
            e.Handled = true;
            return;
        }

        // Enter rail mode near the visible region on first keystroke.
        if (vm.CurrentBlockIndex < 0)
        {
            var pageY = ViewportTopInPagePoints(vm);
            if (pageY is { } y) vm.EnterRailModeNearPageY(y);
        }

        switch (e.Key)
        {
            case Key.Down:
                if (vm.RailNextLineCommand.CanExecute(null)) vm.RailNextLineCommand.Execute(null);
                break;
            case Key.Up:
                if (vm.RailPrevLineCommand.CanExecute(null)) vm.RailPrevLineCommand.Execute(null);
                break;
            case Key.Home:
                if (vm.RailFirstLineOfPageCommand.CanExecute(null))
                    vm.RailFirstLineOfPageCommand.Execute(null);
                break;
            case Key.End:
                if (vm.RailLastLineOfPageCommand.CanExecute(null))
                    vm.RailLastLineOfPageCommand.Execute(null);
                break;
        }
        e.Handled = true;
    }

    private const double HorizontalScrollStep = 80.0;

    private void HScroll(double delta)
    {
        var sv = PageScroller;
        double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        double newX = Math.Clamp(sv.Offset.X + delta, 0, maxX);
        if (Math.Abs(newX - sv.Offset.X) < 0.5) return;
        sv.Offset = new Vector(newX, sv.Offset.Y);
    }

    /// <summary>Returns the page-point coordinates of the viewport
    /// centre, or null if rendering isn't ready. Used after a free-pan
    /// release to re-snap the rail cursor.</summary>
    private (float X, float Y)? ViewportCenterInPagePoints(MainViewModel vm)
    {
        if (PageImageView.Source is not Bitmap bmp) return null;
        if (vm.RenderScale <= 0) return null;
        if (PageImageView.Bounds.Width <= 0 || PageImageView.Bounds.Height <= 0) return null;

        var imageTopLeft = PageImageView.TranslatePoint(new Point(0, 0), PageScroller);
        if (imageTopLeft is null) return null;

        double viewportCenterXInVp = PageScroller.Viewport.Width  / 2.0;
        double viewportCenterYInVp = PageScroller.Viewport.Height / 2.0;
        double imageLocalX = viewportCenterXInVp - imageTopLeft.Value.X;
        double imageLocalY = viewportCenterYInVp - imageTopLeft.Value.Y;

        double scaleX = PageImageView.Bounds.Width  / bmp.PixelSize.Width;
        double scaleY = PageImageView.Bounds.Height / bmp.PixelSize.Height;
        double displayScale = Math.Min(scaleX, scaleY);
        if (displayScale <= 0) return null;
        if (displayScale > 1) displayScale = 1;

        double bitmapX = imageLocalX / displayScale;
        double bitmapY = imageLocalY / displayScale;
        return ((float)(bitmapX / vm.RenderScale), (float)(bitmapY / vm.RenderScale));
    }

    /// <summary>
    /// Returns the page-point Y at the top of the current viewport, or
    /// null if the bitmap/render isn't ready yet. Walks the Image's
    /// top-left through TranslatePoint to ScrollViewer-local coords,
    /// then inverts the image-local → page-point mapping.
    /// </summary>
    private float? ViewportTopInPagePoints(MainViewModel vm)
    {
        if (PageImageView.Source is not Bitmap bmp) return null;
        if (vm.RenderScale <= 0) return null;
        if (PageImageView.Bounds.Width <= 0 || PageImageView.Bounds.Height <= 0) return null;

        var imageTop = PageImageView.TranslatePoint(new Point(0, 0), PageScroller);
        if (imageTop is null) return null;

        // ScrollViewer's local (0, 0) is the top of the viewport. The
        // image-local Y at the top of the viewport is therefore the
        // negative of the image's Y in ScrollViewer-local coords (when
        // the image extends above the viewport).
        double imageLocalY = -imageTop.Value.Y;
        if (imageLocalY < 0) imageLocalY = 0;

        // image-local → bitmap-pixel inverse of LocalToPagePoint.
        double scaleX = PageImageView.Bounds.Width  / bmp.PixelSize.Width;
        double scaleY = PageImageView.Bounds.Height / bmp.PixelSize.Height;
        double displayScale = Math.Min(scaleX, scaleY);
        if (displayScale <= 0) return null;
        if (displayScale > 1) displayScale = 1;

        double bitmapY = imageLocalY / displayScale;
        return (float)(bitmapY / vm.RenderScale);
    }

    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Image img) return;
        if (!e.GetCurrentPoint(img).Properties.IsLeftButtonPressed) return;
        var local = e.GetPosition(img);
        if (local.X < 0 || local.Y < 0 ||
            local.X > img.Bounds.Width || local.Y > img.Bounds.Height) return;

        // Ctrl held → free pan (temporarily exit rail mode). Selection
        // rect stays hidden; rail overlay is hidden by SyncRailOverlay
        // for the duration via the _panStart check.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _panStart = local;
            _panInitialOffset = PageScroller.Offset;
            SelectionRect.IsVisible = false;
            ActiveLineRect.IsVisible = false;
            ActiveBlockRect.IsVisible = false;
            e.Pointer.Capture(img);
            return;
        }

        _selectionStart = local;
        SelectionRect.IsVisible = false;
        e.Pointer.Capture(img);
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Image img) return;

        // Free pan: drag the ScrollViewer's offset by the pointer
        // delta. Negative delta because moving the pointer right
        // should reveal content to the right (offset increases).
        if (_panStart is { } panStart)
        {
            var cur = e.GetPosition(img);
            var sv = PageScroller;
            double maxX = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
            double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            sv.Offset = new Vector(
                Math.Clamp(_panInitialOffset.X - (cur.X - panStart.X), 0, maxX),
                Math.Clamp(_panInitialOffset.Y - (cur.Y - panStart.Y), 0, maxY));
            return;
        }

        if (_selectionStart is not { } start) return;
        var c = e.GetPosition(img);
        double x = Math.Min(start.X, c.X);
        double y = Math.Min(start.Y, c.Y);
        double w = Math.Abs(c.X - start.X);
        double h = Math.Abs(c.Y - start.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
        SelectionRect.IsVisible = w > 1 || h > 1;
    }

    /// <summary>Max pointer delta (in image-local pixels) below which a
    /// release is treated as a click rather than a drag. Below this
    /// threshold we snap the rail cursor to the clicked line instead
    /// of starting a clipboard write.</summary>
    private const double ClickVsDragThreshold = 4.0;

    private async void OnPagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Image img) return;

        // Pan release: snap the rail cursor to the line nearest the
        // viewport centre so the user picks up reading at the
        // spatially-correct place rather than wherever they were
        // before the pan started.
        if (_panStart is not null)
        {
            _panStart = null;
            e.Pointer.Capture(null);
            if (DataContext is MainViewModel vm)
            {
                var centerPage = ViewportCenterInPagePoints(vm);
                if (centerPage is { } cp)
                {
                    vm.EnterRailModeNearPagePoint(cp.X, cp.Y);
                }
            }
            return;
        }

        if (_selectionStart is not { } start) return;
        e.Pointer.Capture(null);
        SelectionRect.IsVisible = false;
        try
        {
            var end = e.GetPosition(img);
            double dx = Math.Abs(end.X - start.X);
            double dy = Math.Abs(end.Y - start.Y);
            if (DataContext is not MainViewModel vm) return;

            // Click (no meaningful drag): snap the rail cursor to the
            // line nearest the clicked point. No clipboard work.
            if (dx < ClickVsDragThreshold && dy < ClickVsDragThreshold)
            {
                var clickPage = LocalToPagePoint(img, end);
                if (clickPage is not null)
                {
                    vm.EnterRailModeNearPagePoint(
                        (float)clickPage.Value.X, (float)clickPage.Value.Y);
                }
                return;
            }

            // Drag: extract selection + write to clipboard inside the
            // user-gesture stack frame (browser authorisation).
            var anchorPage = LocalToPagePoint(img, start);
            var endPage = LocalToPagePoint(img, end);
            if (anchorPage is null || endPage is null) return;

            var selected = vm.GetSelectedText(
                anchorPage.Value.X, anchorPage.Value.Y,
                endPage.Value.X, endPage.Value.Y);

            bool clipboardOk = false;
            if (!string.IsNullOrEmpty(selected))
            {
                clipboardOk = await TryWriteClipboardAsync(selected);
            }
            vm.ReportSelectionResult(selected, clipboardOk);
        }
        finally
        {
            _selectionStart = null;
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> to the system clipboard via
    /// Avalonia 12's <see cref="IDataTransfer"/> model. Called from the
    /// View so we can resolve <c>TopLevel</c> off the control (works in
    /// every Avalonia hosting model, including
    /// <c>IActivityApplicationLifetime</c> used by Avalonia.Browser).
    /// Returns true if the write was dispatched without throwing;
    /// the browser may still reject the write asynchronously (e.g. if
    /// no permission), in which case the user sees the status text but
    /// nothing lands in the clipboard.
    /// </summary>
    private async Task<bool> TryWriteClipboardAsync(string text)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            var clipboard = top?.Clipboard;
            if (clipboard is null) return false;

            var item = new DataTransferItem();
            item.SetText(text);
            var transfer = new DataTransfer();
            transfer.Add(item);
            await clipboard.SetDataAsync(transfer);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Maps a pointer position on the <see cref="Image"/> control to a
    /// page-point coordinate. Handles the two layered scales:
    /// <list type="number">
    ///   <item>image-local → bitmap-pixel via the Stretch=Uniform display
    ///   scale derived from Image.Bounds vs the natural bitmap size, and</item>
    ///   <item>bitmap-pixel → page-point via <c>1 / VM.RenderScale</c>.</item>
    /// </list>
    /// </summary>
    private static (double X, double Y)? LocalToPagePoint(Image img, Point local)
    {
        if (img.Source is not Bitmap bmp || img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
            return null;

        // Stretch=Uniform with DownOnly: image displayed at natural size
        // unless the viewport is smaller, in which case it's scaled
        // uniformly down to fit. The displayed pixel size equals the
        // bitmap's natural size scaled by the smaller of two ratios.
        double scaleX = img.Bounds.Width  / bmp.PixelSize.Width;
        double scaleY = img.Bounds.Height / bmp.PixelSize.Height;
        double scale  = Math.Min(scaleX, scaleY);
        if (scale <= 0) return null;
        // Don't upscale (StretchDirection=DownOnly) — clamp to 1.
        if (scale > 1) scale = 1;

        // The Image control centers the bitmap within its bounds when
        // the bitmap is smaller than the bounds; account for that.
        double displayedW = bmp.PixelSize.Width  * scale;
        double displayedH = bmp.PixelSize.Height * scale;
        double offsetX = (img.Bounds.Width  - displayedW) / 2.0;
        double offsetY = (img.Bounds.Height - displayedH) / 2.0;

        double bitmapX = (local.X - offsetX) / scale;
        double bitmapY = (local.Y - offsetY) / scale;
        if (bitmapX < 0 || bitmapY < 0 ||
            bitmapX > bmp.PixelSize.Width || bitmapY > bmp.PixelSize.Height)
            return null;

        if (DataContextAs<MainViewModel>(img) is not { } vm) return null;
        if (vm.RenderScale <= 0) return null;

        return (bitmapX / vm.RenderScale, bitmapY / vm.RenderScale);
    }

    private static T? DataContextAs<T>(Control c) where T : class
    {
        // Walk up to find a DataContext of the right type — the Image is
        // bound to MainViewModel via the root UserControl's DataContext.
        Control? cur = c;
        while (cur is not null)
        {
            if (cur.DataContext is T vm) return vm;
            cur = cur.Parent as Control;
        }
        return null;
    }

    /// <summary>
    /// Ctrl+wheel zooms in/out. We swallow the event in that case so the
    /// surrounding <c>ScrollViewer</c> doesn't also scroll on the same
    /// gesture. Plain wheel (no Ctrl) bubbles up to the scroller as
    /// before.
    /// </summary>
    private void OnPagePointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is not MainViewModel vm) return;
        if (e.Delta.Y > 0)
        {
            if (vm.ZoomInCommand.CanExecute(null)) vm.ZoomInCommand.Execute(null);
        }
        else if (e.Delta.Y < 0)
        {
            if (vm.ZoomOutCommand.CanExecute(null)) vm.ZoomOutCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            if (vm.SearchCommand.CanExecute(null))
                vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
