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

        if (!vm.IsRailMode)
        {
            ActiveBlockRect.IsVisible = false;
            ActiveLineRect.IsVisible = false;
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
            ScrollToCanvasRect(l);
        }
        else
        {
            ActiveLineRect.IsVisible = false;
        }
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

    /// <summary>
    /// Scrolls the surrounding <see cref="ScrollViewer"/> so the active
    /// line rectangle is fully visible. We let Avalonia's built-in
    /// <c>BringIntoView</c> walk the visual tree — it handles the
    /// Canvas → Grid → ScrollViewer translation, including the 16-px
    /// Grid margin, the centring alignment, and the current zoom.
    /// </summary>
    private void ScrollToCanvasRect(Avalonia.Rect canvasRect)
    {
        _ = canvasRect;  // rect already pushed onto ActiveLineRect; BringIntoView reads layout
        // Defer until after the layout pass so the rect's position
        // reflects the just-set Canvas.Left/Top values.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => ActiveLineRect.BringIntoView(),
            Avalonia.Threading.DispatcherPriority.Background);
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

        bool isRailKey = e.Key is Key.Down or Key.Up or Key.Left or Key.Right or Key.Home or Key.End;
        if (!isRailKey) return;

        // Enter rail mode near the visible region on first keystroke.
        if (vm.CurrentBlockIndex < 0)
        {
            var pageY = ViewportTopInPagePoints(vm);
            if (pageY is { } y) vm.EnterRailModeNearPageY(y);
        }

        switch (e.Key)
        {
            case Key.Down:
            case Key.Right:
                if (vm.RailNextLineCommand.CanExecute(null)) vm.RailNextLineCommand.Execute(null);
                break;
            case Key.Up:
            case Key.Left:
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
        _selectionStart = local;
        SelectionRect.IsVisible = false;
        e.Pointer.Capture(img);
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Image img) return;
        if (_selectionStart is not { } start) return;
        var cur = e.GetPosition(img);
        double x = Math.Min(start.X, cur.X);
        double y = Math.Min(start.Y, cur.Y);
        double w = Math.Abs(cur.X - start.X);
        double h = Math.Abs(cur.Y - start.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
        SelectionRect.IsVisible = w > 1 || h > 1;
    }

    private async void OnPagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Image img) return;
        if (_selectionStart is not { } start) return;
        e.Pointer.Capture(null);
        SelectionRect.IsVisible = false;
        try
        {
            var end = e.GetPosition(img);
            var anchorPage = LocalToPagePoint(img, start);
            var endPage = LocalToPagePoint(img, end);
            if (anchorPage is null || endPage is null) return;
            if (DataContext is not MainViewModel vm) return;

            // Compute the selection synchronously inside the
            // user-gesture stack frame so the browser still
            // considers the clipboard write authorised.
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
