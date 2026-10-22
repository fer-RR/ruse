using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ruse.App.Ui;

/// <summary>One keyboard shortcut, as shown in the help dialog.</summary>
public sealed record ShortcutEntry(string Keys, string Description, string Group);

/// <summary>
/// The list of keyboard shortcuts. This is the single place that defines them,
/// so the dialog and the actual key handling cannot drift apart.
/// </summary>
public static class Shortcuts
{
    public static IReadOnlyList<ShortcutEntry> All { get; } = new List<ShortcutEntry>
    {
        // Editing
        new("Ctrl+Z", "Undo the last change", "Editing"),
        new("Ctrl+Y  /  Ctrl+Shift+Z", "Redo", "Editing"),
        new("Delete", "Remove the selected node", "Editing"),
        new("M", "Mute or unmute the selected node", "Editing"),
        new("Ctrl+D", "Duplicate the selected node", "Editing"),
        new("Esc", "Cancel a cable you are dragging", "Editing"),

        // Files
        new("Ctrl+S", "Save the routing as a preset", "Files"),
        new("Ctrl+O", "Load a preset", "Files"),
        new("Ctrl+F", "Search the sidebar", "Files"),

        // Canvas
        new("Mouse wheel", "Zoom in and out", "Canvas"),
        new("+  /  -", "Zoom in / out", "Canvas"),
        new("0", "Reset the zoom to 100%", "Canvas"),
        new("F", "Fit the whole graph in the view", "Canvas"),
        new("Middle-drag", "Pan the canvas", "Canvas"),
        new("Right-click", "Add a node at the pointer", "Canvas"),
        new("Double-click a node", "Rename it", "Canvas"),
        new("Click a cable", "Disconnect it", "Canvas"),

        // Sidebar
        new("Ctrl+1 … Ctrl+5", "Collapse or expand a sidebar section", "Sidebar"),
        new("Double-click an item", "Add it to the canvas", "Sidebar"),
    };

    public static IReadOnlyList<string> Groups { get; } =
        All.Select(s => s.Group).Distinct().ToList();
}

/// <summary>
/// The keyboard shortcuts window. Opens with F1 or from the toolbar.
/// </summary>
public static class ShortcutDialog
{
    public static void Show(Window owner)
    {
        var window = new Window
        {
            Title = "Keyboard shortcuts",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#181B22")),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6EAF2")),
            MinWidth = 430,
        };

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = "Keyboard shortcuts",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });

        root.Children.Add(new TextBlock
        {
            Text = "Shortcuts work everywhere except while you are typing in a text field.",
            FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8892A6")),
            Margin = new Thickness(0, 0, 0, 14),
        });

        foreach (string group in Shortcuts.Groups)
        {
            root.Children.Add(new TextBlock
            {
                Text = group.ToUpperInvariant(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 4),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6E7A90")),
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            foreach (var entry in Shortcuts.All.Where(s => s.Group == group))
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var keys = new TextBlock
                {
                    Text = entry.Keys,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas, Segoe UI"),
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9CC4FF")),
                    Margin = new Thickness(0, 2, 0, 2),
                };

                var description = new TextBlock
                {
                    Text = entry.Description,
                    FontSize = 12,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1E1")),
                    Margin = new Thickness(0, 2, 0, 2),
                };

                Grid.SetRow(keys, grid.RowDefinitions.Count - 1);
                Grid.SetColumn(keys, 0);
                Grid.SetRow(description, grid.RowDefinitions.Count - 1);
                Grid.SetColumn(description, 1);
                grid.Children.Add(keys);
                grid.Children.Add(description);
            }

            root.Children.Add(grid);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var close = new Button { Content = "Close", Width = 84, IsCancel = true };
        close.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        window.Content = root;
        window.ShowDialog();
    }
}
