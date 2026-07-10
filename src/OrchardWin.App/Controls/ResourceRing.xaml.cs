using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace OrchardWin.App.Controls;

/// One arc segment of a multi-segment resource ring.
public sealed class RingSegment
{
    public double Value { get; init; }
    public Color Color { get; init; }
}

/// Circular multi-segment gauge matching Orchard's menu-bar CPU/MEMORY rings.
public sealed partial class ResourceRing : UserControl
{
    private IReadOnlyList<RingSegment> _segments = Array.Empty<RingSegment>();
    private string _center = "0%";
    private string _title = "CPU";

    public ResourceRing()
    {
        InitializeComponent();
    }

    public void SetData(string title, string center, IReadOnlyList<RingSegment> segments)
    {
        _title = title;
        _center = center;
        _segments = segments ?? Array.Empty<RingSegment>();
        TitleLabel.Text = title;
        CenterLabel.Text = center;
        Redraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        Redraw();
    }

    private void Redraw()
    {
        var w = Plot.ActualWidth;
        var h = Plot.ActualHeight;
        if (w <= 1 || h <= 1)
        {
            w = Math.Max(Root.ActualWidth, 88);
            h = Math.Max(Root.ActualHeight - 22, 88);
        }
        if (w <= 1 || h <= 1) return;

        Plot.Children.Clear();
        var size = Math.Min(w, h);
        var cx = w / 2;
        var cy = h / 2;
        var thickness = Math.Clamp(size * 0.12, 8, 14);
        var radius = size / 2 - thickness / 2 - 2;

        // Track (background ring)
        Plot.Children.Add(ArcPath(cx, cy, radius, thickness, 0, 359.9,
            Color.FromArgb(40, 160, 160, 160)));

        var total = _segments.Sum(s => Math.Max(0, s.Value));
        if (total <= 0)
        {
            CenterLabel.Text = _center;
            return;
        }

        // Start at top (-90°)
        var angle = -90.0;
        foreach (var seg in _segments)
        {
            if (seg.Value <= 0) continue;
            var sweep = 360.0 * (seg.Value / total);
            // Leave tiny gaps between segments for the multi-color look.
            var draw = Math.Max(0.5, sweep - 1.2);
            Plot.Children.Add(ArcPath(cx, cy, radius, thickness, angle, draw, seg.Color));
            angle += sweep;
        }

        CenterLabel.Text = _center;
    }

    private static XamlPath ArcPath(double cx, double cy, double radius, double thickness,
        double startDeg, double sweepDeg, Color color)
    {
        if (sweepDeg >= 359.5)
        {
            // Full circle via Ellipse stroke
            return new XamlPath
            {
                // Use arc with almost-full sweep instead
                Data = FullCircleGeometry(cx, cy, radius),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
            };
        }

        var start = DegToPoint(cx, cy, radius, startDeg);
        var end = DegToPoint(cx, cy, radius, startDeg + sweepDeg);
        var large = sweepDeg > 180;

        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = large,
            SweepDirection = SweepDirection.Clockwise,
            RotationAngle = 0,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);

        return new XamlPath
        {
            Data = geo,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
        };
    }

    private static Geometry FullCircleGeometry(double cx, double cy, double radius)
    {
        var fig = new PathFigure
        {
            StartPoint = new Point(cx, cy - radius),
            IsClosed = false,
            IsFilled = false,
        };
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(cx, cy + radius),
            Size = new Size(radius, radius),
            IsLargeArc = true,
            SweepDirection = SweepDirection.Clockwise,
        });
        fig.Segments.Add(new ArcSegment
        {
            Point = new Point(cx, cy - radius),
            Size = new Size(radius, radius),
            IsLargeArc = true,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    private static Point DegToPoint(double cx, double cy, double r, double deg)
    {
        var rad = deg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
    }
}
