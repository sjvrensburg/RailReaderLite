using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RailReader.Core.Models;

namespace RailReaderLite.Services;

/// <summary>
/// Detect decoration blocks (running headers / footers / page numbers)
/// by cross-page text repetition at border positions. Ported from
/// RailDLA's <c>decoration.py</c>, itself a port of PdfPig's
/// <c>DecorationTextBlockClassifier.cs</c>. Underlying algorithm:
/// Klampfl et al. 2014, Section 5.1.
///
/// <para>
/// Why this matters for Lite: rail mode in v0.7.0 happily walks
/// through page numbers and running headers as if they were body
/// paragraphs. Decoration detection uses corpus-wide repetition as
/// the signal — text that appears in approximately the same border
/// position on most pages, with approximately the same content
/// (digit-normalised), is decoration. No model needed.
/// </para>
///
/// <para>
/// Performance notes preserved from the source:
/// <list type="bullet">
///   <item><description>The <c>MaxDecorationLen = 500</c> length
///   fast-path is non-negotiable. When both compared texts exceed it,
///   we return content-similarity = 0 without running Levenshtein.
///   Decoration is short by definition; without the fast-path,
///   body-paragraph-vs-body-paragraph Levenshtein dominates runtime.</description></item>
///   <item><description>For documents with &gt;3 pages, compare page
///   p to pages p±2 (not p±1) to handle alternating headers on
///   left/right pages (duplex layouts).</description></item>
/// </list>
/// </para>
/// </summary>
public static class KlampflDecoration
{
    public const float DefaultSimilarityThreshold = 0.25f;
    public const int DefaultN = 5;
    private const int MaxDecorationLen = 500;

    private static readonly Regex NumberPattern = new(
        @"(\d+)|(\b[mdclxvi]+\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string NumberReplacement = "@";

    /// <summary>
    /// Returns per-page sets of decoration-block indices into the
    /// original per-page block list. Returns null when fewer than 2
    /// pages are supplied (decoration detection requires cross-page
    /// repetition).
    /// </summary>
    public static List<HashSet<int>>? DetectDecoration(
        IReadOnlyList<IReadOnlyList<BlockWithText>> pagesBlocks,
        float similarityThreshold = DefaultSimilarityThreshold,
        int n = DefaultN)
    {
        int nPages = pagesBlocks.Count;
        if (nPages < 2) return null;

        var out_ = new List<HashSet<int>>(nPages);
        for (int i = 0; i < nPages; i++) out_.Add(new HashSet<int>());

        // Four sort orders — each entry's block position in the order
        // matters for the per-position cross-page comparison.
        var orderings = new Func<IReadOnlyList<BlockWithText>, List<(int Idx, BlockWithText B)>>[]
        {
            blocks => blocks.Select((b, i) => (i, b))
                .OrderBy(t => t.b.Y).ThenBy(t => t.b.X).ToList(),
            blocks => blocks.Select((b, i) => (i, b))
                .OrderByDescending(t => t.b.Y + t.b.H).ThenBy(t => t.b.X).ToList(),
            blocks => blocks.Select((b, i) => (i, b))
                .OrderBy(t => t.b.X).ThenBy(t => t.b.Y).ToList(),
            blocks => blocks.Select((b, i) => (i, b))
                .OrderByDescending(t => t.b.X + t.b.W).ThenBy(t => t.b.Y).ToList(),
        };

        for (int p = 0; p < nPages; p++)
        {
            var curBlocks = pagesBlocks[p];
            if (curBlocks.Count == 0) continue;
            int pm = PrevPageIndex(p, nPages);
            int pp = NextPageIndex(p, nPages);
            var prevBlocks = pagesBlocks[pm];
            var nextBlocks = pagesBlocks[pp];
            var seen = out_[p];

            foreach (var orderer in orderings)
            {
                var curSorted = orderer(curBlocks);
                var prevSortedB = orderer(prevBlocks).Select(t => t.B).ToList();
                var nextSortedB = orderer(nextBlocks).Select(t => t.B).ToList();

                int nCurrent = Math.Min(n, curSorted.Count);
                for (int i = 0; i < nCurrent; i++)
                {
                    var (idx, block) = curSorted[i];
                    if (seen.Contains(idx)) continue;
                    float s = Score(block, prevSortedB, nextSortedB, similarityThreshold, n);
                    if (s >= similarityThreshold) seen.Add(idx);
                }
            }
        }
        return out_;
    }

    private static float Score(BlockWithText current,
        IReadOnlyList<BlockWithText> prev, IReadOnlyList<BlockWithText> next,
        float threshold, int n)
    {
        int k = Math.Min(n, Math.Min(prev.Count, next.Count));
        float best = 0f;
        for (int i = 0; i < k; i++)
        {
            float s = 0.5f * (Similarity(current, prev[i]) + Similarity(current, next[i]));
            if (s > best) best = s;
            if (best >= threshold) return best;
        }
        return best;
    }

    private static float Similarity(BlockWithText a, BlockWithText b) =>
        ContentSimilarity(a.Text, b.Text) * GeometricSimilarity(a, b);

    /// <summary>1 - normalised Levenshtein, with the
    /// <see cref="MaxDecorationLen"/> length fast-path that short-circuits
    /// body-vs-body comparisons.</summary>
    public static float ContentSimilarity(string a, string b)
    {
        string na = Normalise(a);
        string nb = Normalise(b);
        if (na.Length > MaxDecorationLen && nb.Length > MaxDecorationLen) return 0f;
        if (na.Length == 0 && nb.Length == 0) return 1f;
        int d = Levenshtein(na, nb);
        return 1f - (float)d / Math.Max(na.Length, nb.Length);
    }

    /// <summary>Intersection area / max(area_a, area_b). Returns 0 if
    /// the blocks don't intersect.</summary>
    private static float GeometricSimilarity(BlockWithText a, BlockWithText b)
    {
        float ix1 = Math.Max(a.X, b.X);
        float iy1 = Math.Max(a.Y, b.Y);
        float ix2 = Math.Min(a.X + a.W, b.X + b.W);
        float iy2 = Math.Min(a.Y + a.H, b.Y + b.H);
        if (ix2 <= ix1 || iy2 <= iy1) return 0f;
        float interArea = (ix2 - ix1) * (iy2 - iy1);
        float aArea = a.W * a.H;
        float bArea = b.W * b.H;
        return interArea / Math.Max(aArea, bArea);
    }

    /// <summary>Replace digit and roman-numeral runs with <c>@</c> so
    /// "Page 1" / "Page 2" compare equal under the content similarity.</summary>
    public static string Normalise(string text) =>
        NumberPattern.Replace(text, NumberReplacement);

    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            char ca = a[i - 1];
            for (int j = 1; j <= b.Length; j++)
            {
                int ins = cur[j - 1] + 1;
                int del = prev[j] + 1;
                int sub = prev[j - 1] + (ca == b[j - 1] ? 0 : 1);
                cur[j] = Math.Min(Math.Min(ins, del), sub);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static int PrevPageIndex(int p, int nPages)
    {
        int pm = p - 1 >= 0 ? p - 1 : nPages - 1;
        if (nPages > 3) pm = pm - 1 >= 0 ? pm - 1 : nPages - 1;
        return pm;
    }

    private static int NextPageIndex(int p, int nPages)
    {
        int pp = p + 1 < nPages ? p + 1 : 0;
        if (nPages > 3) pp = pp + 1 < nPages ? pp + 1 : 0;
        return pp;
    }
}

/// <summary>Minimal block carrier — what the classifier needs. The VM
/// builds one of these per <see cref="LayoutBlock"/> by concatenating
/// the text of items inside the block's bbox.</summary>
public sealed record BlockWithText(float X, float Y, float W, float H, string Text);
