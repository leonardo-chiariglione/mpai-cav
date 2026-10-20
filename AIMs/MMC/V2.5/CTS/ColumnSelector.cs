using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Mpai.Mmc.Cts;

// One line of a page, as Optical Character Recognition gives it: its text and its box, in the pixels of the page.
public sealed record PageLine(string Text, double X0, double Y0, double X1, double Y1)
{
    public double Height => Y1 - Y0;
    public double Width => X1 - X0;
    public double CentreX => (X0 + X1) / 2;
}

// COLUMN TEXT SELECTION, the logic (MMC-CTS-V2.5). From the pieces of a page that OCR gives and a point on it, the text of the
// column the point is in: simple tests, not made too intelligent (the author, 2026/09/29).
//
//  FROM THE POINT. The piece nearest the point is the first of the column. The column grows from it: a piece joins when it lies
//  over the column's width (at least half of the narrower of the two over the other) and is next to the column vertically - a
//  gap of at most GapFactor line heights from a piece already in it. The column's width is that of the pieces in it, so it grows
//  with the first full-width line that joins: the words of a justified line, which OCR gives in pieces, then join too. A piece of
//  the next column, which at most touches it, does not; neither does a headline (a piece much taller than the others), or a
//  piece wider than most of the page.
//  THE TEXT. The pieces by row, down the page, the pieces of a row from left to right; a word divided by a hyphen at the end of a
//  line is joined.
//  WITH NO POINT: the column of the topmost piece that is not a headline (the leftmost, of those on the top row).
public sealed class ColumnSelector
{
    public double GapFactor { get; init; } = 2.6;           // line heights: a list set with wide spacing is one column; the band that OCR reads bounds its length
    public double HeadlineFactor { get; init; } = 1.45;     // times the median piece height
    public double WidePage { get; init; } = 0.6;            // a piece wider than this share of the page is not in a column
    public double OverlapShare { get; init; } = 0.5;        // of the narrower piece that must lie over the other

    public sealed record Column(string Text, IReadOnlyList<PageLine> Lines);

    public Column? Select(IReadOnlyList<PageLine> lines, (double X, double Y)? point)
    {
        var real = lines.Where(l => l.Width > 0 && l.Height > 0 && !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (real.Count == 0) return null;

        double medianHeight = real.Select(l => l.Height).OrderBy(h => h).ElementAt(real.Count / 2);
        double pageWidth = real.Max(l => l.X1) - real.Min(l => l.X0);
        var body = real.Where(l => l.Height <= HeadlineFactor * medianHeight && l.Width < WidePage * pageWidth).ToList();
        if (body.Count == 0) return null;

        PageLine first = point is { } p
            ? body.OrderBy(l => Distance(l, p.X, p.Y)).ThenBy(l => l.Y0).First()
            : body.OrderBy(l => Math.Round(l.Y0 / medianHeight)).ThenBy(l => l.X0).First();

        var column = new List<PageLine> { first };
        double x0 = first.X0, x1 = first.X1;
        var rest = body.Where(l => !ReferenceEquals(l, first)).ToList();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var l in rest.ToList())
            {
                if (Overlap(l, x0, x1) < OverlapShare) continue;
                if (column.Min(c => VerticalGap(c, l)) > GapFactor * medianHeight) continue;
                column.Add(l); rest.Remove(l); grew = true;
                x0 = Math.Min(x0, l.X0); x1 = Math.Max(x1, l.X1);
            }
        }
        var rows = Rows(column);
        return new Column(Join(rows), rows);
    }

    // The share of the narrower of a piece and an x range that lies over the other.
    private static double Overlap(PageLine l, double x0, double x1)
    {
        double over = Math.Min(l.X1, x1) - Math.Max(l.X0, x0);
        double narrower = Math.Min(l.Width, x1 - x0);
        return narrower <= 0 ? 0 : Math.Max(0, over) / narrower;
    }

    // The vertical distance between two boxes: zero when they share a height.
    private static double VerticalGap(PageLine a, PageLine b) => Math.Max(0, Math.Max(a.Y0, b.Y0) - Math.Min(a.Y1, b.Y1));

    // The distance from a point to a piece's box: zero within it.
    private static double Distance(PageLine l, double x, double y)
    {
        double dx = x < l.X0 ? l.X0 - x : x > l.X1 ? x - l.X1 : 0;
        double dy = y < l.Y0 ? l.Y0 - y : y > l.Y1 ? y - l.Y1 : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // The pieces of the column by row - pieces whose vertical spans overlap are one row - down the page, left to right in a row.
    private static List<PageLine> Rows(List<PageLine> pieces)
    {
        var rows = new List<PageLine>();
        foreach (var p in pieces.OrderBy(p => p.X0))
        {
            int at = rows.FindIndex(r => Math.Min(r.Y1, p.Y1) - Math.Max(r.Y0, p.Y0) >= 0.5 * Math.Min(r.Height, p.Height));
            if (at < 0) rows.Add(p);
            else
            {
                var r = rows[at];
                rows[at] = new PageLine(r.Text + " " + p.Text, Math.Min(r.X0, p.X0), Math.Min(r.Y0, p.Y0), Math.Max(r.X1, p.X1), Math.Max(r.Y1, p.Y1));
            }
        }
        return rows.OrderBy(r => r.Y0).ToList();
    }

    // The lines as one text: a word divided by a hyphen at the end of a line is joined with the rest of it.
    private static string Join(List<PageLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var text = raw.Text.Trim();
            if (text.Length == 0) continue;
            if (sb.Length > 1 && sb[^1] == '-' && char.IsLetter(sb[^2]) && char.IsLower(text[0]))
                sb.Length--;                                          // join: "recog-" + "nition"
            else if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(text);
        }
        return sb.ToString();
    }
}
