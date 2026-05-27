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

    /// <summary>
    /// Legacy fixed-threshold clusterer — superseded by
    /// <see cref="DocstrumSegmenter.Segment"/> in v0.8.0. Kept here
    /// only as a reference / fallback during the bring-up; will be
    /// removed once the Docstrum port has been validated against
    /// the user's PDF corpus.
    /// </summary>
    internal static List<LayoutBlock> ClusterItemsIntoBlocks(IReadOnlyList<PdfTextItem> items)
    {
        if (items.Count == 0) return [];

        // Filter degenerate items + collect stats.
        var clean = new List<TextRect>(items.Count);
        foreach (var it in items)
        {
            float w = (float)it.Width;
            float h = (float)it.Height;
            if (w <= 0 || h <= 0) continue;
            float left = (float)it.X;
            float top  = (float)it.Y;
            clean.Add(new TextRect(left, top, left + w, top + h));
        }
        if (clean.Count == 0) return [];

        float medianH = MedianBy(clean, r => r.Bottom - r.Top);
        float medianW = MedianBy(clean, r => r.Right - r.Left);
        if (medianH <= 0) return [];

        // ----- Phase 1: items → lines (mid-Y cluster + horizontal-gap split).

        // Sort by mid-Y, then by left within ties.
        clean.Sort((a, b) =>
        {
            int c = a.MidY.CompareTo(b.MidY);
            return c != 0 ? c : a.Left.CompareTo(b.Left);
        });

        float yClusterThreshold = medianH * 0.5f;
        // Within-line horizontal gap that splits at a column gutter.
        // 2 × median word width is conservative: normal inter-word gaps
        // are typically < 1 × word width.
        float xGapSplit = medianW * 2.0f;

        var rawLines = new List<List<TextRect>>();
        var current = new List<TextRect> { clean[0] };
        float sumMidY = clean[0].MidY;

        for (int i = 1; i < clean.Count; i++)
        {
            float meanMidY = sumMidY / current.Count;
            if (Math.Abs(clean[i].MidY - meanMidY) > yClusterThreshold)
            {
                rawLines.Add(current);
                current = new List<TextRect> { clean[i] };
                sumMidY = clean[i].MidY;
            }
            else
            {
                current.Add(clean[i]);
                sumMidY += clean[i].MidY;
            }
        }
        rawLines.Add(current);

        // Within each rawLine, sort by Left and split on horizontal
        // gaps that exceed xGapSplit. Each resulting sub-line is one
        // text line in one column.
        var lines = new List<LineRect>(rawLines.Count);
        foreach (var rl in rawLines)
        {
            rl.Sort((a, b) => a.Left.CompareTo(b.Left));
            int start = 0;
            for (int i = 1; i < rl.Count; i++)
            {
                float gap = rl[i].Left - rl[i - 1].Right;
                if (gap > xGapSplit)
                {
                    lines.Add(BuildLineRect(rl, start, i));
                    start = i;
                }
            }
            lines.Add(BuildLineRect(rl, start, rl.Count));
        }

        if (lines.Count == 0) return [];

        // ----- Phase 2: lines → blocks (column-aware vertical clustering).

        // Median vertical gap and line height for the block-split
        // threshold. Same heuristic as Docstrum's `medianGap × 1.4`,
        // floored at 0.4 × median line height to avoid degenerate
        // gap stats on single-line pages.
        float medianLineH = MedianBy(lines, l => l.Bottom - l.Top);
        var verticalGaps = new List<float>();
        var byY = lines.OrderBy(l => l.Top).ToList();
        for (int i = 1; i < byY.Count; i++)
        {
            float g = byY[i].Top - byY[i - 1].Bottom;
            if (g > 0) verticalGaps.Add(g);
        }
        float medianGap = verticalGaps.Count > 0 ? Median(verticalGaps) : medianLineH * 0.5f;
        float gapBreak  = Math.Max(1.4f * medianGap, 0.4f * medianLineH);

        // Sort lines by (Top, Left). Greedily extend the most recent
        // X-overlapping block whose vertical gap is below the threshold.
        var sortedLines = lines.OrderBy(l => l.Top).ThenBy(l => l.Left).ToList();
        var blocks = new List<List<LineRect>>();
        var blockBboxes = new List<TextRect>();

        foreach (var ln in sortedLines)
        {
            int hostIdx = -1;
            // Search most-recent-first; column lines tend to be
            // contiguous in the sort and the most recent block is
            // usually the right candidate.
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                var bbox = blockBboxes[i];
                // X-overlap requires a non-trivial intersection: the
                // median word width is a reasonable floor — anything
                // less is probably a marginal note column, not the
                // same block.
                float overlap = Math.Min(ln.Right, bbox.Right) - Math.Max(ln.Left, bbox.Left);
                if (overlap < medianW * 0.5f) continue;

                float vGap = ln.Top - bbox.Bottom;
                if (vGap < 0) vGap = 0;  // overlapping lines (rare)
                if (vGap > gapBreak) continue;

                hostIdx = i;
                break;
            }

            if (hostIdx >= 0)
            {
                blocks[hostIdx].Add(ln);
                var b = blockBboxes[hostIdx];
                blockBboxes[hostIdx] = new TextRect(
                    Math.Min(b.Left, ln.Left),
                    Math.Min(b.Top,  ln.Top),
                    Math.Max(b.Right, ln.Right),
                    Math.Max(b.Bottom, ln.Bottom));
            }
            else
            {
                blocks.Add(new List<LineRect> { ln });
                blockBboxes.Add(new TextRect(ln.Left, ln.Top, ln.Right, ln.Bottom));
            }
        }

        // Emit LayoutBlocks.
        var result = new List<LayoutBlock>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            var bx = blockBboxes[i];
            // Sort lines top-to-bottom within block.
            var memberLines = blocks[i].OrderBy(l => l.Top).ToList();
            var lineInfos = new List<LineInfo>(memberLines.Count);
            foreach (var ml in memberLines)
                lineInfos.Add(new LineInfo((ml.Top + ml.Bottom) * 0.5f, ml.Bottom - ml.Top));

            result.Add(new LayoutBlock
            {
                BBox = new BBox(bx.Left, bx.Top, bx.Right - bx.Left, bx.Bottom - bx.Top),
                Role = BlockRole.Text,
                ClassId = 0,
                Confidence = 1.0f,
                Order = 0,
                Lines = lineInfos,
            });
        }
        return result;
    }

    private static LineRect BuildLineRect(List<TextRect> src, int start, int endExclusive)
    {
        float minLeft = float.PositiveInfinity;
        float minTop = float.PositiveInfinity;
        float maxRight = float.NegativeInfinity;
        float maxBottom = float.NegativeInfinity;
        for (int i = start; i < endExclusive; i++)
        {
            var r = src[i];
            if (r.Left   < minLeft)   minLeft   = r.Left;
            if (r.Top    < minTop)    minTop    = r.Top;
            if (r.Right  > maxRight)  maxRight  = r.Right;
            if (r.Bottom > maxBottom) maxBottom = r.Bottom;
        }
        return new LineRect(minLeft, minTop, maxRight, maxBottom);
    }

    private static float MedianBy<T>(List<T> src, Func<T, float> selector)
    {
        var arr = new float[src.Count];
        for (int i = 0; i < src.Count; i++) arr[i] = selector(src[i]);
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    private static float Median(List<float> values)
    {
        var arr = values.ToArray();
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _runtime.CloseDoc(_docId); } catch { /* best-effort */ }
    }

    private readonly record struct TextRect(float Left, float Top, float Right, float Bottom)
    {
        public float MidY   => (Top + Bottom) * 0.5f;
    }

    private readonly record struct LineRect(float Left, float Top, float Right, float Bottom);
}
