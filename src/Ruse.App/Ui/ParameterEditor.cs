using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ruse.App.Audio;

namespace Ruse.App.Ui;

/// <summary>
/// Builds the editing control for an effect parameter: a slider, a checkbox or
/// a drop-down, depending on what the parameter is.
/// </summary>
/// <remarks>
/// The modifier node and the chain builder both need this, and they must
/// agree on how a parameter is presented, so the widget lives in one place.
/// </remarks>
internal static class ParameterEditor
{
    private static readonly Brush LabelBrush =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8892A6"));

    /// <summary>
    /// Creates a panel with the parameter's label and control. The callback is
    /// invoked with the new value whenever the user changes it.
    /// </summary>
    public static FrameworkElement Create(AudioParameter parameter, Action<float> onChanged)
    {
        var panel = new StackPanel();

        if (parameter.IsToggle)
        {
            var check = new CheckBox
            {
                Content = parameter.Name,
                IsChecked = parameter.Get() >= 0.5f,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1E1")),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 6),
            };

            check.Checked += (_, _) => onChanged(1f);
            check.Unchecked += (_, _) => onChanged(0f);
            panel.Children.Add(check);
            return panel;
        }

        if (parameter.IsChoice && parameter.Options.Length > 0)
        {
            var label = new TextBlock
            {
                Text = $"{parameter.Name}: {CurrentOption(parameter)}",
                Foreground = LabelBrush,
                FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var combo = new ComboBox
            {
                Margin = new Thickness(0, 0, 0, 6),
                Height = 22,
                FontSize = 11,
                Padding = new Thickness(4, 1, 4, 1),
            };

            foreach (string option in parameter.Options)
            {
                combo.Items.Add(option);
            }

            int selected = (int)Math.Round(parameter.Get());
            combo.SelectedIndex = selected >= 0 && selected < parameter.Options.Length ? selected : -1;

            combo.SelectionChanged += (_, _) =>
            {
                int index = combo.SelectedIndex;
                if (index < 0)
                {
                    return;
                }

                label.Text = $"{parameter.Name}: {parameter.Options[index]}";
                onChanged(index);
            };

            panel.Children.Add(label);
            panel.Children.Add(combo);
            return panel;
        }

        bool logarithmic = parameter.Logarithmic && parameter.Min > 0 && parameter.Max > parameter.Min;

        var sliderLabel = new TextBlock
        {
            Text = Format(parameter, parameter.Get()),
            Foreground = LabelBrush,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var slider = new Slider
        {
            Minimum = logarithmic ? Math.Log10(parameter.Min) : parameter.Min,
            Maximum = logarithmic ? Math.Log10(parameter.Max) : parameter.Max,
            Value = logarithmic
                ? Math.Log10(Math.Clamp(parameter.Get(), parameter.Min, parameter.Max))
                : Math.Clamp(parameter.Get(), parameter.Min, parameter.Max),
            Height = 18,
            Margin = new Thickness(0, 0, 0, 4),
        };

        // Whole-number parameters step in units so the value the effect holds
        // is the value shown.
        if (parameter.IsInteger)
        {
            slider.TickFrequency = 1;
            slider.IsSnapToTickEnabled = true;
        }

        slider.ValueChanged += (_, _) =>
        {
            float value = logarithmic ? (float)Math.Pow(10, slider.Value) : (float)slider.Value;
            if (parameter.IsInteger)
            {
                value = MathF.Round(value);
            }

            sliderLabel.Text = Format(parameter, value);
            onChanged(value);
        };

        panel.Children.Add(sliderLabel);
        panel.Children.Add(slider);
        return panel;
    }

    private static string CurrentOption(AudioParameter parameter)
    {
        int index = (int)Math.Round(parameter.Get());
        return index >= 0 && index < parameter.Options.Length ? parameter.Options[index] : string.Empty;
    }

    private static string Format(AudioParameter parameter, float value) =>
        string.IsNullOrEmpty(parameter.Unit)
            ? $"{parameter.Name}: {value:0.##}"
            : $"{parameter.Name}: {value:0.#} {parameter.Unit}";
}
