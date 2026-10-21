using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Ruse.App.Ui;

/// <summary>Identifies the role a canvas node plays in the routing graph.</summary>
public enum NodeKind
{
    Source,
    Output,
    Modifier,
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class NodePortViewModel : ObservableObject
{
    public required string Name { get; init; }
    public bool IsOutput { get; init; }
    public NodeViewModel Owner { get; init; } = null!;

    /// <summary>The connector dot this port represents, used to align cables to it.</summary>
    public FrameworkElement? Element { get; set; }

    private Point _anchor;
    public Point Anchor
    {
        get => _anchor;
        set => Set(ref _anchor, value);
    }
}

public sealed class NodeViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Accent { get; init; }
    public bool CanBeSource { get; init; }
    public string? MicHint { get; init; }

    /// <summary>What kind of graph element this node represents.</summary>
    public NodeKind Kind { get; init; } = NodeKind.Source;

    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    private string _subtitle = string.Empty;
    public string Subtitle
    {
        get => _subtitle;
        set => Set(ref _subtitle, value);
    }

    private double _x;
    public double X
    {
        get => _x;
        set { if (Set(ref _x, value)) { OnPropertyChanged(nameof(PortX)); } }
    }

    private double _y;
    public double Y
    {
        get => _y;
        set { if (Set(ref _y, value)) { OnPropertyChanged(nameof(PortY)); } }
    }

    public double Width { get; init; } = 190;
    public double Height { get; init; } = 92;

    public double PortX => X + Width;
    public double PortY => Y + (Height / 2);

    private float _meter;
    public float Meter
    {
        get => _meter;
        set => Set(ref _meter, value);
    }

    private float _volume = 1.0f;
    public float Volume
    {
        get => _volume;
        set { if (Set(ref _volume, value)) { VolumeChanged?.Invoke(); } }
    }

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set { if (Set(ref _muted, value)) { VolumeChanged?.Invoke(); } }
    }

    public Action? VolumeChanged { get; set; }
}

public sealed class ConnectionViewModel : ObservableObject
{
    public required NodePortViewModel From { get; init; }
    public required NodePortViewModel To { get; init; }

    public System.Windows.Shapes.Path? Cable { get; set; }
    public System.Windows.Shapes.Path? HitPath { get; set; }

    // The little travelling waveform drawn on top of the cable while audio
    // flows. The geometry is updated in place, so no allocation per frame.
    public System.Windows.Shapes.Path? Wave { get; set; }
    public System.Windows.Media.PathFigure? WaveFigure { get; set; }
    public System.Windows.Media.PolyLineSegment? WaveSegment { get; set; }

    /// <summary>Signal level currently flowing through this cable (0..1).</summary>
    public float Level { get; set; }

    private float _gain = 1.0f;
    public float Gain
    {
        get => _gain;
        set => Set(ref _gain, value);
    }

    private Point _start;
    public Point Start
    {
        get => _start;
        set => Set(ref _start, value);
    }

    private Point _end;
    public Point End
    {
        get => _end;
        set => Set(ref _end, value);
    }
}
