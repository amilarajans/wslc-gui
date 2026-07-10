using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OrchardWin.App.Controls;

/// Code-behind for <see cref="ListItemRow"/>. Every visual property is a DependencyProperty
/// so XAML bindings (`<controls:ListItemRow PrimaryText="{x:Bind ...}" />`) work directly
/// from a page's ItemTemplate without a converter.
public sealed partial class ListItemRow : UserControl
{
    public ListItemRow()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(ListItemRow), new PropertyMetadata("", OnIconChanged));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty IconColorProperty = DependencyProperty.Register(
        nameof(IconColor), typeof(Color), typeof(ListItemRow), new PropertyMetadata(Colors.Gray, OnIconColorChanged));

    public Color IconColor
    {
        get => (Color)GetValue(IconColorProperty);
        set => SetValue(IconColorProperty, value);
    }

    public static readonly DependencyProperty PrimaryTextProperty = DependencyProperty.Register(
        nameof(PrimaryText), typeof(string), typeof(ListItemRow), new PropertyMetadata("", OnPrimaryTextChanged));

    public string PrimaryText
    {
        get => (string)GetValue(PrimaryTextProperty);
        set => SetValue(PrimaryTextProperty, value);
    }

    public static readonly DependencyProperty SecondaryLeftTextProperty = DependencyProperty.Register(
        nameof(SecondaryLeftText), typeof(string), typeof(ListItemRow), new PropertyMetadata(null, OnSecondaryLeftTextChanged));

    public string? SecondaryLeftText
    {
        get => (string?)GetValue(SecondaryLeftTextProperty);
        set => SetValue(SecondaryLeftTextProperty, value);
    }

    public static readonly DependencyProperty SecondaryRightTextProperty = DependencyProperty.Register(
        nameof(SecondaryRightText), typeof(string), typeof(ListItemRow), new PropertyMetadata(null, OnSecondaryRightTextChanged));

    public string? SecondaryRightText
    {
        get => (string?)GetValue(SecondaryRightTextProperty);
        set => SetValue(SecondaryRightTextProperty, value);
    }

    public static readonly DependencyProperty ShowSandboxBadgeProperty = DependencyProperty.Register(
        nameof(ShowSandboxBadge), typeof(bool), typeof(ListItemRow), new PropertyMetadata(false, OnShowSandboxBadgeChanged));

    public bool ShowSandboxBadge
    {
        get => (bool)GetValue(ShowSandboxBadgeProperty);
        set => SetValue(ShowSandboxBadgeProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ListItemRow)d).IconGlyph.Glyph = (string)e.NewValue;

    private static void OnIconColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ListItemRow)d).IconGlyph.Foreground = new SolidColorBrush((Color)e.NewValue);

    private static void OnPrimaryTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ListItemRow)d).PrimaryTextBlock.Text = (string)e.NewValue;

    private static void OnSecondaryLeftTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ListItemRow)d;
        var text = e.NewValue as string;
        self.SecondaryLeftTextBlock.Text = text ?? "";
        self.SecondaryLeftTextBlock.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnSecondaryRightTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ListItemRow)d;
        var text = e.NewValue as string;
        self.SecondaryRightTextBlock.Text = text ?? "";
        self.SecondaryRightTextBlock.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnShowSandboxBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ListItemRow)d).SandboxBadge.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
}
