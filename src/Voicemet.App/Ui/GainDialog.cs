using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Voicemet.App.Ui;

/// <summary>
/// A minimal modal prompt for editing a route's gain (in percent).
/// Built in code to avoid an extra XAML view.
/// </summary>
public static class GainDialog
{
    public static float? Show(Window owner, string title, float currentGain)
    {
        var window = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#181B22")),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6EAF2")),
            MinWidth = 300,
        };

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = "Gain (%)",
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8892A6")),
        });

        var input = new TextBox
        {
            Text = ((int)Math.Round(currentGain * 100)).ToString(),
            Padding = new Thickness(6),
            FontSize = 14,
        };
        panel.Children.Add(input);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 200,
            Value = Math.Clamp(currentGain * 100, 0, 200),
            Margin = new Thickness(0, 10, 0, 0),
            TickFrequency = 10,
            IsSnapToTickEnabled = true,
        };
        slider.ValueChanged += (_, _) => input.Text = ((int)slider.Value).ToString();
        input.TextChanged += (_, _) =>
        {
            if (float.TryParse(input.Text, out var v))
            {
                slider.Value = Math.Clamp(v, 0, 200);
            }
        };
        panel.Children.Add(slider);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        float? result = null;

        var ok = new Button { Content = "Apply", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (float.TryParse(input.Text, out var v))
            {
                result = Math.Clamp(v, 0, 200) / 100f;
                window.DialogResult = true;
            }
        };

        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;
        window.Loaded += (_, _) => { input.SelectAll(); input.Focus(); };

        return window.ShowDialog() == true ? result : null;
    }
}
