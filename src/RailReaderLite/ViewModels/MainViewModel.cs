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

    // Klampfl reading-order via Allen's interval algebra (RailDLA port)
    // is invoked directly in EnsurePageAnalysisAsync so the per-line
    // rect cache can be reordered in lockstep with the blocks.

    /// <summary>Per-page analysis result cache. Blocks are stored in
    /// reading order with their <c>Lines</c> populated.</summary>
    private readonly Dictionary<int, PageAnalysis> _analysisCache = new();

    /// <summary>Per-page per-block per-line bounding rects in
    /// page-point space — parallel to <c>analysis.Blocks[i].Lines</c>.
    /// Used by the rail overlay to draw a tight rect that hugs the
    /// actual line, not the block-wide LineInfo Y/Height. Filled at
    /// the same time as <see cref="_analysisCache"/>.</summary>
    private readonly Dictionary<int, List<List<RectF>>> _lineRectsCache = new();

    /// <summary>Tracks in-flight background analysis tasks per page so
    /// concurrent callers (zoom-cross trigger + first ↓ press) reuse
    /// the same Task instead of computing twice.</summary>
    private readonly Dictionary<int, Task<PageAnalysis>> _pendingAnalysis = new();

    /// <summary>Tracks in-flight page-text extraction tasks. Mirrors
    /// the analysis cache so drag-to-copy (which must run synchronously
    /// inside the user-gesture stack frame) doesn't race the render
    /// pipeline's lazy load.</summary>
    private readonly Dictionary<int, Task<PageText>> _pendingPageText = new();

    /// <summary>Decoration-block indices per page, as computed by
    /// <see cref="KlampflDecoration.DetectDecoration"/>. Rail-mode
    /// navigation skips blocks whose index is in this set, so the
    /// user doesn't step through running headers / footers /
    /// page numbers as if they were body paragraphs.</summary>
    private readonly Dictionary<int, HashSet<int>> _decoration = new();

    /// <summary>Per-block text cache used by the decoration
    /// classifier. Filled as a side effect of
    /// <see cref="EnsureBlockTextsAsync"/>.</summary>
    private readonly Dictionary<int, List<BlockWithText>> _blockTextsCache = new();

    private bool _decorationRunPending;

    /// <summary>Monotonic generation counter. <see cref="OpenAsync"/>
    /// bumps it; every async helper captures the value at start and
    /// discards results if it changed (i.e. a new document loaded
    /// while the helper was awaiting JS interop). This is the fix for
    /// the v0.7.0 hang where opening a second PDF froze the app —
    /// pending analyses from the old doc were writing into the cache
    /// of the new doc and getting wedged.</summary>
    private int _generation;

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

            // Prefer the parallel per-line rects cache — those carry
            // the actual line extent (left, top, right, bottom), so
            // the overlay hugs the visible text. Fall back to the
            // block-wide rect if the cache hasn't been populated for
            // some reason.
            if (_lineRectsCache.TryGetValue(CurrentPage, out var blockRects)
                && CurrentBlockIndex < blockRects.Count
                && CurrentLineIndex < blockRects[CurrentBlockIndex].Count)
            {
                return blockRects[CurrentBlockIndex][CurrentLineIndex];
            }

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

            // Bump generation BEFORE any await so any in-flight tasks
            // from the previous doc see the change as soon as they
            // resume — they'll bail without writing into the caches.
            _generation++;
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
            _lineRectsCache.Clear();
            _pendingAnalysis.Clear();
            _decoration.Clear();
            _blockTextsCache.Clear();
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
            CurrentBlockIndex = FindFirstNonDecoration(CurrentPage, analysis, forward: true);
            CurrentLineIndex = 0;
            return;
        }

        var block = analysis.Blocks[CurrentBlockIndex];
        if (CurrentLineIndex < block.Lines.Count - 1)
        {
            CurrentLineIndex++;
            return;
        }

        // Advance to next non-decoration block on this page (in
        // reading order).
        int nextBlock = NextNonDecorationBlock(CurrentPage, analysis, CurrentBlockIndex);
        if (nextBlock >= 0)
        {
            CurrentBlockIndex = nextBlock;
            CurrentLineIndex = 0;
        }
        else if (CanNext)
        {
            await NextAsync();
            var newAnalysis = await EnsurePageAnalysisAsync(CurrentPage);
            if (newAnalysis.Blocks.Count > 0)
            {
                CurrentBlockIndex = FindFirstNonDecoration(CurrentPage, newAnalysis, forward: true);
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
            CurrentBlockIndex = FindFirstNonDecoration(CurrentPage, analysis, forward: true);
            CurrentLineIndex = 0;
            return;
        }

        if (CurrentLineIndex > 0)
        {
            CurrentLineIndex--;
            return;
        }

        int prevBlock = PrevNonDecorationBlock(CurrentPage, analysis, CurrentBlockIndex);
        if (prevBlock >= 0)
        {
            CurrentBlockIndex = prevBlock;
            var prev = analysis.Blocks[prevBlock];
            CurrentLineIndex = Math.Max(0, prev.Lines.Count - 1);
        }
        else if (CanPrev)
        {
            await PrevAsync();
            var newAnalysis = await EnsurePageAnalysisAsync(CurrentPage);
            if (newAnalysis.Blocks.Count > 0)
            {
                CurrentBlockIndex = FindFirstNonDecoration(CurrentPage, newAnalysis, forward: false);
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
        int gen = _generation;
        var task = _session.GetPageTextAsync(pageIndex);
        _pendingPageText[pageIndex] = task;
        try
        {
            var result = await task;
            // Bail if a new document loaded while we were awaiting JS.
            if (gen != _generation) return new PageText("", []);
            _pageTextCache[pageIndex] = result;
            return result;
        }
        finally
        {
            if (gen == _generation) _pendingPageText.Remove(pageIndex);
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
        int gen = _generation;
        // Compute returns the reordered blocks AND the parallel line-
        // rects list. We can't stuff the latter on PageAnalysis (Core
        // type), so the caller commits it to _lineRectsCache after
        // the generation check passes.
        async Task<(PageAnalysis Analysis, List<List<RectF>> LineRects)> Compute()
        {
            var (w, h) = await session.GetPageSizeAsync(pageIndex);
            var segment = await session.GetBlocksAsync(pageIndex);
            int n = segment.Blocks.Count;
            if (n == 0)
                return (new PageAnalysis { Blocks = [], PageWidth = w, PageHeight = h }, new List<List<RectF>>());

            float tol = (float)(w * 0.005f);
            var order = KlampflReadingOrder.DetectOrder(segment.Blocks, tol, ReadingMode.ColumnWise);
            var orderedBlocks = new List<LayoutBlock>(n);
            var orderedRects  = new List<List<RectF>>(n);
            for (int rank = 0; rank < order.Length; rank++)
            {
                var b = segment.Blocks[order[rank]];
                b.Order = rank;
                orderedBlocks.Add(b);
                orderedRects.Add(segment.LineRects[order[rank]]);
            }
            return (new PageAnalysis { Blocks = orderedBlocks, PageWidth = w, PageHeight = h }, orderedRects);
        }
        var computeTask = Compute();
        // _pendingAnalysis holds a Task<PageAnalysis> for callers that
        // want to share the in-flight work. We adapt by unwrapping.
        var task = AsAnalysisTask(computeTask);
        _pendingAnalysis[pageIndex] = task;

        PageAnalysis result;
        List<List<RectF>> lineRects;
        try
        {
            var tuple = await computeTask;
            result = tuple.Analysis;
            lineRects = tuple.LineRects;
        }
        catch (Exception ex)
        {
            if (gen == _generation) _pendingAnalysis.Remove(pageIndex);
            if (gen == _generation)
                StatusText = $"Analysis failed for page {pageIndex + 1}: {ex.Message}";
            return new PageAnalysis { Blocks = [], PageWidth = 0, PageHeight = 0 };
        }

        // Bail if a new doc loaded mid-analysis — don't poison the new
        // doc's cache with results from the old one.
        if (gen != _generation)
            return new PageAnalysis { Blocks = [], PageWidth = 0, PageHeight = 0 };

        _analysisCache[pageIndex] = result;
        _lineRectsCache[pageIndex] = lineRects;
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

            // Fire-and-forget decoration detection — runs once per
            // document, requires ≥2 analyzed pages. Kicks off in the
            // background so the user's first ↓ isn't blocked.
            _ = EnsureDecorationDetectedAsync();
        }
        return result;
    }

    /// <summary>Cache-only lookup — used by the rail-nav commands when
    /// the active block / line bounds need to be read but the
    /// background analysis may not have completed yet.</summary>
    private PageAnalysis? TryGetCachedAnalysis(int pageIndex) =>
        _analysisCache.TryGetValue(pageIndex, out var a) ? a : null;

    private static async Task<PageAnalysis> AsAnalysisTask(
        Task<(PageAnalysis Analysis, List<List<RectF>> LineRects)> tuple)
    {
        var t = await tuple;
        return t.Analysis;
    }

    /// <summary>Builds the per-block text payload for the decoration
    /// classifier. For each block in the analysis, concatenates all
    /// text items whose centre falls inside the block bbox.</summary>
    private async Task<List<BlockWithText>> EnsureBlockTextsAsync(int pageIndex)
    {
        if (_blockTextsCache.TryGetValue(pageIndex, out var cached)) return cached;
        if (_session is null) return new List<BlockWithText>();
        int gen = _generation;
        var analysis = await EnsurePageAnalysisAsync(pageIndex);
        if (gen != _generation) return new List<BlockWithText>();
        if (analysis.Blocks.Count == 0)
        {
            var empty = new List<BlockWithText>();
            _blockTextsCache[pageIndex] = empty;
            return empty;
        }
        var items = await _session.GetTextItemsAsync(pageIndex);
        if (gen != _generation) return new List<BlockWithText>();
        var result = new List<BlockWithText>(analysis.Blocks.Count);
        foreach (var b in analysis.Blocks)
        {
            float left = b.BBox.X, top = b.BBox.Y;
            float right = left + b.BBox.W, bottom = top + b.BBox.H;
            var sb = new System.Text.StringBuilder();
            foreach (var it in items)
            {
                float cx = (float)(it.X + it.Width * 0.5);
                float cy = (float)(it.Y + it.Height * 0.5);
                if (cx < left || cx > right || cy < top || cy > bottom) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(it.Text);
            }
            result.Add(new BlockWithText(b.BBox.X, b.BBox.Y, b.BBox.W, b.BBox.H, sb.ToString()));
        }
        _blockTextsCache[pageIndex] = result;
        return result;
    }

    /// <summary>Runs Klampfl decoration detection across all pages,
    /// once we have enough analyzed pages to do so. Idempotent — the
    /// guard at the top short-circuits if the cache is already
    /// populated. Triggered from
    /// <see cref="EnsurePageAnalysisAsync"/>'s completion path.</summary>
    private async Task EnsureDecorationDetectedAsync()
    {
        if (_decorationRunPending) return;
        if (_decoration.Count >= PageCount) return;  // already done
        if (PageCount < 2) return;                   // need ≥2 pages
        if (_session is null) return;

        _decorationRunPending = true;
        try
        {
            // Ensure block-texts for every page. Cheap when analysis
            // is already cached.
            var pages = new List<IReadOnlyList<BlockWithText>>(PageCount);
            for (int p = 0; p < PageCount; p++)
                pages.Add(await EnsureBlockTextsAsync(p));

            var result = KlampflDecoration.DetectDecoration(pages);
            if (result is null) return;
            for (int p = 0; p < result.Count; p++) _decoration[p] = result[p];

            // Currently-active block might be a decoration block now;
            // bump to the next non-decoration one.
            if (IsRailMode && CurrentBlockIndex >= 0 && IsDecoration(CurrentPage, CurrentBlockIndex))
                _ = RailNextLineAsync();
        }
        finally
        {
            _decorationRunPending = false;
        }
    }

    private bool IsDecoration(int pageIndex, int blockIndex) =>
        _decoration.TryGetValue(pageIndex, out var s) && s.Contains(blockIndex);

    /// <summary>First non-decoration block on a page, scanning either
    /// from index 0 forward or from the last index backward. Falls
    /// back to index 0 / last if every block is flagged as decoration
    /// (degenerate edge case — shouldn't happen on real pages).</summary>
    private int FindFirstNonDecoration(int page, PageAnalysis analysis, bool forward)
    {
        int n = analysis.Blocks.Count;
        if (n == 0) return -1;
        if (forward)
        {
            for (int i = 0; i < n; i++) if (!IsDecoration(page, i)) return i;
            return 0;
        }
        for (int i = n - 1; i >= 0; i--) if (!IsDecoration(page, i)) return i;
        return n - 1;
    }

    private int NextNonDecorationBlock(int page, PageAnalysis analysis, int from)
    {
        for (int i = from + 1; i < analysis.Blocks.Count; i++)
            if (!IsDecoration(page, i)) return i;
        return -1;
    }

    private int PrevNonDecorationBlock(int page, PageAnalysis analysis, int from)
    {
        for (int i = from - 1; i >= 0; i--)
            if (!IsDecoration(page, i)) return i;
        return -1;
    }

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
    /// <summary>
    /// Click-to-snap: pick the block whose bbox contains the point,
    /// or (fallback) the block nearest by 2D distance to centre.
    /// Within that block, pick the line whose Y-centre is nearest.
    /// Skips decoration blocks so a click in the page-number area
    /// jumps to the body line below it instead of landing on the
    /// number. Replaces <see cref="EnterRailModeNearPageY"/>'s
    /// Y-only heuristic for the click case, which couldn't pick
    /// between columns at the same Y.
    /// </summary>
    public void EnterRailModeNearPagePoint(float pageX, float pageY)
    {
        if (!_analysisCache.TryGetValue(CurrentPage, out var analysis)) return;
        if (analysis.Blocks.Count == 0) return;

        int bestBlock = -1;
        float bestBlockDist = float.MaxValue;
        for (int i = 0; i < analysis.Blocks.Count; i++)
        {
            if (IsDecoration(CurrentPage, i)) continue;
            var b = analysis.Blocks[i].BBox;
            if (pageX >= b.X && pageX <= b.X + b.W && pageY >= b.Y && pageY <= b.Y + b.H)
            {
                bestBlock = i;
                break;
            }
            float cx = b.X + b.W * 0.5f;
            float cy = b.Y + b.H * 0.5f;
            float dx = pageX - cx;
            float dy = pageY - cy;
            float dist = dx * dx + dy * dy;
            if (dist < bestBlockDist) { bestBlockDist = dist; bestBlock = i; }
        }
        if (bestBlock < 0) return;

        int bestLine = 0;
        // Prefer per-line rect Y midpoint — that's the actual visible
        // baseline rather than the LineInfo Y-Height range, which
        // matters when sub/superscripts stretch the Height value.
        if (_lineRectsCache.TryGetValue(CurrentPage, out var rects)
            && bestBlock < rects.Count && rects[bestBlock].Count > 0)
        {
            var blockRects = rects[bestBlock];
            float bestLineDist = float.MaxValue;
            for (int j = 0; j < blockRects.Count; j++)
            {
                float cy = (blockRects[j].Top + blockRects[j].Bottom) * 0.5f;
                float d = Math.Abs(pageY - cy);
                if (d < bestLineDist) { bestLineDist = d; bestLine = j; }
            }
        }
        else
        {
            var block = analysis.Blocks[bestBlock];
            if (block.Lines.Count > 0)
            {
                float bestLineDist = float.MaxValue;
                for (int j = 0; j < block.Lines.Count; j++)
                {
                    float d = Math.Abs(pageY - block.Lines[j].Y);
                    if (d < bestLineDist) { bestLineDist = d; bestLine = j; }
                }
            }
        }

        CurrentBlockIndex = bestBlock;
        CurrentLineIndex = bestLine;
    }

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
