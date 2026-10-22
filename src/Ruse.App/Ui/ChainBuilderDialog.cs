using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ruse.App.Audio;

namespace Ruse.App.Ui;

/// <summary>
/// Builds or edits a chain of effects. The result is saved in the chain
/// library and can then be dropped on the canvas as a single node.
/// </summary>
public static class ChainBuilderDialog
{
    /// <summary>
    /// Opens the builder. Returns the saved chain, or null when cancelled.
    /// </summary>
    public static SubgraphDefinition? Show(Window owner, SubgraphDefinition? existing)
    {
        var definition = existing != null
            ? new SubgraphDefinition
            {
                Name = existing.Name,
                Elements = existing.Elements
                    .Select(e => new SubgraphElement
                    {
                        TypeId = e.TypeId,
                        Parameters = new Dictionary<string, float>(e.Parameters),
                    })
                    .ToList(),
            }
            : new SubgraphDefinition { Name = "My chain" };

        var window = new Window
        {
            Title = existing != null ? "Edit chain" : "New chain",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#181B22")),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6EAF2")),
            MinWidth = 520,
        };

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = "Chain name",
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8892A6")),
        });

        var nameBox = new TextBox
        {
            Text = definition.Name,
            Margin = new Thickness(0, 4, 0, 12),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 14,
        };
        root.Children.Add(nameBox);

        root.Children.Add(new TextBlock
        {
            Text = "Effects, in order",
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8892A6")),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(list);

        // One row per effect: its name, its parameters, and move/remove buttons.
        var rows = new List<(SubgraphElement Element, Border Row)>();

        void Rebuild()
        {
            list.Children.Clear();
            rows.Clear();

            if (definition.Elements.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "No effects yet - add one below.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5A6478")),
                    Margin = new Thickness(0, 6, 0, 6),
                });
                return;
            }

            for (int index = 0; index < definition.Elements.Count; index++)
            {
                SubgraphElement element = definition.Elements[index];
                var row = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B1E27")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2A2F3D")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                };

                var content = new StackPanel();
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var descriptor = AudioProcessorCatalog.Find(element.TypeId);
                header.Children.Add(new TextBlock
                {
                    Text = $"{index + 1}. {descriptor?.DisplayName ?? element.TypeId}",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var up = TinyButton("↑");
                up.ToolTip = "Move up";
                up.Click += (_, _) =>
                {
                    if (index > 0)
                    {
                        (definition.Elements[index - 1], definition.Elements[index]) =
                            (definition.Elements[index], definition.Elements[index - 1]);
                        Rebuild();
                    }
                };
                Grid.SetColumn(up, 1);
                header.Children.Add(up);

                var down = TinyButton("↓");
                down.ToolTip = "Move down";
                down.Click += (_, _) =>
                {
                    if (index < definition.Elements.Count - 1)
                    {
                        (definition.Elements[index + 1], definition.Elements[index]) =
                            (definition.Elements[index], definition.Elements[index + 1]);
                        Rebuild();
                    }
                };
                Grid.SetColumn(down, 2);
                header.Children.Add(down);

                var remove = TinyButton("✕");
                remove.ToolTip = "Remove";
                remove.Click += (_, _) =>
                {
                    definition.Elements.RemoveAt(index);
                    Rebuild();
                };
                Grid.SetColumn(remove, 3);
                header.Children.Add(remove);

                content.Children.Add(header);

                // Parameters: the same widgets the modifier node uses, so a
                // choice is a drop-down and a switch is a checkbox in both
                // places.
                var live = AudioProcessorCatalog.Create(element.TypeId);
                if (live is IParameterizedProcessor parameterized)
                {
                    foreach (var parameter in parameterized.Parameters)
                    {
                        if (!element.Parameters.TryGetValue(parameter.Name, out float value))
                        {
                            value = parameter.Get();
                            element.Parameters[parameter.Name] = value;
                        }

                        var editor = ParameterEditor.Create(parameter, v =>
                        {
                            element.Parameters[parameter.Name] = v;
                        });

                        content.Children.Add(editor);
                    }
                }

                row.Child = content;
                list.Children.Add(row);
                rows.Add((element, row));
            }
        }

        Rebuild();

        // Add an effect.
        var addBox = new ComboBox
        {
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(6, 3, 6, 3),
            MinWidth = 200,
            FontSize = 12,
        };

        foreach (var descriptor in AudioProcessorCatalog.All)
        {
            addBox.Items.Add(descriptor.DisplayName);
        }

        if (addBox.Items.Count > 0)
        {
            addBox.SelectedIndex = 0;
        }

        var addButton = new Button { Content = "Add effect", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
        addButton.Click += (_, _) =>
        {
            if (addBox.SelectedIndex < 0)
            {
                return;
            }

            var descriptor = AudioProcessorCatalog.All[addBox.SelectedIndex];
            definition.Elements.Add(new SubgraphElement { TypeId = descriptor.TypeId });
            Rebuild();
        };

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        addRow.Children.Add(addBox);
        addRow.Children.Add(addButton);
        root.Children.Add(addRow);

        // Buttons.
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };

        SubgraphDefinition? result = null;

        var save = new Button { Content = "Save chain", Width = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        save.Click += (_, _) =>
        {
            definition.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? "My chain" : nameBox.Text.Trim();
            result = definition;
            window.DialogResult = true;
        };

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };

        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        window.Content = root;

        return window.ShowDialog() == true ? result : null;
    }

    private static Button TinyButton(string text) => new()
    {
        Content = text,
        Width = 26,
        Height = 22,
        Margin = new Thickness(4, 0, 0, 0),
        FontSize = 11,
        Padding = new Thickness(0),
    };
}
