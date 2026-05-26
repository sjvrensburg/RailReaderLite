using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.PdfPigSkia;

namespace RailReaderLite.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    /// <summary>Target longest-edge pixel size for page rendering. Higher
    /// gives the Stretch=Uniform Image control more pixels to downscale
    /// from when the viewport is large; 1600 covers most laptop screens
    /// without crushing pdfpig render times on a Pi-class CPU.</summary>
    private const int RenderTargetSize = 1600;

    private IPdfService? _pdf;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    [NotifyPropertyChangedFor(nameof(CanPrev))]
    [NotifyPropertyChangedFor(nameof(CanNext))]
    [NotifyCanExecuteChangedFor(nameof(PrevCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private int _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    [NotifyPropertyChangedFor(nameof(CanPrev))]
    [NotifyPropertyChangedFor(nameof(CanNext))]
    [NotifyPropertyChangedFor(nameof(HasDocument))]
    [NotifyPropertyChangedFor(nameof(HasOutline))]
    [NotifyCanExecuteChangedFor(nameof(PrevCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private int _pageCount;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    private string _statusText = "Open a PDF to begin.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutline))]
    private System.Collections.Generic.List<OutlineEntry> _outline = [];

    [ObservableProperty]
    private bool _outlineVisible;

    /// <summary>
    /// Two-way bound to the outline TreeView. Setting this navigates to
    /// the entry's page if it has one. Container nodes (no page) are
    /// selectable but don't trigger navigation.
    /// </summary>
    public OutlineEntry? SelectedOutlineEntry
    {
        get => _selectedOutlineEntry;
        set
        {
            if (SetProperty(ref _selectedOutlineEntry, value) && value?.Page is int page)
                _ = NavigateToPageAsync(page);
        }
    }
    private OutlineEntry? _selectedOutlineEntry;

    public bool HasDocument => _pdf is not null && PageCount > 0;
    public bool CanPrev => HasDocument && CurrentPage > 0;
    public bool CanNext => HasDocument && CurrentPage < PageCount - 1;
    public bool HasOutline => Outline.Count > 0;

    public string PageLabel =>
        HasDocument ? $"{CurrentPage + 1} / {PageCount}" : "—";

    [RelayCommand]
    private async Task OpenAsync(IStorageProvider? storageProvider)
    {
        if (storageProvider is null) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open PDF",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.Pdf],
        });
        if (files.Count == 0) return;

        IsBusy = true;
        StatusText = "Loading…";
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            var bytes = memory.ToArray();

            // Release the previous document deterministically — Core 0.7.1
            // made PdfPigSkiaPdfService IDisposable so we can drop the
            // cached PdfDocument without waiting for GC.
            if (_pdf is IDisposable disposable) disposable.Dispose();

            // 0.7.1: byte[] ctor on PdfPigSkiaPdfService drops the
            // temp-file hop that Lite previously needed.
            _pdf = new PdfPigSkiaPdfService(bytes);
            PageCount = _pdf.PageCount;
            CurrentPage = 0;
            Outline = _pdf.Outline;
            OutlineVisible = Outline.Count > 0;
            StatusText = files[0].Name;
            await RenderCurrentPageAsync();
        }
        catch (System.Exception ex)
        {
            StatusText = $"Failed to open: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPrev))]
    private async Task PrevAsync()
    {
        if (!CanPrev) return;
        CurrentPage--;
        await RenderCurrentPageAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync()
    {
        if (!CanNext) return;
        CurrentPage++;
        await RenderCurrentPageAsync();
    }

    [RelayCommand]
    private void ToggleOutline() => OutlineVisible = !OutlineVisible;

    private async Task NavigateToPageAsync(int zeroBasedPage)
    {
        if (!HasDocument) return;
        if (zeroBasedPage < 0 || zeroBasedPage >= PageCount) return;
        if (zeroBasedPage == CurrentPage) return;
        CurrentPage = zeroBasedPage;
        await RenderCurrentPageAsync();
    }

    private Task RenderCurrentPageAsync()
    {
        if (_pdf is null) return Task.CompletedTask;
        var pdf = _pdf;
        var page = CurrentPage;

        return Task.Run(() =>
        {
            try
            {
                var (rgb, width, height) = pdf.RenderPagePixmap(page, RenderTargetSize);
                var bmp = RgbToAvaloniaBitmap(rgb, width, height);
                Dispatcher.UIThread.Post(() => PageImage = bmp);
            }
            catch (System.Exception ex)
            {
                Dispatcher.UIThread.Post(() => StatusText = $"Render failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Packs an RGB byte[] (3 bytes/pixel, from <see cref="IPdfService.RenderPagePixmap"/>)
    /// into an Avalonia <see cref="WriteableBitmap"/> with full-alpha
    /// Bgra8888 layout. No unsafe blocks — uses <see cref="Marshal.Copy"/>
    /// from a temp byte[] for portability across the WASM target.
    /// </summary>
    private static WriteableBitmap RgbToAvaloniaBitmap(byte[] rgb, int width, int height)
    {
        var bmp = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        int pixelCount = width * height;
        var bgra = new byte[pixelCount * 4];
        for (int i = 0; i < pixelCount; i++)
        {
            int s = i * 3;
            int d = i * 4;
            bgra[d]     = rgb[s + 2]; // B
            bgra[d + 1] = rgb[s + 1]; // G
            bgra[d + 2] = rgb[s];     // R
            bgra[d + 3] = 0xFF;       // A
        }

        using var frame = bmp.Lock();
        Marshal.Copy(bgra, 0, frame.Address, bgra.Length);
        return bmp;
    }
}

// Tiny indirection so we don't have to import the full Avalonia.Threading
// namespace at the top — keeps the using list focused on the data path.
internal static class Dispatcher
{
    public static Avalonia.Threading.Dispatcher UIThread => Avalonia.Threading.Dispatcher.UIThread;
}
