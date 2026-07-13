using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace OrchardWin.App.Controls;

/// Compact bar chart for tray history popovers (Orchard ResourceHistoryPanel).
public sealed partial class MiniBarChart : UserControl
{
    private IReadOnlyList<double> _values = Array.Empty<double>();
    private Color _color;

    public MiniBarChart()
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

    public void SetValues(IReadOnlyList<double> values, Color color)
    {
        _values = ChartPulse.EnsureDrawable(values);
        _color = color;
        Redraw();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        Redraw();
    }

    private void Redraw()
    {
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        if (w <= 1 || h <= 1) return;
        Plot.Children.Clear();
        if (_values.Count == 0) return;

        var max = _values.Max();
        if (max < 0.0001) max = 1;

        var n = Math.Min(_values.Count, 48);
        var step = Math.Max(1, _values.Count / n);
        var bars = new List<double>();
        for (var i = 0; i < _values.Count; i += step)
            bars.Add(_values[i]);
        if (bars.Count == 0) return;

        var gap = 2.0;
        var barW = Math.Max(2, (w - gap * (bars.Count - 1)) / bars.Count);
        for (var i = 0; i < bars.Count; i++)
        {
            var frac = Math.Clamp(bars[i] / max, 0.02, 1);
            var barH = frac * h;
            var rect = new Rectangle
            {
                Width = barW,
                Height = barH,
                Fill = new SolidColorBrush(_color),
                RadiusX = 2,
                RadiusY = 2,
            };
            Canvas.SetLeft(rect, i * (barW + gap));
            Canvas.SetTop(rect, h - barH);
            Plot.Children.Add(rect);
        }
    }
}
