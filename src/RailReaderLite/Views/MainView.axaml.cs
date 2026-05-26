using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using RailReaderLite.ViewModels;

namespace RailReaderLite.Views;

public partial class MainView : UserControl
{
    /// <summary>Start point of an in-flight drag selection, in page-point
    /// coordinates. Null when no drag is active.</summary>
    private (double X, double Y)? _selectionAnchor;

    public MainView()
    {
        InitializeComponent();
    }

    private void OnPagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Image img) return;
        if (!e.GetCurrentPoint(img).Properties.IsLeftButtonPressed) return;
        var pt = ToPagePoint(img, e);
        if (pt is null) return;
        _selectionAnchor = pt;
        e.Pointer.Capture(img);
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        // No live-highlight during drag in v0.5.0 — repainting the
        // bitmap on every mouse move would burn 50–100 ms per event.
        // Future PR can add a Canvas overlay for live feedback.
        _ = sender; _ = e;
    }

    private void OnPagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Image img) return;
        if (_selectionAnchor is not { } anchor) return;
        e.Pointer.Capture(null);
        try
        {
            var end = ToPagePoint(img, e);
            if (end is null) return;
            if (DataContext is MainViewModel vm)
                vm.CompleteSelection(anchor.X, anchor.Y, end.Value.X, end.Value.Y);
        }
        finally
        {
            _selectionAnchor = null;
        }
    }

    /// <summary>
    /// Maps a pointer event on the <see cref="Image"/> control to a
    /// page-point coordinate. Handles the two layered scales:
    /// <list type="number">
    ///   <item>image-local → bitmap-pixel via the Stretch=Uniform display
    ///   scale derived from Image.Bounds vs the natural bitmap size, and</item>
    ///   <item>bitmap-pixel → page-point via <c>1 / VM.RenderScale</c>.</item>
    /// </list>
    /// </summary>
    private static (double X, double Y)? ToPagePoint(Image img, PointerEventArgs e)
    {
        if (img.Source is not Bitmap bmp || img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
            return null;

        // Stretch=Uniform with DownOnly: image displayed at natural size
        // unless the viewport is smaller, in which case it's scaled
        // uniformly down to fit. The displayed pixel size equals the
        // bitmap's natural size scaled by the smaller of two ratios.
        double scaleX = img.Bounds.Width  / bmp.PixelSize.Width;
        double scaleY = img.Bounds.Height / bmp.PixelSize.Height;
        double scale  = System.Math.Min(scaleX, scaleY);
        if (scale <= 0) return null;
        // Don't upscale (StretchDirection=DownOnly) — clamp to 1.
        if (scale > 1) scale = 1;

        // The Image control centers the bitmap within its bounds when
        // the bitmap is smaller than the bounds; account for that.
        double displayedW = bmp.PixelSize.Width  * scale;
        double displayedH = bmp.PixelSize.Height * scale;
        double offsetX = (img.Bounds.Width  - displayedW) / 2.0;
        double offsetY = (img.Bounds.Height - displayedH) / 2.0;

        var pos = e.GetPosition(img);
        double bitmapX = (pos.X - offsetX) / scale;
        double bitmapY = (pos.Y - offsetY) / scale;
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
