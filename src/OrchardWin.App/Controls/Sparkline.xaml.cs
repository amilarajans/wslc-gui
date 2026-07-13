using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OrchardWin.App.Controls;

public sealed partial class Sparkline : UserControl
{
    public Sparkline()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ChartPulse.EnsureStartedOnUiThread();
            ChartPulse.Subscribe(OnPulse);
            Redraw();
        };
        Unloaded += (_, _) => ChartPulse.Unsubscribe(OnPulse);
    }

    private void OnPulse() => Redraw();

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
        new PropertyMetadata(Colors.DodgerBlue, (d, _) => ((Sparkline)d).Redraw()));

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
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        Redraw();
    }

    private void Redraw()
    {
        var values = ChartPulse.EnsureDrawable(Values);
        var width = RootGrid.ActualWidth;
        var height = RootGrid.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var softened = ChartPathBuilder.SoftenSeries(values, 0.5);
        var max = MaxValue > 0 ? MaxValue : 0.0001;
        if (MaxValue <= 0)
        {
            foreach (var v in softened)
                if (v > max) max = v;
            if (max < 0.0001) max = 1;
        }

        var points = ChartPathBuilder.ToPoints(softened, width, height, max, padTop: 1, padBottom: 1);
        var geo = ChartPathBuilder.BuildStrokeGeometry(points);

        Glow.Data = geo;
        Glow.Stroke = new SolidColorBrush(Color.FromArgb(50, StrokeColor.R, StrokeColor.G, StrokeColor.B));
        Glow.StrokeThickness = 3.2;
        Glow.StrokeLineJoin = PenLineJoin.Round;
        Glow.StrokeStartLineCap = PenLineCap.Round;
        Glow.StrokeEndLineCap = PenLineCap.Round;

        Line.Data = geo;
        Line.Stroke = new SolidColorBrush(StrokeColor);
        Line.StrokeThickness = 1.6;
        Line.StrokeLineJoin = PenLineJoin.Round;
        Line.StrokeStartLineCap = PenLineCap.Round;
        Line.StrokeEndLineCap = PenLineCap.Round;
    }
}
