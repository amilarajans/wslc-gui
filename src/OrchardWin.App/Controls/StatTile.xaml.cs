using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OrchardWin.App.Controls;

public sealed partial class StatTile : UserControl
{
    public StatTile()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(StatTile), new PropertyMetadata("", (d, e) => ((StatTile)d).IconGlyph.Glyph = (string)e.NewValue));

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(StatTile), new PropertyMetadata("", (d, e) => ((StatTile)d).TitleTextBlock.Text = (string)e.NewValue));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(StatTile), new PropertyMetadata("--", (d, e) => ((StatTile)d).ValueTextBlock.Text = (string)e.NewValue));

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty DetailProperty = DependencyProperty.Register(
        nameof(Detail), typeof(string), typeof(StatTile), new PropertyMetadata("", (d, e) => ((StatTile)d).DetailTextBlock.Text = (string)e.NewValue));

    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    public static readonly DependencyProperty ValueColorProperty = DependencyProperty.Register(
        nameof(ValueColor), typeof(Color?), typeof(StatTile), new PropertyMetadata(null, OnValueColorChanged));

    public Color? ValueColor { get => (Color?)GetValue(ValueColorProperty); set => SetValue(ValueColorProperty, value); }

    private static void OnValueColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is Color color)
        {
            ((StatTile)d).ValueTextBlock.Foreground = new SolidColorBrush(color);
        }
    }
}
