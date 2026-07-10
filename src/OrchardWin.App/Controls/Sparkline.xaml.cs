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

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        var values = Values;
        var width = RootGrid.ActualWidth;
        var height = RootGrid.ActualHeight;
        if (values.Count < 2 || width <= 0 || height <= 0)
        {
            Line.Points.Clear();
            return;
        }

        var max = Math.Max(values.Max(), 0.0001);
        var points = new PointCollection();
        var stepX = width / (values.Count - 1);
        for (var i = 0; i < values.Count; i++)
        {
            var normalized = Math.Clamp(values[i] / max, 0, 1);
            var x = i * stepX;
            var y = height - normalized * height;
            points.Add(new Point(x, y));
        }
        Line.Points = points;
    }
}
