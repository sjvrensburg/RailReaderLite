using System;
using System.Collections.Generic;
using System.Linq;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailReaderLite.Services;

/// <summary>
/// Unsupervised reading-order detection via Allen's 13 interval
/// relations. Ported from RailDLA's <c>reading_order.py</c> (which is
/// itself a port of PdfPig's <c>UnsupervisedReadingOrderDetector.cs</c>;
/// the underlying algorithm is Klampfl, Granitzer, Jack &amp; Kern
/// "Unsupervised document structure analysis of digital scientific
/// articles" (2014), Section 4.1, built on Todoran et al. "Document
/// Understanding for a Broad Class of Documents").
///
/// <para>
/// Each block projects to an X-interval and a Y-interval. Two blocks
/// have a <em>before-in-reading</em> relation when their X/Y
/// interval-pair relations (via Allen's algebra) match one of a small
/// set of patterns. The patterns vary by reading style — column-wise
/// (default for academic papers with multi-column text) or row-wise.
/// The relations form a directed graph; a reading order is extracted
/// by repeatedly removing the node with the most outgoing edges
/// (i.e. the block that should come earliest).
/// </para>
///
/// <para>
/// Per <c>RailDLA/PORTING.md §3.5</c>: in the RailDLA corpus, ~26% of
/// pages have <em>some</em> disagreement between Klampfl and the naive
/// (strip, column, y) lattice sort or the XY-cut variant; on every
/// audited disagreement Klampfl is the correct one. This is what
/// Lite v0.7.0 was missing — XYCutPlusPlusResolver gets confused by
/// interleaved columns.
/// </para>
/// </summary>
public sealed class KlampflReadingOrder : IReadingOrderResolver
{
    /// <summary>Alignment tolerance — edges within this distance are
    /// treated as equal in Allen's relation. Page coordinates are
    /// page-points (not normalised), so the default scales with the
    /// page width passed to <see cref="AssignOrder"/>.</summary>
    private const float TolFractionOfPageWidth = 0.005f;

    private readonly ReadingMode _mode;

    public KlampflReadingOrder(ReadingMode mode = ReadingMode.ColumnWise)
    {
        _mode = mode;
    }

    public void AssignOrder(IList<LayoutBlock> blocks, double pageWidth, double pageHeight)
    {
        if (blocks.Count <= 1)
        {
            for (int i = 0; i < blocks.Count; i++) blocks[i].Order = i;
            return;
        }
        float tol = (float)(pageWidth * TolFractionOfPageWidth);
        var order = DetectOrder(blocks, tol, _mode);
        for (int rank = 0; rank < order.Length; rank++)
            blocks[order[rank]].Order = rank;
    }

    /// <summary>Returns the reading-order permutation: indices into
    /// <paramref name="blocks"/> in reading order.</summary>
    public static int[] DetectOrder(IList<LayoutBlock> blocks, float tol, ReadingMode mode)
    {
        int n = blocks.Count;
        var outEdges = new HashSet<int>[n];
        for (int i = 0; i < n; i++) outEdges[i] = new HashSet<int>();

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;
                if (BeforeInReading(blocks[i].BBox, blocks[j].BBox, tol, mode))
                    outEdges[i].Add(j);
            }
        }

        var remaining = new HashSet<int>(Enumerable.Range(0, n));
        var order = new int[n];
        int rank = 0;
        while (remaining.Count > 0)
        {
            // Pick the node with the most outgoing edges among the
            // remaining. Tie-break by smallest index → deterministic.
            int best = -1;
            int bestEdges = -1;
            foreach (var k in remaining)
            {
                int edges = outEdges[k].Count;
                if (edges > bestEdges || (edges == bestEdges && (best < 0 || k < best)))
                {
                    bestEdges = edges;
                    best = k;
                }
            }
            order[rank++] = best;
            remaining.Remove(best);
            // Drop best from every other node's out-edges so the next
            // iteration ranks among the rest.
            foreach (var k in remaining) outEdges[k].Remove(best);
        }
        return order;
    }

    // --- Allen relations ------------------------------------------------

    /// <summary>Allen's 13 interval relations. Names mirror the
    /// canonical paper labels: <c>P</c>recedes, <c>M</c>eets,
    /// <c>O</c>verlaps, <c>S</c>tarts, <c>D</c>uring, <c>F</c>inishes,
    /// <c>E</c>quals, and the inverse of each (suffixed
    /// <c>I</c>).</summary>
    private enum Relation { P, PI, M, MI, O, OI, S, SI, D, DI, F, FI, E }

    private static Relation IntervalRelation(float a0, float a1, float b0, float b1, float tol)
    {
        bool eqStart = Math.Abs(a0 - b0) <= tol;
        bool eqEnd   = Math.Abs(a1 - b1) <= tol;

        if (eqStart && eqEnd) return Relation.E;

        if (a1 < b0 - tol) return Relation.P;
        if (b1 < a0 - tol) return Relation.PI;

        if (Math.Abs(a1 - b0) <= tol) return Relation.M;
        if (Math.Abs(b1 - a0) <= tol) return Relation.MI;

        if (eqStart) return a1 < b1 ? Relation.S : Relation.SI;
        if (eqEnd)   return a0 > b0 ? Relation.F : Relation.FI;

        if (a0 > b0 + tol && a1 < b1 - tol) return Relation.D;
        if (b0 > a0 + tol && b1 < a1 - tol) return Relation.DI;

        if (a0 < b0 && a1 < b1) return Relation.O;
        return Relation.OI;
    }

    private static Relation XRel(BBox a, BBox b, float tol) =>
        IntervalRelation(a.X, a.X + a.W, b.X, b.X + b.W, tol);

    private static Relation YRel(BBox a, BBox b, float tol) =>
        IntervalRelation(a.Y, a.Y + a.H, b.Y, b.Y + b.H, tol);

    // The "before in reading" predicates use two relation sets per
    // axis. Members of `PreMeetOver` are "strictly before / touching
    // / partly before". The wider `LeftOfFull` (or YTopFull) is used
    // when the orthogonal axis already orders the pair.

    private static readonly HashSet<Relation> PreMeetOver = new()
    {
        Relation.P, Relation.M, Relation.O,
    };

    private static readonly HashSet<Relation> XLeftOfFull = new()
    {
        Relation.P, Relation.M, Relation.O,
        Relation.S, Relation.FI, Relation.E,
        Relation.D, Relation.DI, Relation.F,
        Relation.SI, Relation.OI,
    };

    private static readonly HashSet<Relation> YTopOfFull = XLeftOfFull;

    public static bool BeforeInReading(BBox a, BBox b, float tol, ReadingMode mode)
    {
        var xr = XRel(a, b, tol);
        var yr = YRel(a, b, tol);

        return mode switch
        {
            ReadingMode.Basic =>
                PreMeetOver.Contains(xr) || PreMeetOver.Contains(yr),

            ReadingMode.ColumnWise =>
                // Strictly left of b (any Y).
                xr is Relation.P or Relation.M
                // Overlaps X but above-and-left.
                || (xr == Relation.O && PreMeetOver.Contains(yr))
                // Vertically before b AND not horizontally after.
                || (PreMeetOver.Contains(yr) && XLeftOfFull.Contains(xr)),

            ReadingMode.RowWise =>
                yr is Relation.P or Relation.M
                || (yr == Relation.O && PreMeetOver.Contains(xr))
                || (PreMeetOver.Contains(xr) && YTopOfFull.Contains(yr)),

            _ => false,
        };
    }
}

public enum ReadingMode { Basic, ColumnWise, RowWise }
