using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ruse.App.Diagnostics;

namespace Ruse.App.Ui;

/// <summary>Result of the settings dialog: the options the user changed.</summary>
public sealed class SettingsResult
{
    public bool HighlightMutedNodes { get; init; }
    public bool AnimateCables { get; init; }
    public int MinimapMode { get; init; } = 2;
}

/// <summary>
/// The application settings dialog. Built in code, like the other small
/// dialogs, so it stays in one file with the rest of the UI helpers.
/// </summary>
public static class SettingsDialog
{
    public static SettingsResult? Show(Window owner, bool highlightMutedNodes, bool animateCables, int minimapMode = 2)
    {
        var window = new Window
        {
            Title = "Settings",
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#181B22")),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6EAF2")),
            MinWidth = 460,
        };

        var root = new StackPanel { Margin = new Thickness(20) };

        // ---- Appearance -------------------------------------------------
        root.Children.Add(SectionHeader("Appearance"));

        var mutedBox = new CheckBox
        {
            Content = "Highlight muted nodes with a red border",
            IsChecked = highlightMutedNodes,
            Margin = new Thickness(0, 4, 0, 8),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1E1")),
        };

        var waveBox = new CheckBox
        {
            Content = "Animate a waveform on the cables while audio flows",
            IsChecked = animateCables,
            Margin = new Thickness(0, 4, 0, 12),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1E1")),
        };

        root.Children.Add(mutedBox);
        root.Children.Add(waveBox);

        // ---- Mini-map ----
        root.Children.Add(SectionHeader("Canvas"));

        int minimapChanged = minimapMode;

        var minimapBox = new ComboBox
        {
            Margin = new Thickness(0, 4, 0, 14),
            Padding = new Thickness(6, 3, 6, 3),
            FontSize = 12,
            MinWidth = 220,
        };

        minimapBox.Items.Add("Always show the mini-map");
        minimapBox.Items.Add("Never show the mini-map");
        minimapBox.Items.Add("Show it only when zoomed in");
        minimapBox.SelectedIndex = minimapMode < 0 ? 0 : (minimapMode > 2 ? 2 : minimapMode);
        minimapBox.SelectionChanged += (_, _) => minimapChanged = minimapBox.SelectedIndex;

        root.Children.Add(new TextBlock
        {
            Text = "Mini-map",
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 2),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C9D1E1")),
        });
        root.Children.Add(minimapBox);

        // ---- Diagnostics ------------------------------------------------
        root.Children.Add(SectionHeader("Diagnostics"));

        root.Children.Add(new TextBlock
        {
            Text = "Log file: " + AppLog.LogPath,
            Margin = new Thickness(0, 4, 0, 8),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8892A6")),
        });

        var diagButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

        var openLog = SmallButton("Open log");
        openLog.Click += (_, _) => AppLog.OpenLog();
        diagButtons.Children.Add(openLog);

        var openFolder = SmallButton("Open log folder");
        openFolder.Click += (_, _) => AppLog.OpenLogFolder();
        diagButtons.Children.Add(openFolder);

        var copyDiag = SmallButton("Copy diagnostics");
        copyDiag.Click += (_, _) =>
        {
            try
            {
                string summary = BuildDiagnostics();
                Clipboard.SetText(summary);
                AppLog.Info("Diagnostics copied to the clipboard.");
            }
            catch (Exception ex)
            {
                AppLog.Warning("Could not copy the diagnostics: " + ex.Message);
            }
        };
        diagButtons.Children.Add(copyDiag);

        root.Children.Add(diagButtons);

        int tier = RenderCapability.Tier;
        string tierText = tier == 0x80000
            ? "Graphics: hardware accelerated (tier 2)."
            : tier == 0x20000
                ? "Graphics: software rendering (tier 0) — the interface will feel slower. A known Windows 11 24H2/25H2 quirk: try disabling hardware-accelerated GPU scheduling or an older GPU driver."
                : "Graphics: partial acceleration (tier 1).";

        root.Children.Add(new TextBlock
        {
            Text = tierText,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tier == 0x80000 ? "#6F8AA8" : "#C8A26A")),
        });

        // ---- Buttons ----------------------------------------------------
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };

        SettingsResult? result = null;

        var ok = new Button { Content = "Save", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            result = new SettingsResult
            {
                HighlightMutedNodes = mutedBox.IsChecked == true,
                AnimateCables = waveBox.IsChecked == true,
                MinimapMode = minimapChanged,
            };
            window.DialogResult = true;
        };

        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        window.Content = root;

        return window.ShowDialog() == true ? result : null;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 6),
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6E7A90")),
    };

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(10, 4, 10, 4),
        Margin = new Thickness(0, 0, 8, 0),
        FontSize = 12,
    };

    /// <summary>A short summary of the environment, for bug reports.</summary>
    private static string BuildDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Ruse diagnostics");
        sb.AppendLine("  time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("  os: " + Environment.OSVersion.VersionString);
        sb.AppendLine("  runtime: " + Environment.Version);
        sb.AppendLine("  graphics tier: " + RenderCapability.Tier +
            (RenderCapability.Tier == 0x80000 ? " (hardware)" : RenderCapability.Tier == 0x20000 ? " (software)" : " (partial)"));
        sb.AppendLine("  log: " + AppLog.LogPath);
        sb.AppendLine("  recent log entries:");
        foreach (string line in AppLog.Recent(25))
        {
            sb.AppendLine("    " + line);
        }

        return sb.ToString();
    }
}