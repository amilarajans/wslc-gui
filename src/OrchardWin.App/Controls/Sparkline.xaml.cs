using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace OrchardWin.App.Controls;

public sealed partial class Sparkline : UserControl
{
    public Sparkline()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new PropertyMetadata(Array.Empty<double>(), (d, _) => ((Sparkline)d).Redraw()));

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public static readonly DependencyProperty StrokeColorProperty = DependencyProperty.Register(
        nameof(StrokeColor), typeof(Color), typeof(Sparkline),
        new PropertyMetadata(Colors.DodgerBlue, (d, e) => ((Sparkline)d).Line.Stroke = new SolidColorBrush((Color)e.NewValue)));

    public Color StrokeColor
    {
        get => (Color)GetValue(StrokeColorProperty);
        set => SetValue(StrokeColorProperty, value);
    }

    /// When &gt; 0, Y axis is fixed to this max (e.g. 100 for CPU%). Otherwise auto-scales.
    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(Sparkline),
        new PropertyMetadata(0.0, (d, _) => ((Sparkline)d).Redraw()));

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Ignore zero-size intermediate layout passes that would clear the polyline and flash.
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        Redraw();
    }

    private void Redraw()
    {
        var values = Values;
        var width = RootGrid.ActualWidth;
        var height = RootGrid.ActualHeight;
        if (values is null || values.Count < 2 || width <= 0 || height <= 0)
        {
            // Keep previous geometry when layout is momentarily zero-sized (avoids flicker).
            if (width <= 0 || height <= 0) return;
            Line.Points = [];
            return;
        }

        var max = MaxValue > 0 ? MaxValue : 0.0001;
        if (MaxValue <= 0)
        {
            for (var i = 0; i < values.Count; i++)
            {
                if (values[i] > max) max = values[i];
            }
        }

        var points = new PointCollection();
        var stepX = width / (values.Count - 1);
        for (var i = 0; i < values.Count; i++)
        {
            var normalized = Math.Clamp(values[i] / max, 0, 1);
            points.Add(new Point(i * stepX, height - normalized * height));
        }
        Line.Points = points;
        Line.Stroke = new SolidColorBrush(StrokeColor);
    }
}
