using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RailReader.Core.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace RailReaderLite.Services;

/// <summary>
/// A per-document session that owns a cached <see cref="PdfDocument"/>
/// and exposes the per-page work the Lite VM does (text extraction
/// for search + selection, block segmentation for rail mode).
///
/// <para>
/// Why this exists, given that <see cref="RailReader.Core.PdfPig.PdfTextService"/>
/// already exposes <c>ExtractPageText(byte[], int)</c>:
/// <list type="bullet">
///   <item><description><b>Caching</b> — the Core service re-opens the
///   <c>PdfDocument</c> on every call. For a 50-page PDF, navigating
///   between pages at high zoom (which triggers per-page analysis)
///   would re-parse the file every time, on the UI thread. Caching
///   the document brings the per-page cost down to word extraction
///   only.</description></item>
///   <item><description><b>Real Docstrum</b> — Core's
///   <c>DocstrumLayoutAnalyzer</c> is a charbox-only approximation
///   and lacks the word-aware horizontal-gap detection that splits
///   columns. PdfPig ships a proper
///   <see cref="DocstrumBoundingBoxes"/> that operates on
///   <see cref="Word"/>s and naturally separates columns; running it
///   here keeps the Lite consumer column-aware without changing the
///   Core analyzer.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Thread safety.</b> <see cref="PdfDocument"/> is not thread-safe;
/// every method serialises on <see cref="_gate"/>. Calls from
/// background work (<c>Task.Run</c>) are the expected pattern — the
/// VM dispatches results back to the UI thread.
/// </para>
/// </summary>
internal sealed class LitePdfPigSession : IDisposable
{
    private readonly PdfDocument _doc;
    private readonly object _gate = new();
    private bool _disposed;

    public LitePdfPigSession(byte[] pdfBytes)
    {
        _doc = PdfDocument.Open(pdfBytes);
        PageCount = _doc.NumberOfPages;
    }

    public int PageCount { get; }

    public (double Width, double Height) GetPageSize(int pageIndex)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= PageCount) return (0, 0);
            var page = _doc.GetPage(pageIndex + 1);
            return (page.Width, page.Height);
        }
    }

    /// <summary>
    /// Extracts the page's text in reading order along with one
    /// <see cref="CharBox"/> per character, in page-point space
    /// (origin top-left, Y-down). Mirrors the algorithm in
    /// <c>RailReader.Core.PdfPig.PdfTextService.BuildPageText</c> —
    /// the existing search + drag-to-copy paths depend on the same
    /// space/newline injection semantics, so this is a port rather
    /// than a re-design.
    /// </summary>
    public PageText GetPageText(int pageIndex)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= PageCount) return new PageText("", []);
            var page = _doc.GetPage(pageIndex + 1);
            return BuildPageText(page);
        }
    }

    /// <summary>
    /// Runs <see cref="DocstrumBoundingBoxes"/> on the page's words
    /// and converts the result into Core <see cref="LayoutBlock"/>s
    /// with <see cref="LayoutBlock.Lines"/> already populated from
    /// PdfPig's <see cref="TextLine"/> output. Coordinates are
    /// page-point, Y-down (origin top-left), matching the rest of
    /// Lite. <see cref="LayoutBlock.Role"/> is stamped as
    /// <see cref="BlockRole.Text"/> (Docstrum is unclassified —
    /// figures and tables collapse into the same role) and
    /// <see cref="LayoutBlock.Order"/> is left at zero;
    /// <c>XYCutPlusPlusResolver</c> in the VM assigns reading order
    /// afterwards.
    /// </summary>
    public List<LayoutBlock> GetBlocks(int pageIndex)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= PageCount) return [];
            var page = _doc.GetPage(pageIndex + 1);
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance);
            // PdfPig's Docstrum may throw on degenerate pages (no words /
            // single word with zero spacing stats). Guard so the VM
            // doesn't crash on title pages or scanned-fragment pages.
            IReadOnlyList<TextBlock> blocks;
            try
            {
                blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
            }
            catch
            {
                return [];
            }

            double pageH = page.Height;
            var result = new List<LayoutBlock>(blocks.Count);
            foreach (var b in blocks)
            {
                var bb = b.BoundingBox;
                float left   = (float)bb.Left;
                float right  = (float)bb.Right;
                float top    = (float)(pageH - bb.Top);     // Y-flip: PdfPig is Y-up
                float bottom = (float)(pageH - bb.Bottom);
                if (right <= left || bottom <= top) continue;

                var lines = new List<LineInfo>(b.TextLines.Count);
                foreach (var tl in b.TextLines)
                {
                    var lr = tl.BoundingBox;
                    float lt = (float)(pageH - lr.Top);
                    float lb = (float)(pageH - lr.Bottom);
                    if (lb <= lt) continue;
                    lines.Add(new LineInfo((lt + lb) * 0.5f, lb - lt));
                }
                if (lines.Count == 0) continue;

                result.Add(new LayoutBlock
                {
                    BBox = new BBox(left, top, right - left, bottom - top),
                    Role = BlockRole.Text,
                    ClassId = 0,
                    Confidence = 1.0f,
                    Order = 0,
                    Lines = lines,
                });
            }
            return result;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _doc.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LitePdfPigSession));
    }

    // ----- Per-page text/char-box builder (ported from Core's
    // PdfTextService.BuildPageText so search + selection see identical
    // text under both backends. Y-flips PdfPig's Y-up rects into the
    // Y-down coordinate frame Lite uses everywhere downstream).

    private static PageText BuildPageText(Page page)
    {
        var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
        if (words.Count == 0) return new PageText("", []);

        double pageH = page.Height;
        var sb = new StringBuilder(words.Sum(w => w.Letters.Count));
        var boxes = new List<CharBox>(sb.Capacity);

        float prevWordRight = float.NaN;
        float prevWordTop = float.NaN;
        float prevWordBottom = float.NaN;

        foreach (var word in words)
        {
            var wbox = word.BoundingBox;
            float wLeft   = (float)wbox.Left;
            float wRight  = (float)wbox.Right;
            float wTop    = (float)(pageH - wbox.Top);
            float wBottom = (float)(pageH - wbox.Bottom);
            float wHeight = wBottom - wTop;

            if (!float.IsNaN(prevWordRight))
            {
                float midY     = (wTop + wBottom) / 2f;
                float prevMidY = (prevWordTop + prevWordBottom) / 2f;
                float refLineH = Math.Max(1f, Math.Max(wHeight, prevWordBottom - prevWordTop));
                bool sameLine = Math.Abs(midY - prevMidY) <= refLineH * 0.5f;

                char lastChar  = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                char firstChar = word.Text.Length > 0 ? word.Text[0] : '\0';
                bool boundaryAlreadyWhitespace = char.IsWhiteSpace(lastChar) || char.IsWhiteSpace(firstChar);

                if (sameLine)
                {
                    if (!boundaryAlreadyWhitespace)
                    {
                        int idx = sb.Length;
                        sb.Append(' ');
                        float spaceTop    = Math.Min(prevWordTop, wTop);
                        float spaceBottom = Math.Max(prevWordBottom, wBottom);
                        boxes.Add(new CharBox(idx, prevWordRight, spaceTop, wLeft, spaceBottom));
                    }
                }
                else if (lastChar != '\n')
                {
                    int idx = sb.Length;
                    sb.Append('\n');
                    boxes.Add(new CharBox(idx, prevWordRight, prevWordTop,
                                          prevWordRight + 1f, prevWordBottom));
                }
            }

            foreach (var letter in word.Letters)
            {
                var rect = letter.BoundingBox;
                float left   = (float)rect.Left;
                float right  = (float)rect.Right;
                float top    = (float)(pageH - rect.Top);
                float bottom = (float)(pageH - rect.Bottom);

                string value = letter.Value ?? "";
                if (value.Length == 0)
                {
                    int index = sb.Length;
                    sb.Append('�');
                    boxes.Add(new CharBox(index, left, top, right, bottom));
                }
                else
                {
                    foreach (var ch in value)
                    {
                        int index = sb.Length;
                        sb.Append(ch);
                        boxes.Add(new CharBox(index, left, top, right, bottom));
                    }
                }
            }

            prevWordRight  = wRight;
            prevWordTop    = wTop;
            prevWordBottom = wBottom;
        }

        return new PageText(sb.ToString(), boxes);
    }
}
