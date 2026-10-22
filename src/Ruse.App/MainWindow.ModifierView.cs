/// <summary>
/// ModifierView part of the main window. The shared state lives in MainWindow.xaml.cs;
/// this file only holds the code for that area.
/// </summary>
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;
using Ruse.App.Ui;

namespace Ruse.App;

public partial class MainWindow
{
    private void BuildModifierVisual(NodeViewModel vm, ModifierNode modifier)
    {
        var accent = (Color)ColorConverter.ConvertFromString(vm.Accent);

        var headerText = new TextBlock
        {
            Foreground = Brushes.Black,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        headerText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(NodeViewModel.Title))
        {
            Source = vm,
            Mode = System.Windows.Data.BindingMode.OneWay,
        });

        var header = new Border
        {
            Background = new SolidColorBrush(accent),
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = Cursors.Hand,
            ToolTip = "Double-click to rename",
            Child = headerText,
        };
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                // A chain node opens its builder instead of the rename box.
                if (modifier.Processors.OfType<ChainProcessor>().Any())
                {
                    EditChainNode(modifier);
                }
                else
                {
                    RenameNode(vm);
                }

                e.Handled = true;
            }
        };

        var subtitle = new TextBlock
        {
            Text = vm.Subtitle,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
            FontSize = 11,
            Margin = new Thickness(12, 6, 12, 4),
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(subtitle);

        // A chain node shows the effects it contains as chips, the way a
        // collapsed group looks.
        var chain = modifier.Processors.OfType<ChainProcessor>().FirstOrDefault();
        if (chain != null)
        {
            var chips = new WrapPanel { Margin = new Thickness(12, 2, 12, 6) };
            foreach (var inner in chain.Processors)
            {
                var chip = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3A2E14")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5A4A22")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                chip.Child = new TextBlock
                {
                    Text = AudioProcessorCatalog.Find(inner.TypeId)?.DisplayName ?? inner.TypeId,
                    FontSize = 11,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0A63A")),
                };
                chips.Children.Add(chip);
            }

            stack.Children.Add(chips);
        }

        // A generic slider for every parameter the modifier's processors expose.
        foreach (var processor in modifier.Processors)
        {
            if (processor is not IParameterizedProcessor parameterized)
            {
                continue;
            }

            foreach (var parameter in parameterized.Parameters)
            {
                if (parameter.IsToggle)
                {
                    var check = new CheckBox
                    {
                        Content = parameter.Name,
                        IsChecked = parameter.Get() >= 0.5f,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1E1")),
                        FontSize = 11,
                        Margin = new Thickness(PortLabelGutter, 2, PortLabelGutter, 6),
                    };
                    check.Checked += (_, _) => parameter.Set(1f);
                    check.Unchecked += (_, _) => parameter.Set(0f);
                    stack.Children.Add(check);
                    continue;
                }

                if (parameter.IsChoice)
                {
                    // A choice parameter shows its options in a drop-down; the
                    // value is the index of the selected one.
                    var choiceLabel = new TextBlock
                    {
                        Text = parameter.Name,
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
                        FontSize = 10,
                        Margin = new Thickness(PortLabelGutter, 2, PortLabelGutter, 0),
                    };

                    var combo = new ComboBox
                    {
                        Margin = new Thickness(PortLabelGutter, 0, PortLabelGutter, 6),
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

                    float lastChoice = parameter.Get();
                    combo.SelectionChanged += (_, _) =>
                    {
                        int index = combo.SelectedIndex;
                        if (index < 0)
                        {
                            return;
                        }

                        parameter.Set(index);
                        choiceLabel.Text = $"{parameter.Name}: {parameter.Options[index]}";
                        PushParameter(vm, parameter.Name, lastChoice, index);
                        lastChoice = index;
                    };

                    stack.Children.Add(choiceLabel);
                    stack.Children.Add(combo);
                    continue;
                }

                bool logarithmic = parameter.Logarithmic && parameter.Min > 0 && parameter.Max > parameter.Min;

                string Format(float value) =>
                    string.IsNullOrEmpty(parameter.Unit)
                        ? $"{parameter.Name}: {value:0.##}"
                        : $"{parameter.Name}: {value:0.#} {parameter.Unit}";

                var label = new TextBlock
                {
                    Text = Format(parameter.Get()),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
                    FontSize = 10,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(PortLabelGutter, 2, PortLabelGutter, 0),
                };

                var slider = new Slider
                {
                    Minimum = logarithmic ? Math.Log10(parameter.Min) : parameter.Min,
                    Maximum = logarithmic ? Math.Log10(parameter.Max) : parameter.Max,
                    Value = logarithmic
                        ? Math.Log10(Math.Clamp(parameter.Get(), parameter.Min, parameter.Max))
                        : Math.Clamp(parameter.Get(), parameter.Min, parameter.Max),
                    Margin = new Thickness(PortLabelGutter, 0, PortLabelGutter, 6),
                    Height = 20,
                };

                // Whole-number parameters step in units, so the slider snaps and
                // the label shows the value the effect really holds.
                if (parameter.IsInteger)
                {
                    slider.TickFrequency = 1;
                    slider.IsSnapToTickEnabled = true;
                }

                float lastParam = parameter.Get();
                slider.ValueChanged += (_, _) =>
                {
                    float value = logarithmic ? (float)Math.Pow(10, slider.Value) : (float)slider.Value;
                    if (parameter.IsInteger)
                    {
                        value = MathF.Round(value);
                    }

                    parameter.Set(value);
                    label.Text = Format(parameter.Get());
                    PushParameter(vm, parameter.Name, lastParam, value);
                    lastParam = value;
                };

                stack.Children.Add(label);
                stack.Children.Add(slider);
            }
        }

        var removeButton = new Button
        {
            Content = "Remove",
            Margin = new Thickness(12, 0, 12, 10),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 11,
        };
        removeButton.Click += (_, _) => RemoveNode(vm);
        stack.Children.Add(removeButton);

        var nodeMenu = new ContextMenu();
        var menuDisconnect = new MenuItem { Header = "Disconnect all routes" };
        menuDisconnect.Click += (_, _) => DisconnectNode(vm);
        var menuRemove = new MenuItem { Header = "Remove node" };
        menuRemove.Click += (_, _) => RemoveNode(vm);
        nodeMenu.Items.Add(menuDisconnect);
        nodeMenu.Items.Add(menuRemove);

        var body = new Border
        {
            Width = vm.Width,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#221E16")),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4A3D22")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = stack,
            Cursor = Cursors.SizeAll,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = Colors.Black,
            },
            ContextMenu = nodeMenu,
        };

        // Output connector (right) starts a drag; input connector (left) is the drop side.
        var outConnector = new Ellipse
        {
            Width = 16,
            Height = 16,
            Fill = new SolidColorBrush(accent),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            Cursor = Cursors.Cross,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -8, 0),
            ToolTip = "Drag to an output or another modifier",
        };
        outConnector.Tag = vm;
        outConnector.MouseLeftButtonDown += OnSourceConnectorDown;

        var inConnector = new Ellipse
        {
            Width = 16,
            Height = 16,
            Fill = new SolidColorBrush(accent),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-8, 0, 0, 0),
            ToolTip = "Drop a source or modifier here",
        };

        var outLabel = new TextBlock
        {
            Text = "OUT",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false,
        };

        var inLabel = new TextBlock
        {
            Text = "IN",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A93A6")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };

        var host = new Grid();
        host.Children.Add(body);
        host.Children.Add(inLabel);
        host.Children.Add(outLabel);
        host.Children.Add(inConnector);
        host.Children.Add(outConnector);

        Canvas.SetLeft(host, vm.X);
        Canvas.SetTop(host, vm.Y);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(NodeViewModel.X))
            {
                Canvas.SetLeft(host, vm.X);
                UpdateConnections();
                UpdateGraphExtent();
            }
            else if (args.PropertyName == nameof(NodeViewModel.Y))
            {
                Canvas.SetTop(host, vm.Y);
                UpdateConnections();
                UpdateGraphExtent();
            }
        };

        bool dragging = false;
        double dragStartX = 0;
        double dragStartY = 0;
        Point dragOffset = default;
        body.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
                dragStartX = vm.X;
                dragStartY = vm.Y;
            _selected = vm;
            dragOffset = e.GetPosition(NodeCanvas);
            dragOffset.X -= vm.X;
            dragOffset.Y -= vm.Y;
            body.CaptureMouse();
            e.Handled = false;
        };
        body.MouseMove += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            Point p = e.GetPosition(NodeCanvas);
            vm.X = p.X - dragOffset.X;
            vm.Y = p.Y - dragOffset.Y;
        };
        body.MouseLeftButtonUp += (_, _) =>
        {
            if (dragging)
            {
                double newX = SnapToGrid(vm.X);
                double newY = SnapToGrid(vm.Y);
                vm.X = newX;
                vm.Y = newY;
                PushMoveNode(vm, dragStartX, dragStartY, newX, newY);
            }

            dragging = false;
            body.ReleaseMouseCapture();
        };

        _nodeVisuals[vm] = body;
        _nodeHosts[vm] = host;

        // A modifier is both a connection source (out) and a drop target (in).
        var outPort = new NodePortViewModel
        {
            Name = "out",
            IsOutput = true,
            Owner = vm,
            Element = outConnector,
            Anchor = new Point(vm.PortX, vm.PortY),
        };
        var inPort = new NodePortViewModel
        {
            Name = "in",
            IsOutput = false,
            Owner = vm,
            Element = inConnector,
            Anchor = new Point(vm.X, vm.PortY),
        };
        _sourcePorts[vm] = outPort;
        _outputPorts[vm] = inPort;

        NodeCanvas.Children.Add(host);
        AttachPortAnchor(outPort, host);
        AttachPortAnchor(inPort, host);
        UpdateEmptyHint();
        UpdateGraphExtent();
    }
}
