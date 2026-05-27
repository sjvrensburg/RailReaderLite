using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RailReader.Core.Models;

namespace RailReaderLite.Services;

/// <summary>
/// Per-document session backed by PDF.js (via <see cref="IPdfJsRuntime"/>).
/// Mirrors the surface area that <c>LitePdfPigSession</c> used to provide
/// but everything is async (PDF.js itself is async-only) and all the
/// rendering / parsing happens off the .NET WASM main thread because
/// PDF.js runs in a Web Worker.
///
/// <para>
/// Construction is async — use <see cref="OpenAsync"/>. The session
/// owns a <c>docId</c> issued by the JS shim and disposes it on
/// <see cref="Dispose"/>.
/// </para>
///
/// <para>
/// Coordinates throughout this class are page-point space, origin
/// top-left, Y-down — the JS shim already flips PDF.js's native Y-up.
/// </para>
/// </summary>
public sealed class PdfJsSession : IDisposable
{
    private readonly IPdfJsRuntime _runtime;
    private readonly int _docId;
    private bool _disposed;

    public int PageCount { get; }

    private PdfJsSession(IPdfJsRuntime runtime, int docId, int pageCount)
    {
        _runtime = runtime;
        _docId = docId;
        PageCount = pageCount;
    }

    public static async Task<PdfJsSession> OpenAsync(IPdfJsRuntime runtime, byte[] pdfBytes)
    {
        var (docId, pageCount) = await runtime.OpenDocAsync(pdfBytes);
        return new PdfJsSession(runtime, docId, pageCount);
    }

    public Task<(double Width, double Height)> GetPageSizeAsync(int pageIndex) =>
        _runtime.GetPageSizeAsync(_docId, pageIndex);

    public Task<RenderedPage> RenderPageAsync(int pageIndex, int targetLongestEdge) =>
        _runtime.RenderPageAsync(_docId, pageIndex, targetLongestEdge);

    public Task<IReadOnlyList<PdfOutlineEntry>> GetOutlineAsync() =>
        _runtime.GetOutlineAsync(_docId);

    /// <summary>
    /// Exposes the underlying PDF.js text items so the VM can build
    /// per-block text (needed by the decoration classifier — see
    /// <see cref="KlampflDecoration"/>). The JS shim caches these per
    /// page so repeated calls are O(1).
    /// </summary>
    public Task<IReadOnlyList<PdfTextItem>> GetTextItemsAsync(int pageIndex) =>
        _runtime.GetTextItemsAsync(_docId, pageIndex);

    /// <summary>
    /// Builds the per-page <see cref="PageText"/> (concatenated string
    /// + per-character <see cref="CharBox"/>es in reading order)
    /// from PDF.js's word-level text items. Each item is a continuous
    /// run from PDF.js — typically a word, sometimes a short phrase.
    /// We approximate per-character positions by splitting the item's
    /// horizontal extent evenly across its characters; that's exactly
    /// what drag-to-copy and search hit-clustering need, since both
    /// quantise glyphs by mid-X/mid-Y.
    /// </summary>
    public async Task<PageText> GetPageTextAsync(int pageIndex)
    {
        var items = await _runtime.GetTextItemsAsync(_docId, pageIndex);
        if (items.Count == 0) return new PageText("", []);

        var sb = new StringBuilder(items.Sum(i => i.Text.Length));
        var boxes = new List<CharBox>(sb.Capacity);

        float prevItemRight = float.NaN;
        float prevItemTop = float.NaN;
        float prevItemBottom = float.NaN;

        foreach (var item in items)
        {
            if (item.Text.Length == 0) continue;

            float iLeft   = (float)item.X;
            float iRight  = (float)(item.X + item.Width);
            float iTop    = (float)item.Y;
            float iBottom = (float)(item.Y + item.Height);
            float iHeight = iBottom - iTop;
            if (iHeight <= 0 || (iRight - iLeft) <= 0) continue;

            // Inter-item separator: space if same line, newline if not.
            // Mirrors LitePdfPigSession.BuildPageText so search /
            // selection semantics stay identical across backends.
            if (!float.IsNaN(prevItemRight))
            {
                float midY     = (iTop + iBottom) / 2f;
                float prevMidY = (prevItemTop + prevItemBottom) / 2f;
                float refLineH = Math.Max(1f, Math.Max(iHeight, prevItemBottom - prevItemTop));
                bool sameLine = Math.Abs(midY - prevMidY) <= refLineH * 0.5f;

                char lastChar  = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                char firstChar = item.Text[0];
                bool boundaryAlreadyWhitespace = char.IsWhiteSpace(lastChar) || char.IsWhiteSpace(firstChar);

                if (sameLine)
                {
                    if (!boundaryAlreadyWhitespace)
                    {
                        int idx = sb.Length;
                        sb.Append(' ');
                        float spaceTop    = Math.Min(prevItemTop, iTop);
                        float spaceBottom = Math.Max(prevItemBottom, iBottom);
                        boxes.Add(new CharBox(idx, prevItemRight, spaceTop, iLeft, spaceBottom));
                    }
                }
                else if (lastChar != '\n')
                {
                    int idx = sb.Length;
                    sb.Append('\n');
                    boxes.Add(new CharBox(idx, prevItemRight, prevItemTop,
                                          prevItemRight + 1f, prevItemBottom));
                }
            }

            // Split the item's horizontal extent evenly across its chars.
            // PDF.js doesn't give us per-glyph bounds, but for drag-select
            // and search hit-clustering even spacing is more than good
            // enough — both quantise glyphs by mid-X/mid-Y.
            int charCount = item.Text.Length;
            float charW = (iRight - iLeft) / charCount;
            for (int c = 0; c < charCount; c++)
            {
                int idx = sb.Length;
                sb.Append(item.Text[c]);
                float left  = iLeft + c * charW;
                float right = left + charW;
                boxes.Add(new CharBox(idx, left, iTop, right, iBottom));
            }

            prevItemRight  = iRight;
            prevItemTop    = iTop;
            prevItemBottom = iBottom;
        }

        return new PageText(sb.ToString(), boxes);
    }

    /// <summary>
    /// Word-level layout analysis. PDF.js text items are word-sized
    /// so column gutters fall naturally between items. Delegates to
    /// <see cref="DocstrumSegmenter.Segment"/>; returns both the
    /// blocks (with <see cref="LayoutBlock.Lines"/> populated) and a
    /// parallel list of per-line rects (the latter is what the rail
    /// overlay needs since <see cref="LineInfo"/> is just Y + Height).
    /// </summary>
    public async Task<DocstrumSegmenter.SegmentResult> GetBlocksAsync(int pageIndex)
    {
        var items = await _runtime.GetTextItemsAsync(_docId, pageIndex);
        var (pageW, pageH) = await _runtime.GetPageSizeAsync(_docId, pageIndex);
        return DocstrumSegmenter.Segment(items, pageW, pageH);
    }


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _runtime.CloseDoc(_docId); } catch { /* best-effort */ }
    }

}
