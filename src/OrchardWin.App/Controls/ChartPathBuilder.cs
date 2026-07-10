using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace OrchardWin.App.Controls;

/// Builds smooth chart geometries from discrete sample points using a Catmull–Rom spline
/// converted to cubic Bézier segments (polished monitoring-UI curves, not jagged polylines).
internal static class ChartPathBuilder
{
    /// Light 3-point weighted soften. Softens single-sample spikes without erasing shape.
    /// <paramref name="strength"/> 0 = raw, 1 = full 1-2-1 average.
    public static IReadOnlyList<double> SoftenSeries(IReadOnlyList<double> values, double strength = 0.55)
    {
        if (values.Count < 3 || strength <= 0) return values;

        strength = Math.Clamp(strength, 0, 1);
        var result = new double[values.Count];
        result[0] = values[0];
        result[^1] = values[^1];
        for (var i = 1; i < values.Count - 1; i++)
        {
            var avg = (values[i - 1] + values[i] * 2.0 + values[i + 1]) / 4.0;
            result[i] = values[i] * (1.0 - strength) + avg * strength;
        }
        return result;
    }

    /// Map value samples into plot coordinates (origin top-left, Y down).
    public static List<Point> ToPoints(
        IReadOnlyList<double> values,
        double width,
        double height,
        double max,
        double padTop = 2,
        double padBottom = 2)
    {
        var usableH = Math.Max(1, height - padTop - padBottom);
        var points = new List<Point>(values.Count);
        if (values.Count == 0) return points;

        if (values.Count == 1)
        {
            var n = Math.Clamp(values[0] / max, 0, 1);
            points.Add(new Point(0, padTop + (1 - n) * usableH));
            return points;
        }

        var step = width / (values.Count - 1);
        for (var i = 0; i < values.Count; i++)
        {
            var n = Math.Clamp(values[i] / max, 0, 1);
            points.Add(new Point(i * step, padTop + (1 - n) * usableH));
        }
        return points;
    }

    /// Mirrored chart points around a center baseline.
    /// <paramref name="plotBelow"/> true → grow downward (downloads / write); false → upward (uploads / read).
    public static List<Point> ToPointsMirrored(
        IReadOnlyList<double> values,
        double width,
        double centerY,
        double halfHeight,
        double max,
        bool plotBelow)
    {
        var points = new List<Point>(values.Count);
        if (values.Count == 0 || max <= 0) return points;

        double Y(double v)
        {
            var n = Math.Clamp(Math.Abs(v) / max, 0, 1);
            return plotBelow
                ? centerY + n * halfHeight
                : centerY - n * halfHeight;
        }

        if (values.Count == 1)
        {
            points.Add(new Point(0, Y(values[0])));
            return points;
        }

        var step = width / (values.Count - 1);
        for (var i = 0; i < values.Count; i++)
            points.Add(new Point(i * step, Y(values[i])));
        return points;
    }

    /// Stroke-only smooth path through <paramref name="points"/>.
    public static PathGeometry BuildStrokeGeometry(IReadOnlyList<Point> points)
    {
        var geo = new PathGeometry();
        if (points.Count == 0) return geo;

        var figure = new PathFigure
        {
            StartPoint = points[0],
            IsClosed = false,
            IsFilled = false,
        };

        if (points.Count == 1)
            figure.Segments.Add(new LineSegment { Point = points[0] });
        else if (points.Count == 2)
            figure.Segments.Add(new LineSegment { Point = points[1] });
        else
            AppendCatmullRom(figure, points);

        geo.Figures.Add(figure);
        return geo;
    }

    /// Closed fill under a smooth curve down to <paramref name="baselineY"/>.
    public static PathGeometry BuildFillGeometry(IReadOnlyList<Point> points, double baselineY)
    {
        var geo = new PathGeometry();
        if (points.Count < 2) return geo;

        var figure = new PathFigure
        {
            StartPoint = points[0],
            IsClosed = true,
            IsFilled = true,
        };

        if (points.Count == 2)
            figure.Segments.Add(new LineSegment { Point = points[1] });
        else
            AppendCatmullRom(figure, points);

        figure.Segments.Add(new LineSegment { Point = new Point(points[^1].X, baselineY) });
        figure.Segments.Add(new LineSegment { Point = new Point(points[0].X, baselineY) });
        geo.Figures.Add(figure);
        return geo;
    }

    /// Catmull–Rom through points → cubic Béziers.
    private static void AppendCatmullRom(PathFigure figure, IReadOnlyList<Point> pts)
    {
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var p0 = pts[Math.Max(i - 1, 0)];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = pts[Math.Min(i + 2, pts.Count - 1)];

            // Cubic Bezier control points from Catmull–Rom:
            //   c1 = p1 + (p2 - p0) / 6
            //   c2 = p2 - (p3 - p1) / 6
            var c1 = new Point(
                p1.X + (p2.X - p0.X) / 6.0,
                p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new Point(
                p2.X - (p3.X - p1.X) / 6.0,
                p2.Y - (p3.Y - p1.Y) / 6.0);

            c1 = ClampControl(c1, p1, p2);
            c2 = ClampControl(c2, p1, p2);

            figure.Segments.Add(new BezierSegment
            {
                Point1 = c1,
                Point2 = c2,
                Point3 = p2,
            });
        }
    }

    /// Limit vertical overshoot of control points relative to the segment endpoints.
    private static Point ClampControl(Point c, Point a, Point b)
    {
        var minY = Math.Min(a.Y, b.Y);
        var maxY = Math.Max(a.Y, b.Y);
        var span = Math.Max(8, maxY - minY);
        var lo = minY - span * 0.35;
        var hi = maxY + span * 0.35;
        return new Point(c.X, Math.Clamp(c.Y, lo, hi));
    }
}
