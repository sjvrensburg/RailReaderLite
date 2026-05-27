using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReaderLite.Services;

namespace RailReaderLite.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    /// <summary>Longest-edge pixel size at Zoom=1.0. The effective render
    /// size is <see cref="BaseRenderTargetSize"/> × <see cref="Zoom"/>,
    /// rendered into the bitmap so the page stays sharp under manual
    /// zoom. At Zoom=1.0 the View uses Stretch=Uniform/DownOnly so a big
    /// bitmap shrinks to fit the viewport (current behaviour); above
    /// 1.0 it switches to Stretch=None so the bitmap is shown at its
    /// natural pixel size and the ScrollViewer scrolls.</summary>
    private const int BaseRenderTargetSize = 1600;

    private const double MinZoom = 1.0;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 1.25;

    private const byte HighlightCurrentMatchR = 255;
    private const byte HighlightCurrentMatchG = 200;
    private const byte HighlightCurrentMatchB = 0;
    private const byte HighlightOtherMatchR  = 255;
    private const byte HighlightOtherMatchG  = 235;
    private const byte HighlightOtherMatchB  = 130;
    private const byte HighlightSelectionR   = 120;
    private const byte HighlightSelectionG   = 180;
    private const byte HighlightSelectionB   = 255;

    /// <summary>Zoom level at which rail mode auto-engages. Below this
    /// the page is meant to be read as a whole; above it the user is
    /// already magnified into a chunk of the page and benefits from
    /// line-by-line locked navigation.</summary>
    private const double RailModeZoomThreshold = 1.4;

    /// <summary>PDF.js-backed session. Replaces the previous
    /// PdfPig+SkiaSharp stack — PDF.js runs in a Web Worker (free
    /// background thread) and uses hardware-accelerated Canvas2D
    /// rasterisation, which is what fixes the v0.6.0 sluggishness
    /// for real. Constructed by the Browser entry point's
    /// JSInterop layer via <see cref="PdfJsRuntimeRegistry.Current"/>.</summary>
    private PdfJsSession? _session;

    private readonly IReadingOrderResolver _resolver = new XYCutPlusPlusResolver();

    /// <summary>Per-page analysis result cache. Blocks are stored in
    /// reading order with their <c>Lines</c> populated.</summary>
    private readonly Dictionary<int, PageAnalysis> _analysisCache = new();

    /// <summary>Tracks in-flight background analysis tasks per page so
    /// concurrent callers (zoom-cross trigger + first ↓ press) reuse
    /// the same Task instead of computing twice.</summary>
    private readonly Dictionary<int, Task<PageAnalysis>> _pendingAnalysis = new();

    /// <summary>Tracks in-flight page-text extraction tasks. Mirrors
    /// the analysis cache so drag-to-copy (which must run synchronously
    /// inside the user-gesture stack frame) doesn't race the render
    /// pipeline's lazy load.</summary>
    private readonly Dictionary<int, Task<PageText>> _pendingPageText = new();

    /// <summary>Lazy per-page text cache. PdfPig.ExtractPageText re-opens
    /// the document on each call (Core.PdfPig.PdfTextService doesn't yet
    /// hold a cached PdfDocument the way the renderer does in 0.7.1), so
    /// extraction is comparatively expensive; cache after first use.</summary>
    private readonly Dictionary<int, PageText> _pageTextCache = new();

    /// <summary>Search hits across all pages. Each entry is a glyph-rect
    /// list in PAGE-POINT coordinates for one match on one page; the
    /// render path converts to bitmap pixels using the current
    /// <see cref="RenderScale"/>.</summary>
    private readonly List<SearchHit> _searchHits = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    [NotifyPropertyChangedFor(nameof(CanPrev))]
    [NotifyPropertyChangedFor(nameof(CanNext))]
    [NotifyPropertyChangedFor(nameof(IsRailMode))]
    [NotifyPropertyChangedFor(nameof(RailStatus))]
    [NotifyPropertyChangedFor(nameof(ActiveBlockBoundsPagePoints))]
    [NotifyPropertyChangedFor(nameof(ActiveLineBoundsPagePoints))]
    [NotifyCanExecuteChangedFor(nameof(PrevCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailNextLineCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailPrevLineCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailNextBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailPrevBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailFirstLineOfBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailLastLineOfBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailFirstLineOfPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailLastLineOfPageCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomResetCommand))]
    private int _pageCount;

    /// <summary>
    /// Manual zoom factor. 1.0 means "fit to window" (the bitmap is
    /// rendered at <see cref="BaseRenderTargetSize"/> and the View
    /// scales it down with Stretch=Uniform/DownOnly). Above 1.0 the
    /// bitmap is rendered at <c>BaseRenderTargetSize × Zoom</c> pixels
    /// and the View switches to Stretch=None so it appears at natural
    /// size and the ScrollViewer scrolls. Capped at <see cref="MaxZoom"/>
    /// to keep WASM RAM use bounded (3× of 1600 = 4800 px longest edge =
    /// ~36 MB peak RGB buffer for a square page).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageStretch))]
    [NotifyPropertyChangedFor(nameof(ImageStretchDirection))]
    [NotifyPropertyChangedFor(nameof(ZoomPercent))]
    [NotifyPropertyChangedFor(nameof(IsRailMode))]
    [NotifyPropertyChangedFor(nameof(RailStatus))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomResetCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailNextLineCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailPrevLineCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailNextBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailPrevBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailFirstLineOfBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailLastLineOfBlockCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailFirstLineOfPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(RailLastLineOfPageCommand))]
    private double _zoom = 1.0;

    /// <summary>Active block index within the current page's analysis,
    /// or -1 when rail mode hasn't been entered yet (or the page has no
    /// analysed blocks).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveBlockBoundsPagePoints))]
    [NotifyPropertyChangedFor(nameof(ActiveLineBoundsPagePoints))]
    [NotifyPropertyChangedFor(nameof(RailStatus))]
    private int _currentBlockIndex = -1;

    /// <summary>Active line index within the current block.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveLineBoundsPagePoints))]
    [NotifyPropertyChangedFor(nameof(RailStatus))]
    private int _currentLineIndex = -1;

    /// <summary>True when manual zoom has crossed the rail-mode threshold
    /// AND the current page has been analysed and contains at least one
    /// block. The View binds the overlay visibility + arrow-key intercept
    /// to this.</summary>
    public bool IsRailMode =>
        HasDocument && Zoom > RailModeZoomThreshold &&
        _analysisCache.TryGetValue(CurrentPage, out var a) && a.Blocks.Count > 0;

    public string RailStatus
    {
        get
        {
            if (!IsRailMode) return "";
            if (!_analysisCache.TryGetValue(CurrentPage, out var a)) return "";
            if (CurrentBlockIndex < 0) return $"Rail ready · {a.Blocks.Count} blocks";
            var block = a.Blocks[CurrentBlockIndex];
            return $"Block {CurrentBlockIndex + 1}/{a.Blocks.Count} · Line {CurrentLineIndex + 1}/{block.Lines.Count}";
        }
    }

    public RectF? ActiveBlockBoundsPagePoints
    {
        get
        {
            if (CurrentBlockIndex < 0) return null;
            if (!_analysisCache.TryGetValue(CurrentPage, out var a)) return null;
            if (CurrentBlockIndex >= a.Blocks.Count) return null;
            var b = a.Blocks[CurrentBlockIndex].BBox;
            return new RectF(b.X, b.Y, b.X + b.W, b.Y + b.H);
        }
    }

    public RectF? ActiveLineBoundsPagePoints
    {
        get
        {
            if (CurrentBlockIndex < 0 || CurrentLineIndex < 0) return null;
            if (!_analysisCache.TryGetValue(CurrentPage, out var a)) return null;
            if (CurrentBlockIndex >= a.Blocks.Count) return null;
            var block = a.Blocks[CurrentBlockIndex];
            if (CurrentLineIndex >= block.Lines.Count) return null;
            var line = block.Lines[CurrentLineIndex];
            float top = line.Y - line.Height * 0.5f;
            float bottom = line.Y + line.Height * 0.5f;
            return new RectF(block.BBox.X, top, block.BBox.X + block.BBox.W, bottom);
        }
    }

    public Stretch ImageStretch =>
        Zoom <= MinZoom + 1e-3 ? Stretch.Uniform : Stretch.None;

    public StretchDirection ImageStretchDirection =>
        Zoom <= MinZoom + 1e-3 ? StretchDirection.DownOnly : StretchDirection.Both;

    public string ZoomPercent => $"{(int)Math.Round(Zoom * 100)}%";

    /// <summary>Render target longest-edge pixel count under the
    /// current <see cref="Zoom"/>. <see cref="RenderCurrentPageAsync"/>
    /// passes this to <c>IPdfService.RenderPagePixmap</c>.</summary>
    private int EffectiveRenderTargetSize =>
        (int)Math.Round(BaseRenderTargetSize * Math.Max(1.0, Zoom));

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

    public bool HasDocument => _session is not null && PageCount > 0;
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

            _session?.Dispose();

            var runtime = PdfJsRuntimeRegistry.Current
                ?? throw new InvalidOperationException(
                    "PDF.js runtime not initialised. Browser entry point should set PdfJsRuntimeRegistry.Current.");
            _session = await PdfJsSession.OpenAsync(runtime, bytes);
            PageCount = _session.PageCount;
            CurrentPage = 0;
            var rawOutline = await _session.GetOutlineAsync();
            Outline = ConvertOutline(rawOutline);
            OutlineVisible = Outline.Count > 0;
            StatusText = files[0].Name;
            _pageTextCache.Clear();
            _pendingPageText.Clear();
            _analysisCache.Clear();
            _pendingAnalysis.Clear();
            CurrentBlockIndex = -1;
            CurrentLineIndex = -1;
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

    private bool CanZoomIn() => HasDocument && Zoom < MaxZoom - 1e-3;
    private bool CanZoomOut() => HasDocument && Zoom > MinZoom + 1e-3;
    private bool CanZoomReset() => HasDocument;

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn() => Zoom = Math.Min(MaxZoom, Zoom * ZoomStep);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut() => Zoom = Math.Max(MinZoom, Zoom / ZoomStep);

    [RelayCommand(CanExecute = nameof(CanZoomReset))]
    private void ZoomReset() => Zoom = 1.0;

    /// <summary>Rail-mode is eligible to take key events when the user is
    /// magnified past the threshold AND the current page yields blocks.
    /// We allow the command to be invoked even when no active line is
    /// set yet — the first invocation enters rail mode at block 0 /
    /// line 0.</summary>
    private bool CanRailNav() =>
        HasDocument && Zoom > RailModeZoomThreshold;

    [RelayCommand(CanExecute = nameof(CanRailNav))]
    private async Task RailNextLineAsync()
    {
        var analysis = await EnsurePageAnalysisAsync(CurrentPage);
        if (analysis.Blocks.Count == 0) return;

        if (CurrentBlockIndex < 0)
        {
            // Default entry — View should have already called
            // EnterRailModeNearPageY before this fires; if it didn't
            // (analysis still pending at View's call), fall back to
            // top-of-page in reading order.
            CurrentBlockIndex = 0;
            CurrentLineIndex = 0;
            return;
        }

        var block = analysis.Blocks[CurrentBlockIndex];
        if (CurrentLineIndex < block.Lines.Count - 1)
        {
            CurrentLineIndex++;
        }
        else if (CurrentBlockIndex < analysis.Blocks.Count - 1)
        {
            CurrentBlockIndex++;
            CurrentLineIndex = 0;
        }
        else if (CanNext)
        {
            // Advance to next page, top-of-page-in-reading-order. The
            // analysis for the new page may not be ready yet —
            // EnsurePageAnalysisAsync handles the wait.
            await NextAsync();
            var newAnalysis = await EnsurePageAnalysisAsync(CurrentPage);
            if (newAnalysis.Blocks.Count > 0)
            {
                CurrentBlockIndex = 0;
                CurrentLineIndex = 0;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRailNav))]
    private async Task RailNextBlockAsync()
    {
        var analysis = await EnsurePageAnalysisAsync(CurrentPage);
        if (analysis.Blocks.Count == 0) return;
        if (CurrentBlockIndex < 0) { CurrentBlockIndex = 0; CurrentLineIndex = 0; return; }

        if (CurrentBlockIndex < analysis.Blocks.Count - 1)
        {
            CurrentBlockIndex++;
            CurrentLineIndex = 0;
        }
        else if (CanNext)
        {
            await NextAsync();
            var newAnalysis = await EnsurePageAnalysisAsync(CurrentPage);
            if (newAnalysis.Blocks.Count > 0)
            {
                CurrentBlockIndex = 0;
                CurrentLineIndex = 0;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRailNav))]
    private async Task RailPrevBlockAsync()
    {
        var analysis = await EnsurePageAnalysisAsync(CurrentPage);
        if (analysis.Blocks.Count == 0) return;
        if (CurrentBlockIndex < 0) { CurrentBlockIndex = 0; CurrentLineIndex = 0; return; }

        if (CurrentBlockIndex > 0)
        {
            CurrentBlockIndex--;
            CurrentLineIndex = 0;
        }
        else if (CanPrev)
        {
            await PrevAsync();
            var newAnalysis = await EnsurePageAnalysisAsync(CurrentPage);
            if (newAnalysis.Blocks.Count > 0)
            {
                CurrentBlockIndex = newAnalysis.Blocks.Count - 1;
                CurrentLineIndex = 0;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRailNav))]
    private async Task RailFirstLineOfBlockAsync()
    {
        var analysis = await EnsurePageAnalysisAsync(CurrentPage);
        if (analysis.Blocks.Count == 0) return;
        if (CurrentBlockIndex < 0) { CurrentBlockIndex = 0; CurrentLineIndex = 0; return; }
        CurrentLineIndex = 0;
    }

    [RelayCommand(CanExecute = nameof(CanRailNav))]
    private async Task RailLastLineOfBlockAsync()
    {
        var analysis = await EnsurePageAnalysisAsync(CurrentPage);
        if (analysis.Blocks.Count == 0) return;
        if (CurrentBlockIndex < 0)
        {
            CurrentBlockIndex = 0;
            CurrentLineIndex = Math.Max(0, analysis.Blocks[0].Lines.Count - 1);
            return;
        }
        var block = analysis.Blocks[CurrentBlockIndex];
        CurrentLineIndex = Math.Max(0, block.Lines.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(CanRailNav))]
    private async Task RailFirstLineOfPageAsync()
    {
        var analysis = await EnsurePageAnalysisAsync(CurrentPage);
        if (analysis.Blocks.Count == 0) return;
        CurrentBlockIndex = 0;
        CurrentLineIndex = 0;
    }

    [RelayCommand(CanExecute = nameof(CanRailNav))]
    private async Task RailLastLineOfPageAsync()
    {
        var analysis = await EnsurePageAnalysisAsync(CurrentPage);
        if (analysis.Blocks.Count == 0) return;
        CurrentBlockIndex = analysis.Blocks.Count - 1;
        var last = analysis.Blocks[CurrentBlockIndex];
        CurrentLineIndex = Math.Max(0, last.Lines.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(CanRailNav))]
    private async Task RailPrevLineAsync()
    {
        var analysis = await EnsurePageAnalysisAsync(CurrentPage);
        if (analysis.Blocks.Count == 0) return;

        if (CurrentBlockIndex < 0)
        {
            CurrentBlockIndex = 0;
            CurrentLineIndex = 0;
            return;
        }

        if (CurrentLineIndex > 0)
        {
            CurrentLineIndex--;
        }
        else if (CurrentBlockIndex > 0)
        {
            CurrentBlockIndex--;
            var prev = analysis.Blocks[CurrentBlockIndex];
            CurrentLineIndex = Math.Max(0, prev.Lines.Count - 1);
        }
        else if (CanPrev)
        {
            await PrevAsync();
            var newAnalysis = await EnsurePageAnalysisAsync(CurrentPage);
            if (newAnalysis.Blocks.Count > 0)
            {
                CurrentBlockIndex = newAnalysis.Blocks.Count - 1;
                var lastBlock = newAnalysis.Blocks[CurrentBlockIndex];
                CurrentLineIndex = Math.Max(0, lastBlock.Lines.Count - 1);
            }
        }
    }

    /// <summary>Source-generated hook on <see cref="Zoom"/> changes —
    /// clamp to range, then re-render the current page so the bitmap
    /// matches the new <see cref="EffectiveRenderTargetSize"/>.</summary>
    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, MinZoom, MaxZoom);
        if (Math.Abs(clamped - value) > 1e-6)
        {
            Zoom = clamped;
            return;  // setter re-entry will trigger render
        }
        if (HasDocument) _ = RenderCurrentPageAsync();
        // Eagerly analyse on a background thread when crossing into
        // rail-eligible zoom — by the time the user presses ↓ the
        // result is usually already cached. PdfPig's word extraction +
        // Docstrum is heavy enough to feel sluggish if it runs on the
        // UI thread on first key press.
        if (HasDocument && Zoom > RailModeZoomThreshold)
            _ = EnsurePageAnalysisAsync(CurrentPage);
    }

    /// <summary>Source-generated hook on <see cref="CurrentPage"/>
    /// changes — clear the rail-mode cursor so the new page starts
    /// fresh. PrevAsync / NextAsync mutate CurrentPage directly, so we
    /// centralise the reset here.</summary>
    partial void OnCurrentPageChanged(int value)
    {
        CurrentBlockIndex = -1;
        CurrentLineIndex = -1;
        // Pre-warm analysis on the new page if we're already in
        // rail-eligible zoom. Background — IsRailMode will flip true
        // once the result lands.
    if (HasDocument && Zoom > RailModeZoomThreshold)
            _ = EnsurePageAnalysisAsync(value);
    }

    private bool CanSearch() => HasDocument && !string.IsNullOrWhiteSpace(SearchQuery);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (_session is null) return;
        ClearSearchHits();
        var query = SearchQuery;

        // Scan every page for the query. Builds glyph-rect lists in
        // PAGE-POINT coordinates so the render path can paint them
        // without knowing the search semantics.
        for (int p = 0; p < PageCount; p++)
        {
            var text = await EnsurePageTextAsync(p);
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
        // GetSelectedText runs from the View synchronously inside the
        // user-gesture frame so the clipboard write is authorised by
        // the browser. We can't await text extraction here. The render
        // pipeline pre-fetches PageText after every render, so this
        // hit is essentially always cached by the time the user
        // finishes a drag.
        var text = TryGetCachedPageText(CurrentPage);
        if (text is null || text.Text.Length == 0) return null;

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
        // Reset rail position so the new page starts fresh — the user's
        // next ↓/↑ re-enters at top/bottom of the new page in reading
        // order (see RailNextLineAsync / RailPrevLineAsync).
        CurrentBlockIndex = -1;
        CurrentLineIndex = -1;
        await RenderCurrentPageAsync();
    }

    /// <summary>Synchronous cache lookup — returns the cached
    /// <see cref="PageText"/> if it's already been extracted, else
    /// null. Used by <see cref="GetSelectedText"/> which must run
    /// synchronously inside the user-gesture stack frame so the
    /// browser still authorises the clipboard write.</summary>
    private PageText? TryGetCachedPageText(int pageIndex) =>
        _pageTextCache.TryGetValue(pageIndex, out var t) ? t : null;

    /// <summary>Async extract-and-cache. The render pipeline kicks
    /// this off after every page render so by the time the user
    /// drag-selects, the text is in cache and the sync lookup hits.
    /// Concurrent callers for the same page share one in-flight
    /// task via <see cref="_pendingPageText"/>.</summary>
    private async Task<PageText> EnsurePageTextAsync(int pageIndex)
    {
        if (_pageTextCache.TryGetValue(pageIndex, out var cached)) return cached;
        if (_pendingPageText.TryGetValue(pageIndex, out var pending)) return await pending;
        if (_session is null) return new PageText("", []);
        var task = _session.GetPageTextAsync(pageIndex);
        _pendingPageText[pageIndex] = task;
        try
        {
            var result = await task;
            _pageTextCache[pageIndex] = result;
            return result;
        }
        finally
        {
            _pendingPageText.Remove(pageIndex);
        }
    }

    private static List<OutlineEntry> ConvertOutline(IReadOnlyList<PdfOutlineEntry> src)
    {
        var dst = new List<OutlineEntry>(src.Count);
        foreach (var s in src)
        {
            dst.Add(new OutlineEntry
            {
                Title = s.Title,
                Page = s.PageIndex >= 0 ? s.PageIndex : null,
                Children = ConvertOutline(s.Children),
            });
        }
        return dst;
    }

    /// <summary>
    /// Ensures the requested page's layout analysis is computed and
    /// cached, returning the result. The actual segmentation runs on
    /// a background <see cref="Task"/> so the UI thread isn't blocked
    /// — important because PdfPig's <see cref="DocstrumBoundingBoxes"/>
    /// takes meaningful time on dense academic pages and was the main
    /// source of the v0.6.0 sluggishness. Concurrent callers for the
    /// same page share one in-flight task via
    /// <see cref="_pendingAnalysis"/>. The result is inserted into
    /// <see cref="_analysisCache"/> on the UI thread (the implicit
    /// async continuation captures the Avalonia
    /// <see cref="System.Threading.SynchronizationContext"/>), and
    /// property-changed events fire so the View can light up
    /// rail mode.
    /// </summary>
    private async Task<PageAnalysis> EnsurePageAnalysisAsync(int pageIndex)
    {
        if (_analysisCache.TryGetValue(pageIndex, out var cached)) return cached;
        if (_pendingAnalysis.TryGetValue(pageIndex, out var pending)) return await pending;
        if (_session is null) return new PageAnalysis { Blocks = [], PageWidth = 0, PageHeight = 0 };

        var session = _session;
        var resolver = _resolver;
        // PDF.js's getTextContent already runs in a Web Worker, so we
        // don't need Task.Run to keep the UI thread free — awaiting
        // directly is enough.
        async Task<PageAnalysis> Compute()
        {
            var (w, h) = await session.GetPageSizeAsync(pageIndex);
            var blocks = await session.GetBlocksAsync(pageIndex);
            resolver.AssignOrder(blocks, w, h);
            blocks.Sort((a, b) => a.Order.CompareTo(b.Order));
            return new PageAnalysis { Blocks = blocks, PageWidth = w, PageHeight = h };
        }
        var task = Compute();
        _pendingAnalysis[pageIndex] = task;

        PageAnalysis result;
        try
        {
            result = await task;
        }
        catch (Exception ex)
        {
            _pendingAnalysis.Remove(pageIndex);
            StatusText = $"Analysis failed for page {pageIndex + 1}: {ex.Message}";
            return new PageAnalysis { Blocks = [], PageWidth = 0, PageHeight = 0 };
        }

        _analysisCache[pageIndex] = result;
        _pendingAnalysis.Remove(pageIndex);

        if (pageIndex == CurrentPage)
        {
            OnPropertyChanged(nameof(IsRailMode));
            OnPropertyChanged(nameof(RailStatus));
            OnPropertyChanged(nameof(ActiveBlockBoundsPagePoints));
            OnPropertyChanged(nameof(ActiveLineBoundsPagePoints));
            RailNextLineCommand.NotifyCanExecuteChanged();
            RailPrevLineCommand.NotifyCanExecuteChanged();
            RailNextBlockCommand.NotifyCanExecuteChanged();
            RailPrevBlockCommand.NotifyCanExecuteChanged();
            RailFirstLineOfBlockCommand.NotifyCanExecuteChanged();
            RailLastLineOfBlockCommand.NotifyCanExecuteChanged();
            RailFirstLineOfPageCommand.NotifyCanExecuteChanged();
            RailLastLineOfPageCommand.NotifyCanExecuteChanged();
        }
        return result;
    }

    /// <summary>Cache-only lookup — used by the rail-nav commands when
    /// the active block / line bounds need to be read but the
    /// background analysis may not have completed yet.</summary>
    private PageAnalysis? TryGetCachedAnalysis(int pageIndex) =>
        _analysisCache.TryGetValue(pageIndex, out var a) ? a : null;

    /// <summary>
    /// Called by the View on the user's first rail-mode keystroke so
    /// rail mode enters near where the user is looking, not at the top
    /// of the page. <paramref name="pageY"/> is the page-point Y of the
    /// top of the current viewport; we pick the block whose vertical
    /// extent contains (or is closest to) that Y, then the line within
    /// the block whose centre is nearest. If the analysis isn't ready
    /// yet this is a no-op — the caller falls back to the default
    /// "start at block[0] line[0]" behaviour by setting the indices
    /// directly.
    /// </summary>
    public void EnterRailModeNearPageY(float pageY)
    {
        if (!_analysisCache.TryGetValue(CurrentPage, out var analysis)) return;
        if (analysis.Blocks.Count == 0) return;

        int bestBlock = 0;
        float bestBlockDist = float.MaxValue;
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            var b = analysis.Blocks[i].BBox;
            if (pageY >= b.Y && pageY <= b.Y + b.H)
            {
                bestBlock = i;
                bestBlockDist = 0f;
                break;
            }
            float centerY = b.Y + b.H * 0.5f;
            float dist = Math.Abs(pageY - centerY);
            if (dist < bestBlockDist) { bestBlockDist = dist; bestBlock = i; }
        }

        var block = analysis.Blocks[bestBlock];
        int bestLine = 0;
        if (block.Lines.Count > 0)
        {
            float bestLineDist = float.MaxValue;
            for (int j = 0; j < block.Lines.Count; j++)
            {
                float dist = Math.Abs(block.Lines[j].Y - pageY);
                if (dist < bestLineDist) { bestLineDist = dist; bestLine = j; }
            }
        }

        CurrentBlockIndex = bestBlock;
        CurrentLineIndex = bestLine;
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

    private bool _renderInFlight;
    private bool _renderQueued;

    /// <summary>
    /// Re-renders the current page at the effective render target size.
    /// Concurrent calls coalesce: while one render is running, any
    /// further request just sets <see cref="_renderQueued"/> and the
    /// running render re-runs once it finishes. Without this guard,
    /// holding Ctrl+= would queue full re-renders sequentially and the
    /// viewport would lag behind the user's zoom level by N renders.
    /// </summary>
    private async Task RenderCurrentPageAsync()
    {
        if (_session is null) return;
        if (_renderInFlight) { _renderQueued = true; return; }
        _renderInFlight = true;
        try
        {
            do
            {
                _renderQueued = false;
                await RenderOnceAsync();
            }
            while (_renderQueued);
        }
        finally
        {
            _renderInFlight = false;
        }
    }

    private async Task RenderOnceAsync()
    {
        if (_session is null) return;
        var session = _session;
        var page = CurrentPage;
        var targetSize = EffectiveRenderTargetSize;
        var searchHitsForPage = _searchHits.Where(h => h.PageIndex == page).ToList();
        var currentMatch = (CurrentMatchIndex >= 0 && CurrentMatchIndex < _searchHits.Count)
            ? _searchHits[CurrentMatchIndex] : null;

        try
        {
            var (pageW, pageH) = await session.GetPageSizeAsync(page);
            float scale = (float)(targetSize / Math.Max(pageW, pageH));
            RenderScale = scale;
            CurrentPagePointWidth = pageW;
            CurrentPagePointHeight = pageH;

            // PDF.js renders inside a Web Worker so the await yields
            // until the worker posts back — UI thread stays free.
            var rendered = await session.RenderPageAsync(page, targetSize);

            // Paint search highlights into the RGBA buffer in place, then
            // swizzle to BGRA (Avalonia's required pixel format).
            foreach (var hit in searchHitsForPage)
            {
                bool isCurrent = ReferenceEquals(hit, currentMatch);
                var (r, g, b) = isCurrent
                    ? (HighlightCurrentMatchR, HighlightCurrentMatchG, HighlightCurrentMatchB)
                    : (HighlightOtherMatchR,  HighlightOtherMatchG,  HighlightOtherMatchB);
                foreach (var pageRect in hit.PagePointRects)
                    PaintRectBlendRgba(rendered.Rgba, rendered.Width, rendered.Height, pageRect, scale, r, g, b, 0.5f);
            }

            PageImage = RgbaToAvaloniaBitmapInPlace(rendered.Rgba, rendered.Width, rendered.Height);

            // Pre-fetch page text so drag-select (which must be sync)
            // hits the cache. Fire-and-forget — render isn't blocked
            // on this.
            _ = EnsurePageTextAsync(page);
        }
        catch (Exception ex)
        {
            StatusText = $"Render failed: {ex.Message}";
        }
    }

    /// <summary>
    /// In-place alpha blend over an RGBA buffer (4-byte stride, A
    /// channel left untouched). The buffer came from PDF.js's
    /// <c>getImageData()</c> which is RGBA; we mutate it here and
    /// the final swizzle to BGRA happens in
    /// <see cref="RgbaToAvaloniaBitmapInPlace"/>.
    /// </summary>
    private static void PaintRectBlendRgba(byte[] rgba, int w, int h, RectF pageRect, float pageToBitmap,
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
            int rowStart = y * w * 4;
            for (int x = x1; x < x2; x++)
            {
                int idx = rowStart + x * 4;
                rgba[idx]     = (byte)(rgba[idx]     * inv + tintR * alpha);
                rgba[idx + 1] = (byte)(rgba[idx + 1] * inv + tintG * alpha);
                rgba[idx + 2] = (byte)(rgba[idx + 2] * inv + tintB * alpha);
                // alpha untouched
            }
        }
    }

    /// <summary>
    /// Swizzles RGBA → BGRA in place and copies into an
    /// Avalonia <see cref="WriteableBitmap"/>. Saves an extra buffer
    /// allocation versus the previous RGB→BGRA expand path. The input
    /// array is mutated; callers shouldn't reuse it after.
    /// </summary>
    private static WriteableBitmap RgbaToAvaloniaBitmapInPlace(byte[] rgba, int width, int height)
    {
        var bmp = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        int pixelCount = width * height;
        for (int i = 0; i < pixelCount; i++)
        {
            int idx = i * 4;
            // RGBA → BGRA: swap R (idx) and B (idx+2). G, A unchanged.
            (rgba[idx], rgba[idx + 2]) = (rgba[idx + 2], rgba[idx]);
        }

        using var frame = bmp.Lock();
        Marshal.Copy(rgba, 0, frame.Address, rgba.Length);
        return bmp;
    }

    private sealed record SearchHit(int PageIndex, int CharStart, int CharLength, List<RectF> PagePointRects);
}

internal static class Dispatcher
{
    public static Avalonia.Threading.Dispatcher UIThread => Avalonia.Threading.Dispatcher.UIThread;
}
