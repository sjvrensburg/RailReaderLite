using System;
using System.Collections.Generic;
using System.Linq;
using RailReader.Core.Models;

namespace RailReaderLite.Services;

/// <summary>
/// Bottom-up Docstrum-style segmentation on word-level bounding boxes.
/// Ported from RailDLA's <c>docstrum.py</c>, itself a port of PdfPig's
/// <c>DocstrumBoundingBoxes.cs</c>. Underlying algorithm: O'Gorman 1993,
/// "The Document Spectrum for Page Layout Analysis", axis-aligned
/// bounding-box-input variant.
///
/// <para>
/// Two passes:
/// <list type="number">
///   <item><description><b>Words → lines.</b> Histogram-peak estimate of
///   the within-line word gap. Union-find clustering on each word's
///   nearest right neighbour (within a vertical tolerance, horizontal
///   gap ≤ <c>3 × estimated within-line gap</c>).</description></item>
///   <item><description><b>Lines → blocks.</b> Histogram-peak estimate of
///   the between-line vertical gap. Union-find clustering on each
///   line's nearest below neighbour with the additional constraint
///   that their X-extents overlap by ≥ 10% of the shorter line —
///   that's what keeps interleaved columns in separate blocks at the
///   geometric level (no raster needed).</description></item>
/// </list>
/// </para>
///
/// <para>
/// Histogram-peak gap estimation is the key improvement over Lite
/// v0.7.0's fixed-threshold clusterer: the dominant bucket of the
/// nearest-neighbour distance distribution self-adapts to the
/// page's actual word spacing, so the same code works on tight
/// academic typesetting and loose body-copy alike.
/// </para>
/// </summary>
public static class DocstrumSegmenter
{
    // Defaults from RailDLA/docstrum.py, expressed as fractions of
    // the relevant page dimension. Lite's coords are page-points
    // (not normalised); we multiply by pageW / pageH at use.

    private const float WithinLineVerticalTolFracH      = 0.004f;
    private const float BetweenLineHorizontalTolFracW   = 0.005f;
    private const float WithinLineBinFracW              = 0.001f;
    private const float BetweenLineBinFracH             = 0.001f;
    private const float WithinLineMultiplier            = 3.0f;
    private const float BetweenLineMultiplier           = 1.3f;
    private const float LineOverlapMinFrac              = 0.10f;
    private const float FallbackWithinLineGapFracW      = 0.005f;
    private const float FallbackBetweenLineGapFracH     = 0.012f;

    /// <summary>Drop blocks smaller than this fraction of total page
    /// area. Ported from RailDLA's <c>BLOCK_MIN_AREA_FRAC</c>. Filters
    /// out scattered single-character markers (citation superscripts,
    /// margin dots, decoration glyphs) that bloat the block count and
    /// confuse Klampfl's column-wise reading order — its "strictly
    /// left of" rule ignores Y, so a stray narrow block can sort
    /// ahead of any wider block it precedes horizontally regardless
    /// of where the two sit vertically on the page.</summary>
    private const float BlockMinAreaFrac = 0.0006f;

    /// <summary>
    /// Runs the full Docstrum pipeline on PDF.js-supplied word-level
    /// text items. Returns one <see cref="LayoutBlock"/> per detected
    /// paragraph (or column-segment) with <see cref="LayoutBlock.Lines"/>
    /// populated, plus a parallel <c>LineRects</c> list giving the
    /// exact per-line bounding rect (which Core's <see cref="LineInfo"/>
    /// can't hold — it only carries Y + Height). The rail-mode overlay
    /// uses LineRects so the active-line highlight hugs the actual
    /// visible text instead of spanning the whole block width.
    /// Coordinates are page-point space (Y-down) throughout.
    /// </summary>
    public static SegmentResult Segment(
        IReadOnlyList<PdfTextItem> items, double pageW, double pageH)
    {
        if (items.Count == 0) return SegmentResult.Empty;

        // Filter degenerate items + collect rects.
        var words = new List<Word>(items.Count);
        foreach (var it in items)
        {
            float w = (float)it.Width;
            float h = (float)it.Height;
            if (w <= 0 || h <= 0) continue;
            words.Add(new Word((float)it.X, (float)it.Y, w, h));
        }
        if (words.Count == 0) return SegmentResult.Empty;

        // Median item height — used as a font-size proxy to floor
        // the histogram-peak gap estimates. PDF.js sometimes emits
        // text items per-character (when the source PDF positions
        // each glyph individually with kerning), and a raw peak
        // would lock onto the ~0pt intra-word spacing — leaving
        // no chance for words to merge into lines. Flooring the
        // gap at a fraction of the font height stops that.
        float medianH = MedianHeight(words);

        // ----- Phase 1: words → lines.

        float yTolWithin = (float)(pageH * WithinLineVerticalTolFracH);
        float xTolBetween = (float)(pageW * BetweenLineHorizontalTolFracW);
        float withinBin   = (float)(pageW * WithinLineBinFracW);
        float betweenBin  = (float)(pageH * BetweenLineBinFracH);

        var withinGaps  = WithinLineDistances(words, yTolWithin);
        var betweenGaps = BetweenLineDistances(words, xTolBetween);

        float withinGap = PeakBucketAverage(withinGaps, withinBin)
                          ?? (float)(pageW * FallbackWithinLineGapFracW);
        float betweenGap = PeakBucketAverage(betweenGaps, betweenBin)
                           ?? (float)(pageH * FallbackBetweenLineGapFracH);

        // Floor at 30 % of the font height for within-line, 80 % for
        // between-line. These come from O'Gorman's typical
        // body-typesetting ratios: word spaces are ~1/3 em, line
        // gaps are ~em-to-1.2em. The floor only kicks in when the
        // histogram peak is unreasonably small (character-level
        // text emission); on normal word-level extraction the
        // estimate dominates.
        if (withinGap < medianH * 0.30f)  withinGap  = medianH * 0.30f;
        if (betweenGap < medianH * 0.80f) betweenGap = medianH * 0.80f;

        float maxWithin  = withinGap  * WithinLineMultiplier;
        float maxBetween = betweenGap * BetweenLineMultiplier;

        // Hard cap on within-line max gap. Column gutters in
        // academic 2-column layouts are typically 18–40 pt; loose
        // justification can produce histogram-peak estimates wider
        // than 6 pt, and 3 × that overshoots the gutter — the line
        // clusterer ends up unioning an end-of-abstract word with
        // the first word of a figure caption next to it. The cap
        // below (≤ 2.5 % of page width) is smaller than every
        // gutter we've seen in real corpora, so within-line merging
        // never crosses a gutter, while normal inter-word merging
        // (typically &lt; 1 % of page width) is unaffected.
        float maxWithinAbsolute = (float)(pageW * 0.025f);
        if (maxWithin > maxWithinAbsolute) maxWithin = maxWithinAbsolute;

        var lines = BuildLines(words, maxWithin, yTolWithin);

        // ----- Phase 2: lines → blocks.

        var blocks = BuildBlocks(lines, maxBetween, LineOverlapMinFrac);

        // Emit LayoutBlocks + parallel per-line full RectF, filtering
        // by minimum area. Stray narrow blocks (single-character
        // markers, decoration glyphs) make Klampfl's column-wise
        // reading order misbehave — see BlockMinAreaFrac docs.
        double pageArea = pageW * pageH;
        float minArea = (float)(pageArea * BlockMinAreaFrac);
        var resultBlocks = new List<LayoutBlock>(blocks.Count);
        var resultRects = new List<List<RectF>>(blocks.Count);
        foreach (var b in blocks)
        {
            float minLeft = b.Min(l => l.Left);
            float minTop  = b.Min(l => l.Top);
            float maxRight = b.Max(l => l.Right);
            float maxBot   = b.Max(l => l.Bottom);
            float area = (maxRight - minLeft) * (maxBot - minTop);
            if (area < minArea) continue;

            var blockLines = b.Select(l => new LineInfo(
                (l.Top + l.Bottom) * 0.5f, l.Bottom - l.Top)).ToList();
            var perLineRects = b.Select(l => new RectF(l.Left, l.Top, l.Right, l.Bottom)).ToList();
            resultBlocks.Add(new LayoutBlock
            {
                BBox = new BBox(minLeft, minTop, maxRight - minLeft, maxBot - minTop),
                Role = BlockRole.Text,
                ClassId = 0,
                Confidence = 1.0f,
                Order = 0,
                Lines = blockLines,
            });
            resultRects.Add(perLineRects);
        }
        return new SegmentResult(resultBlocks, resultRects);
    }

    // ---- Histogram-peak gap estimation -------------------------------

    /// <summary>Average of the most-populated histogram bucket. Returns
    /// null if there are no samples; this is the signal the caller
    /// should fall back to a fixed gap.</summary>
    internal static float? PeakBucketAverage(List<float> values, float binSize)
    {
        if (values.Count == 0) return null;
        if (binSize <= 0) throw new ArgumentException("bin size must be positive", nameof(binSize));

        float vmax = 0;
        foreach (var v in values) if (v > vmax) vmax = v;
        int nBins = (int)(vmax / binSize) + 1;
        var bins = new List<float>[nBins];
        for (int i = 0; i < nBins; i++) bins[i] = new List<float>();

        foreach (var v in values)
        {
            if (v < 0) continue;  // shouldn't happen but be defensive
            int idx = Math.Min((int)(v / binSize), nBins - 1);
            bins[idx].Add(v);
        }

        int bestIdx = 0;
        for (int i = 1; i < nBins; i++)
            if (bins[i].Count > bins[bestIdx].Count) bestIdx = i;
        if (bins[bestIdx].Count == 0) return null;

        float sum = 0;
        foreach (var v in bins[bestIdx]) sum += v;
        return sum / bins[bestIdx].Count;
    }

    /// <summary>For each word, the horizontal gap to its nearest right-
    /// side neighbour whose vertical centre is within
    /// <paramref name="verticalTol"/>.</summary>
    private static List<float> WithinLineDistances(List<Word> words, float verticalTol)
    {
        var gaps = new List<float>();
        for (int i = 0; i < words.Count; i++)
        {
            float cyi = words[i].MidY;
            float right = words[i].Right;
            float best = float.PositiveInfinity;
            for (int j = 0; j < words.Count; j++)
            {
                if (i == j) continue;
                float cyj = words[j].MidY;
                if (Math.Abs(cyj - cyi) > verticalTol) continue;
                if (words[j].Left <= right) continue;  // not strictly to the right
                float gap = words[j].Left - right;
                if (gap < best) best = gap;
            }
            if (best != float.PositiveInfinity) gaps.Add(best);
        }
        return gaps;
    }

    /// <summary>For each word, the vertical gap to its nearest below-
    /// side neighbour whose horizontal extent overlaps this word's by
    /// more than <paramref name="horizontalTol"/>.</summary>
    private static List<float> BetweenLineDistances(List<Word> words, float horizontalTol)
    {
        var gaps = new List<float>();
        for (int i = 0; i < words.Count; i++)
        {
            float bottomI = words[i].Bottom;
            float rightI  = words[i].Right;
            float leftI   = words[i].Left;
            float best = float.PositiveInfinity;
            for (int j = 0; j < words.Count; j++)
            {
                if (i == j) continue;
                if (words[j].Top < bottomI) continue;  // not below
                float overlap = Math.Min(rightI, words[j].Right) - Math.Max(leftI, words[j].Left);
                if (overlap < horizontalTol) continue;
                float gap = words[j].Top - bottomI;
                if (gap < best) best = gap;
            }
            if (best != float.PositiveInfinity) gaps.Add(best);
        }
        return gaps;
    }

    // ---- Line construction (union-find) ------------------------------

    private static List<Line> BuildLines(List<Word> words, float maxGap, float verticalTol)
    {
        if (words.Count == 0) return [];
        // Sort by (midY, left) — gives deterministic line order.
        var sorted = words.OrderBy(w => w.MidY).ThenBy(w => w.Left).ToList();
        int n = sorted.Count;
        var parent = Enumerable.Range(0, n).ToArray();

        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        // Each word's nearest right neighbour within the line tolerances.
        for (int i = 0; i < n; i++)
        {
            float cyi = sorted[i].MidY;
            float right = sorted[i].Right;
            int best = -1;
            float bestGap = float.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                float cyj = sorted[j].MidY;
                if (Math.Abs(cyj - cyi) > verticalTol) continue;
                if (sorted[j].Left <= right) continue;
                float gap = sorted[j].Left - right;
                if (gap < bestGap) { bestGap = gap; best = j; }
            }
            if (best >= 0 && bestGap <= maxGap) Union(i, best);
        }

        // Group by root, build LineRects.
        var groups = new Dictionary<int, List<Word>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var list))
            {
                list = new List<Word>();
                groups[r] = list;
            }
            list.Add(sorted[i]);
        }
        var lines = new List<Line>(groups.Count);
        foreach (var g in groups.Values)
        {
            float left = g.Min(w => w.Left);
            float top  = g.Min(w => w.Top);
            float right = g.Max(w => w.Right);
            float bot   = g.Max(w => w.Bottom);
            lines.Add(new Line(left, top, right, bot));
        }
        lines.Sort((a, b) => a.MidY.CompareTo(b.MidY));
        return lines;
    }

    // ---- Block construction (union-find on lines) --------------------

    private static List<List<Line>> BuildBlocks(List<Line> lines, float maxGap, float overlapMin)
    {
        if (lines.Count == 0) return [];
        int n = lines.Count;
        var parent = Enumerable.Range(0, n).ToArray();

        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        for (int i = 0; i < n; i++)
        {
            float bottomI = lines[i].Bottom;
            int best = -1;
            float bestGap = float.PositiveInfinity;
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                if (lines[j].Top < bottomI - 1e-6f) continue;  // not below
                float overlap = Math.Min(lines[i].Right, lines[j].Right)
                                - Math.Max(lines[i].Left, lines[j].Left);
                float shorter = Math.Min(lines[i].Width, lines[j].Width);
                if (shorter <= 0 || overlap / shorter < overlapMin) continue;
                float gap = lines[j].Top - bottomI;
                if (gap < bestGap) { bestGap = gap; best = j; }
            }
            if (best >= 0 && bestGap <= maxGap) Union(i, best);
        }

        var groups = new Dictionary<int, List<Line>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var list))
            {
                list = new List<Line>();
                groups[r] = list;
            }
            list.Add(lines[i]);
        }
        // Sort lines within block top-to-bottom; sort blocks by their
        // first line's mid-Y so the LayoutBlock list is roughly in
        // top-to-bottom order before reading-order resolution.
        var blocks = new List<List<Line>>(groups.Count);
        foreach (var g in groups.Values)
        {
            g.Sort((a, b) => a.MidY.CompareTo(b.MidY));
            blocks.Add(g);
        }
        blocks.Sort((a, b) => a[0].MidY.CompareTo(b[0].MidY));
        return blocks;
    }

    private static float MedianHeight(List<Word> words)
    {
        var arr = new float[words.Count];
        for (int i = 0; i < words.Count; i++) arr[i] = words[i].H;
        Array.Sort(arr);
        return arr[arr.Length / 2];
    }

    // ---- Public result + internal types ----------------------------

    /// <summary>Result of <see cref="Segment"/>. <c>LineRects</c> is
    /// parallel to <c>Blocks[i].Lines</c> — same outer and inner
    /// indexing. Per-line rects are kept here rather than on
    /// <see cref="LineInfo"/> because <see cref="LineInfo"/> is a Core
    /// type that only carries Y + Height.</summary>
    public sealed record SegmentResult(
        List<LayoutBlock> Blocks,
        List<List<RectF>> LineRects)
    {
        public static SegmentResult Empty { get; } =
            new(new List<LayoutBlock>(), new List<List<RectF>>());
    }

    // ---- Internal types ----------------------------------------------

    internal readonly record struct Word(float Left, float Top, float W, float H)
    {
        public float Right  => Left + W;
        public float Bottom => Top  + H;
        public float MidY   => Top + H * 0.5f;
    }

    internal readonly record struct Line(float Left, float Top, float Right, float Bottom)
    {
        public float Width  => Right - Left;
        public float MidY   => (Top + Bottom) * 0.5f;
    }
}
