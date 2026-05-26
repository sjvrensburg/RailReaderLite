using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core.Services;
using RailReader.Renderer.PdfPigSkia;

namespace RailReaderLite.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IPdfServiceFactory _factory = new PdfPigSkiaPdfServiceFactory();

    /// <summary>Target longest-edge pixel size for page rendering. ~1200 fits
    /// most laptop viewports without over-rasterising.</summary>
    private const int RenderTargetSize = 1200;

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
    [NotifyCanExecuteChangedFor(nameof(PrevCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private int _pageCount;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    private string _statusText = "Open a PDF to begin.";

    [ObservableProperty]
    private bool _isBusy;

    public bool HasDocument => _pdf is not null && PageCount > 0;
    public bool CanPrev => HasDocument && CurrentPage > 0;
    public bool CanNext => HasDocument && CurrentPage < PageCount - 1;

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

            // PdfPigSkiaPdfService takes a file path. Drop the bytes
            // into the WASM/process temp dir so a single string argument
            // is sufficient; future API may add a byte[] overload to
            // PdfPigSkiaPdfService and this dance goes away.
            var tempPath = Path.Combine(Path.GetTempPath(),
                $"railreaderlite_{System.Guid.NewGuid():N}.pdf");
            await File.WriteAllBytesAsync(tempPath, bytes);

            // IPdfService is intentionally not IDisposable — each
            // render call opens its own document internally; the service
            // holds no long-lived resources besides PdfBytes.
            _pdf = _factory.CreatePdfService(tempPath);
            PageCount = _pdf?.PageCount ?? 0;
            CurrentPage = 0;
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
