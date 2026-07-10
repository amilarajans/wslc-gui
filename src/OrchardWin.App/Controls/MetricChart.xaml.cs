using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace OrchardWin.App.Controls;

/// One series plotted on <see cref="MetricChart"/>.
public sealed class ChartSeries
{
    public required IReadOnlyList<double> Values { get; init; }
    public Color Stroke { get; init; } = Colors.DodgerBlue;
    public double Thickness { get; init; } = 1.9;
    public bool Fill { get; init; }

    /// 0 = raw samples, 1 = full 1-2-1 soften. Default softens spikes without washing trends.
    public double Soften { get; init; } = 0.55;

    /// Mirrored charts only: plot this series below the center baseline (e.g. downloads / write).
    /// Magnitude values stay non-negative; polarity is applied at draw time.
    public bool PlotBelow { get; init; }
}

/// Multi-series metric chart: auto-scaled Y, optional fixed max / guide line, grid.
/// Curves are Catmull–Rom → cubic Bézier (smooth), not jagged polylines.
/// When <c>mirrored</c>, series plot above/below a center zero baseline (Network / Disk I/O).
public sealed partial class MetricChart : UserControl
{
    private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    private double? _fixedMax;
    private double? _guideValue;
    private Color _guideColor = Color.FromArgb(120, 128, 128, 128);
    private bool _mirrored;

    public MetricChart()
    {
        InitializeComponent();
    }

    public void SetSeries(
        IReadOnlyList<ChartSeries> series,
        double? fixedMax = null,
        double? guideValue = null,
        Color? guideColor = null,
        bool mirrored = false)
    {
        _series = series ?? Array.Empty<ChartSeries>();
        _fixedMax = fixedMax;
        _guideValue = guideValue;
        _mirrored = mirrored;
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

        // Soften before computing axis max so scale matches what we draw.
        var prepared = new List<(ChartSeries Series, IReadOnlyList<double> Values)>(_series.Count);
        foreach (var s in _series)
        {
            var vals = s.Values.Count == 0
                ? s.Values
                : ChartPathBuilder.SoftenSeries(s.Values, s.Soften);
            prepared.Add((s, vals));
        }

        var max = _fixedMax ?? 0.0001;
        if (_fixedMax is null)
        {
            foreach (var (_, vals) in prepared)
            {
                foreach (var v in vals)
                {
                    var mag = Math.Abs(v);
                    if (mag > max) max = mag;
                }
            }
            if (_guideValue is { } g && Math.Abs(g) > max) max = Math.Abs(g);
            max *= 1.08;
            if (max < 0.0001) max = 1;
        }

        if (_mirrored)
            DrawMirrored(prepared, width, height, max);
        else
            DrawStandard(prepared, width, height, max);
    }

    private void DrawStandard(
        List<(ChartSeries Series, IReadOnlyList<double> Values)> prepared,
        double width,
        double height,
        double max)
    {
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
                Stroke = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
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

        foreach (var (series, values) in prepared)
        {
            if (values.Count < 2) continue;
            var points = ChartPathBuilder.ToPoints(values, width, height, max);
            DrawSeries(series, points, baselineY: height);
        }
    }

    /// Center baseline: series with <see cref="ChartSeries.PlotBelow"/> go down; others go up.
    private void DrawMirrored(
        List<(ChartSeries Series, IReadOnlyList<double> Values)> prepared,
        double width,
        double height,
        double max)
    {
        var centerY = height / 2.0;
        var halfH = Math.Max(1, height / 2.0 - 2);

        // Grid: top, mid (emphasized), bottom + one intermediate each side.
        for (var i = 0; i <= 4; i++)
        {
            var t = i / 4.0; // 0 bottom … 1 top
            var y = height - t * height;
            var isCenter = i == 2;
            PlotCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(
                    (byte)(isCenter ? 90 : 36), 255, 255, 255)),
                StrokeThickness = isCenter ? 1.25 : 1,
            });
        }

        // Y labels: +max (top), mid, +max (bottom) — magnitude only, like Orchard mirrored.
        void Label(string text, double topMargin)
        {
            YAxisLabels.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 10,
                Opacity = 0.45,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, topMargin, 0, 0),
            });
        }
        Label(FormatAxis(max, max), 0);
        Label(FormatAxis(max / 2, max), height / 4 - 10);
        Label("0", height / 2 - 10);
        Label(FormatAxis(max / 2, max), height * 0.75 - 10);
        Label(FormatAxis(max, max), height - 14);

        foreach (var (series, values) in prepared)
        {
            if (values.Count < 2) continue;
            var points = ChartPathBuilder.ToPointsMirrored(
                values, width, centerY, halfH, max, plotBelow: series.PlotBelow);
            DrawSeries(series, points, baselineY: centerY);
        }
    }

    private void DrawSeries(ChartSeries series, IReadOnlyList<Windows.Foundation.Point> points, double baselineY)
    {
        if (points.Count < 2) return;
        // WinUI Path.Data rejects NaN/Infinity coordinates with ArgumentException.
        foreach (var p in points)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y))
                return;
        }
        if (double.IsNaN(baselineY) || double.IsInfinity(baselineY))
            return;

        Geometry? strokeGeo;
        Geometry? fillGeo = null;
        try
        {
            strokeGeo = ChartPathBuilder.BuildStrokeGeometry(points);
            if (series.Fill)
                fillGeo = ChartPathBuilder.BuildFillGeometry(points, baselineY);
        }
        catch
        {
            return;
        }

        try
        {
            if (fillGeo is not null)
            {
                PlotCanvas.Children.Add(new XamlPath
                {
                    Data = fillGeo,
                    Fill = new SolidColorBrush(Color.FromArgb(48, series.Stroke.R, series.Stroke.G, series.Stroke.B)),
                    StrokeThickness = 0,
                });
            }

            // Soft under-glow, then crisp stroke on top.
            PlotCanvas.Children.Add(new XamlPath
            {
                Data = strokeGeo,
                Stroke = new SolidColorBrush(Color.FromArgb(55, series.Stroke.R, series.Stroke.G, series.Stroke.B)),
                StrokeThickness = series.Thickness + 2.4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });

            PlotCanvas.Children.Add(new XamlPath
            {
                Data = strokeGeo,
                Stroke = new SolidColorBrush(series.Stroke),
                StrokeThickness = series.Thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }
        catch (ArgumentException)
        {
            // Geometry rejected by WinRT — skip this series for this frame.
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
