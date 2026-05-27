using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
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

    public MainView()
    {
        InitializeComponent();
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
