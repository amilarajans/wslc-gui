using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace OrchardWin.App.Controls;

/// One series plotted on <see cref="MetricChart"/>.
public sealed class ChartSeries
{
    public required IReadOnlyList<double> Values { get; init; }
    public Color Stroke { get; init; } = Colors.DodgerBlue;
    public double Thickness { get; init; } = 1.6;
    public bool Fill { get; init; }
}

/// Lightweight multi-series line chart (no external charting package) matching Orchard's
/// system dashboard graphs: auto-scaled Y, optional fixed max, horizontal grid, dual series.
public sealed partial class MetricChart : UserControl
{
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private double? _fixedMax;
    private double? _guideValue; // e.g. memory limit line
    private Color _guideColor = Color.FromArgb(120, 128, 128, 128);

    public MetricChart()
    {
        InitializeComponent();
    }

    public void SetSeries(
        IReadOnlyList<ChartSeries> series,
        double? fixedMax = null,
        double? guideValue = null,
        Color? guideColor = null)
    {
        _series = series ?? Array.Empty<ChartSeries>();
        _fixedMax = fixedMax;
        _guideValue = guideValue;
        if (guideColor is { } c) _guideColor = c;
        Redraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        Redraw();
    }

    private void Redraw()
    {
        var width = PlotCanvas.ActualWidth;
        var height = PlotCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            width = RootGrid.ActualWidth - 42;
            height = RootGrid.ActualHeight;
        }
        if (width <= 1 || height <= 1) return;

        PlotCanvas.Children.Clear();
        YAxisLabels.Children.Clear();

        var max = _fixedMax ?? 0.0001;
        if (_fixedMax is null)
        {
            foreach (var s in _series)
            {
                foreach (var v in s.Values)
                    if (v > max) max = v;
            }
            if (_guideValue is { } g && g > max) max = g;
            // Headroom so peaks aren't clipped.
            max *= 1.08;
            if (max < 0.0001) max = 1;
        }

        // Horizontal grid + Y labels (4 ticks).
        for (var i = 0; i <= 3; i++)
        {
            var t = i / 3.0;
            var y = height - t * height;
            var value = max * t;
            PlotCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                StrokeThickness = 1,
            });
            YAxisLabels.Children.Add(new TextBlock
            {
                Text = FormatAxis(value, max),
                FontSize = 10,
                Opacity = 0.45,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, i == 3 ? 0 : height / 3 - 12, 0, 0),
            });
        }

        if (_guideValue is { } guide && guide > 0 && guide <= max)
        {
            var gy = height - (guide / max) * height;
            PlotCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = gy,
                Y2 = gy,
                Stroke = new SolidColorBrush(_guideColor),
                StrokeThickness = 1,
                StrokeDashArray = [4, 3],
            });
        }

        foreach (var series in _series)
        {
            if (series.Values.Count < 2) continue;
            var points = new PointCollection();
            var step = width / Math.Max(1, series.Values.Count - 1);
            for (var i = 0; i < series.Values.Count; i++)
            {
                var n = Math.Clamp(series.Values[i] / max, 0, 1);
                points.Add(new Point(i * step, height - n * height));
            }

            if (series.Fill && points.Count >= 2)
            {
                var fillPoints = new PointCollection();
                foreach (var p in points) fillPoints.Add(p);
                fillPoints.Add(new Point(points[^1].X, height));
                fillPoints.Add(new Point(points[0].X, height));
                PlotCanvas.Children.Add(new Polygon
                {
                    Points = fillPoints,
                    Fill = new SolidColorBrush(Color.FromArgb(55, series.Stroke.R, series.Stroke.G, series.Stroke.B)),
                    StrokeThickness = 0,
                });
            }

            PlotCanvas.Children.Add(new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(series.Stroke),
                StrokeThickness = series.Thickness,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
    }

    private static string FormatAxis(double value, double max)
    {
        if (max >= 1_000_000) return $"{value / 1_000_000:0.#}M";
        if (max >= 1000) return $"{value / 1000:0.#}k";
        if (max >= 10) return $"{value:0}";
        if (max >= 1) return $"{value:0.0}";
        return $"{value:0.00}";
    }
}
