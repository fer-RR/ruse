using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ruse.App.Ui;

/// <summary>Minimal modal single-line text prompt, built in code.</summary>
public static class InputDialog
{
    public static string? Show(Window owner, string title, string label, string initial)
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
            MinWidth = 320,
        };

        var panel = new StackPanel { Margin = new Thickness(20) };

        if (!string.IsNullOrEmpty(label))
        {
            panel.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8892A6")),
            });
        }

        var input = new TextBox
        {
            Text = initial,
            Padding = new Thickness(6),
            FontSize = 14,
        };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        string? result = null;

        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            result = input.Text;
            window.DialogResult = true;
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
