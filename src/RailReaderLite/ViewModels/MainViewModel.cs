using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core.Models;
using RailReader.Core.PdfPig;
using RailReader.Core.Services;
using RailReader.Renderer.PdfPigSkia;

namespace RailReaderLite.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    /// <summary>Longest-edge pixel size used at render time. Re-render this
    /// many pixels in each direction; the View's Stretch=Uniform/DownOnly
    /// then scales the result to fit the viewport.</summary>
    private const int RenderTargetSize = 1600;

    private const byte HighlightCurrentMatchR = 255;
    private const byte HighlightCurrentMatchG = 200;
    private const byte HighlightCurrentMatchB = 0;
    private const byte HighlightOtherMatchR  = 255;
    private const byte HighlightOtherMatchG  = 235;
    private const byte HighlightOtherMatchB  = 130;
    private const byte HighlightSelectionR   = 120;
    private const byte HighlightSelectionG   = 180;
    private const byte HighlightSelectionB   = 255;

    private IPdfService? _pdf;
    private readonly IPdfTextService _textService = new PdfTextService();

    /// <summary>Lazy per-page text cache. PdfPig.ExtractPageText re-opens
    /// the document on each call (Core.PdfPig.PdfTextService doesn't yet
    /// hold a cached PdfDocument the way the renderer does in 0.7.1), so
    /// extraction is comparatively expensive; cache after first use.</summary>
    private readonly Dictionary<int, PageText> _pageTextCache = new();

    /// <summary>Search hits across all pages. Each entry is a glyph-rect
    /// list in bitmap-pixel coordinates (relative to a page rendered at
    /// <see cref="RenderTargetSize"/>) for one match on one page.</summary>
    private readonly List<SearchHit> _searchHits = [];

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
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private int _pageCount;

    [ObservableProperty]
    private Bitmap? _pageImage;

    [ObservableProperty]
    private string _statusText = "Open a PDF to begin.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutline))]
    private List<OutlineEntry> _outline = [];

    [ObservableProperty]
    private bool _outlineVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _matchStatus = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextMatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrevMatchCommand))]
    private int _currentMatchIndex = -1;

    [ObservableProperty]
    private int _matchCount;

    /// <summary>
    /// Render scale that maps page-points to bitmap pixels for the
    /// current page. View code uses this to translate pointer coords
    /// into page-point coords for selection.
    /// </summary>
    [ObservableProperty]
    private float _renderScale = 1f;

    [ObservableProperty]
    private double _currentPagePointWidth;

    [ObservableProperty]
    private double _currentPagePointHeight;

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

            if (_pdf is IDisposable disposable) disposable.Dispose();

            _pdf = new PdfPigSkiaPdfService(bytes);
            PageCount = _pdf.PageCount;
            CurrentPage = 0;
            Outline = _pdf.Outline;
            OutlineVisible = Outline.Count > 0;
            StatusText = files[0].Name;
            _pageTextCache.Clear();
            ClearSearchHits();
            await RenderCurrentPageAsync();
        }
        catch (Exception ex)
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

    private bool CanSearch() => HasDocument && !string.IsNullOrWhiteSpace(SearchQuery);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (_pdf is null) return;
        ClearSearchHits();
        var query = SearchQuery;

        // Scan every page for the query. Builds glyph-rect lists in
        // bitmap-pixel coordinates so the render path can paint them
        // without knowing the search semantics.
        for (int p = 0; p < PageCount; p++)
        {
            var text = GetOrExtractPageText(p);
            if (text.Text.Length == 0) continue;

            int idx = 0;
            while (idx < text.Text.Length)
            {
                int matchStart = text.Text.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
                if (matchStart < 0) break;
                int matchLen = query.Length;

                // Hit rects in PAGE-POINT space, one rect per visually
                // contiguous run; we cluster glyphs by Y-band like the
                // line tokeniser pattern.
                var pagePointRects = ClusterGlyphsToRects(text, matchStart, matchLen);
                _searchHits.Add(new SearchHit(p, matchStart, matchLen, pagePointRects));
                idx = matchStart + Math.Max(1, matchLen);
            }
        }

        MatchCount = _searchHits.Count;
        if (MatchCount == 0)
        {
            MatchStatus = "No matches";
            CurrentMatchIndex = -1;
        }
        else
        {
            CurrentMatchIndex = 0;
            MatchStatus = $"1 / {MatchCount}";
            await NavigateToPageAsync(_searchHits[0].PageIndex);
        }
        await RenderCurrentPageAsync();
    }

    private bool CanGoNextMatch() => MatchCount > 0;
    private bool CanGoPrevMatch() => MatchCount > 0;

    [RelayCommand(CanExecute = nameof(CanGoNextMatch))]
    private async Task NextMatchAsync()
    {
        if (MatchCount == 0) return;
        CurrentMatchIndex = (CurrentMatchIndex + 1) % MatchCount;
        MatchStatus = $"{CurrentMatchIndex + 1} / {MatchCount}";
        await NavigateToMatchAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevMatch))]
    private async Task PrevMatchAsync()
    {
        if (MatchCount == 0) return;
        CurrentMatchIndex = (CurrentMatchIndex - 1 + MatchCount) % MatchCount;
        MatchStatus = $"{CurrentMatchIndex + 1} / {MatchCount}";
        await NavigateToMatchAsync();
    }

    private async Task NavigateToMatchAsync()
    {
        var hit = _searchHits[CurrentMatchIndex];
        if (hit.PageIndex != CurrentPage)
            await NavigateToPageAsync(hit.PageIndex);
        else
            await RenderCurrentPageAsync();  // repaint with new current-match highlight
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = "";
        ClearSearchHits();
        _ = RenderCurrentPageAsync();
    }

    private void ClearSearchHits()
    {
        _searchHits.Clear();
        MatchCount = 0;
        CurrentMatchIndex = -1;
        MatchStatus = "";
    }

    /// <summary>
    /// Called by the View when the user finishes a drag selection. All
    /// four coordinates are in page-point space (origin top-left,
    /// Y-down) — the View handles the bitmap-pixel ↔ image-local
    /// conversions and reports page-points to the VM. Returns the
    /// extracted text in reading order, or null if no glyph midpoints
    /// fell inside the drag rect. The View is responsible for pushing
    /// the result to the clipboard inside its own user-gesture frame
    /// (Avalonia.Browser's <see cref="Avalonia.Controls.ApplicationLifetimes.IActivityApplicationLifetime"/>
    /// makes <c>TopLevel</c> accessible to the View but not via
    /// <c>Application.Current.ApplicationLifetime</c> on the VM side).
    /// </summary>
    public string? GetSelectedText(double pageX1, double pageY1, double pageX2, double pageY2)
    {
        if (!HasDocument) return null;
        var text = GetOrExtractPageText(CurrentPage);
        if (text.Text.Length == 0) return null;

        float l = (float)Math.Min(pageX1, pageX2);
        float r = (float)Math.Max(pageX1, pageX2);
        float t = (float)Math.Min(pageY1, pageY2);
        float b = (float)Math.Max(pageY1, pageY2);

        // Reject degenerate / single-click selections.
        if (r - l < 1f && b - t < 1f) return null;

        return text.ExtractTextInRect(l, t, r, b);
    }

    /// <summary>
    /// View calls this after the clipboard write completes so the VM
    /// can surface the result in the toolbar status line.
    /// </summary>
    public void ReportSelectionResult(string? selectedText, bool clipboardOk)
    {
        if (string.IsNullOrEmpty(selectedText))
            StatusText = "No text in selection.";
        else if (clipboardOk)
            StatusText = $"Copied {selectedText.Length} characters.";
        else
            StatusText = $"Selected {selectedText.Length} characters (clipboard unavailable).";
    }

    private async Task NavigateToPageAsync(int zeroBasedPage)
    {
        if (!HasDocument) return;
        if (zeroBasedPage < 0 || zeroBasedPage >= PageCount) return;
        if (zeroBasedPage == CurrentPage) return;
        CurrentPage = zeroBasedPage;
        await RenderCurrentPageAsync();
    }

    private PageText GetOrExtractPageText(int pageIndex)
    {
        if (_pageTextCache.TryGetValue(pageIndex, out var cached)) return cached;
        if (_pdf is null) return new PageText("", []);
        var extracted = _textService.ExtractPageText(_pdf.PdfBytes, pageIndex);
        _pageTextCache[pageIndex] = extracted;
        return extracted;
    }

    /// <summary>
    /// Clusters character boxes in a given index range into one rect
    /// per visual line — same pattern as
    /// <c>PdfTextService.GetTextRangeRects</c>, inlined here so search
    /// can share the per-page cache.
    /// </summary>
    private static List<RectF> ClusterGlyphsToRects(PageText text, int charStart, int charLength)
    {
        var rects = new List<RectF>();
        int end = Math.Min(text.Text.Length, charStart + charLength);
        RectF? current = null;
        float currentMidY = 0f;
        float currentLineHeight = 1f;

        foreach (var cb in text.CharBoxes)
        {
            if (cb.Index < charStart || cb.Index >= end) continue;
            float midY = (cb.Top + cb.Bottom) / 2f;
            float lineHeight = Math.Max(1f, cb.Bottom - cb.Top);
            if (current is null)
            {
                current = new RectF(cb.Left, cb.Top, cb.Right, cb.Bottom);
                currentMidY = midY;
                currentLineHeight = lineHeight;
            }
            else if (Math.Abs(midY - currentMidY) > currentLineHeight * 0.5f)
            {
                rects.Add(current.Value);
                current = new RectF(cb.Left, cb.Top, cb.Right, cb.Bottom);
                currentMidY = midY;
                currentLineHeight = lineHeight;
            }
            else
            {
                var c = current.Value;
                current = new RectF(
                    Math.Min(c.Left, cb.Left),
                    Math.Min(c.Top, cb.Top),
                    Math.Max(c.Right, cb.Right),
                    Math.Max(c.Bottom, cb.Bottom));
            }
        }
        if (current is not null) rects.Add(current.Value);
        return rects;
    }

    private Task RenderCurrentPageAsync()
    {
        if (_pdf is null) return Task.CompletedTask;
        var pdf = _pdf;
        var page = CurrentPage;
        var searchHitsForPage = _searchHits.Where(h => h.PageIndex == page).ToList();
        var currentMatch = (CurrentMatchIndex >= 0 && CurrentMatchIndex < _searchHits.Count)
            ? _searchHits[CurrentMatchIndex] : null;

        return Task.Run(() =>
        {
            try
            {
                var (pageW, pageH) = pdf.GetPageSize(page);
                float scale = (float)(RenderTargetSize / Math.Max(pageW, pageH));
                Dispatcher.UIThread.Post(() =>
                {
                    RenderScale = scale;
                    CurrentPagePointWidth = pageW;
                    CurrentPagePointHeight = pageH;
                });

                var (rgb, width, height) = pdf.RenderPagePixmap(page, RenderTargetSize);

                // Paint highlights INTO the rgb buffer so the View doesn't
                // need a separate overlay (and so coordinate conversion
                // stays in one place — page-points → bitmap pixels by
                // multiplying by `scale`).
                foreach (var hit in searchHitsForPage)
                {
                    bool isCurrent = ReferenceEquals(hit, currentMatch);
                    var (r, g, b) = isCurrent
                        ? (HighlightCurrentMatchR, HighlightCurrentMatchG, HighlightCurrentMatchB)
                        : (HighlightOtherMatchR,  HighlightOtherMatchG,  HighlightOtherMatchB);
                    foreach (var pageRect in hit.PagePointRects)
                        PaintRectBlend(rgb, width, height, pageRect, scale, r, g, b, 0.5f);
                }

                var bmp = RgbToAvaloniaBitmap(rgb, width, height);
                Dispatcher.UIThread.Post(() => PageImage = bmp);
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => StatusText = $"Render failed: {ex.Message}");
            }
        });
    }

    private static void PaintRectBlend(byte[] rgb, int w, int h, RectF pageRect, float pageToBitmap,
        byte tintR, byte tintG, byte tintB, float alpha)
    {
        int x1 = Math.Max(0, (int)(pageRect.Left  * pageToBitmap));
        int y1 = Math.Max(0, (int)(pageRect.Top   * pageToBitmap));
        int x2 = Math.Min(w, (int)(pageRect.Right * pageToBitmap));
        int y2 = Math.Min(h, (int)(pageRect.Bottom* pageToBitmap));
        if (x2 <= x1 || y2 <= y1) return;

        float inv = 1f - alpha;
        for (int y = y1; y < y2; y++)
        {
            int rowStart = y * w * 3;
            for (int x = x1; x < x2; x++)
            {
                int idx = rowStart + x * 3;
                rgb[idx]     = (byte)(rgb[idx]     * inv + tintR * alpha);
                rgb[idx + 1] = (byte)(rgb[idx + 1] * inv + tintG * alpha);
                rgb[idx + 2] = (byte)(rgb[idx + 2] * inv + tintB * alpha);
            }
        }
    }

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
            bgra[d]     = rgb[s + 2];
            bgra[d + 1] = rgb[s + 1];
            bgra[d + 2] = rgb[s];
            bgra[d + 3] = 0xFF;
        }

        using var frame = bmp.Lock();
        Marshal.Copy(bgra, 0, frame.Address, bgra.Length);
        return bmp;
    }

    private sealed record SearchHit(int PageIndex, int CharStart, int CharLength, List<RectF> PagePointRects);
}

internal static class Dispatcher
{
    public static Avalonia.Threading.Dispatcher UIThread => Avalonia.Threading.Dispatcher.UIThread;
}
