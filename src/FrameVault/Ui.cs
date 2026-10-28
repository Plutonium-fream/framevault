using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;

namespace FrameVault;

internal static class Ui
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    public static TextBlock Text(string value, double size = 14, string color = "Text", FontWeight? weight = null) => new()
    {
        Text = value, FontSize = Math.Max(12, size), Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal,
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), TextWrapping = TextWrapping.Wrap, Opacity = 1
    };
    public static TextBlock Icon(string glyph, double size = 20, string color = "Text") => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = size,
        Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center
    };
    public static TextBlock Display(string value, double size = 32, string color = "Text")
    {
        var text = Text(value, size, color);
        text.FontFamily = (FontFamily)Application.Current.FindResource("EditorialFont");
        text.FontWeight = FontWeights.SemiBold;
        return text;
    }
    public static TextBlock Mono(string value, double size = 11, string color = "Muted")
    {
        var text = Text(value, Math.Max(11, size), color);
        text.FontSize = Math.Max(11, size);
        text.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        return text;
    }
    public static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label };
        if (primary) button.Style = (Style)Application.Current.FindResource("PrimaryButton");
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
    public static StackPanel Stack(double gap = 0) => new() { Margin = new Thickness(gap) };
    public static Border Card(UIElement child, double padding = 20) => new()
    {
        Background = Brush("Surface"), BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(padding), Child = child
    };
    public static void Label(StackPanel parent, string text, Control field)
    {
        var label = Text(text, 14, "Text", FontWeights.SemiBold);
        label.Margin = new Thickness(0, 14, 0, 7);
        parent.Children.Add(label);
        AutomationProperties.SetName(field, text);
        parent.Children.Add(field);
    }
}
