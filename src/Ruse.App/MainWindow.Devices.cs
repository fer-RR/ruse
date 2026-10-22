/// <summary>
/// Devices part of the main window. The shared state lives in MainWindow.xaml.cs;
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
    private static MMDevice? GetFirstDevice(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault();
    }

    private DeviceItem FirstOutputItem()
    {
        if (OutputsList.ItemsSource is IEnumerable<DeviceItem> items)
        {
            var first = items.FirstOrDefault();
            if (first != null)
            {
                return first;
            }
        }

        return new DeviceItem(GetFirstDevice(DataFlow.Render)
            ?? throw new InvalidOperationException("No render devices available."));
    }

    private DeviceItem? SecondOutputItem()
    {
        if (OutputsList.ItemsSource is IEnumerable<DeviceItem> items)
        {
            return items.Skip(1).FirstOrDefault();
        }

        return null;
    }

    private NodeViewModel? TryAddVirtual(double x, double y)
    {
        if (VirtualList.ItemsSource is IEnumerable<VirtualOutputInfo> virt && virt.Any())
        {
            return AddVirtualOutputNode(virt.First(), x, y);
        }

        return null;
    }

    private void RefreshAllDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _allOutputs = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(d => new DeviceItem(d))
                .ToList();

            _allInputs = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(d => new DeviceItem(d))
                .ToList();

            // ASIO drivers join the output list: they are the low-latency path
            // for anyone with a pro interface. They are exclusive, so they are
            // listed but never opened until the user adds one.
            var asioDrivers = AsioCatalog.Discover();
            foreach (var driver in asioDrivers)
            {
                _allOutputs.Add(new DeviceItem(driver));

                // The same driver can also capture its input channels.
                if (driver.InputChannelCount > 0)
                {
                    _allInputs.Add(new DeviceItem(driver, asioInput: true));
                }
            }

            var virtuals = VirtualDeviceCatalog.Discover();
            VirtualList.ItemsSource = virtuals;
            VirtualEmptyHint.Visibility = virtuals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            ApplyFilter();
            SetStatus("Devices refreshed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Device enumeration failed: {ex.Message}");
            AppLog.Warning("Device enumeration failed: " + ex.Message);
        }
    }

    private void RefreshApps()
    {
        // Enumerating every audio session on every device is the one thing
        // that costs real CPU while nothing is happening, so it only runs while
        // the window is in front. Coming back to the window refreshes at once.
        if (!IsActive && !_refreshOnActivate)
        {
            return;
        }

        _refreshOnActivate = false;

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var sessions = AudioSessionEnumerator.GetRenderSessions();

            // Preserve the user's selection/scroll across the periodic refresh
            // (bug: the list used to reset every 4s).
            var previous = AppsList.SelectedItem as AudioSessionInfo;
            _allApps = sessions;
            ApplyFilter();

            if (previous != null)
            {
                var match = _allApps.FirstOrDefault(a => a.ProcessId == previous.ProcessId);
                if (match != null)
                {
                    AppsList.SelectedItem = match;
                }
            }

            AppsCount.Text = sessions.Count.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Warning("Session enumeration failed: " + ex.Message);
            SetStatus($"Session enumeration failed: {ex.Message}");
        }
        finally
        {
            _refreshCount++;
            _refreshMs += (System.Diagnostics.Stopwatch.GetTimestamp() - start)
                / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
        }
    }

    private IReadOnlyList<AudioSessionInfo> _allApps = Array.Empty<AudioSessionInfo>();
    private List<DeviceItem> _allOutputs = new();
    private List<DeviceItem> _allInputs = new();

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        string query = SearchBox?.Text?.Trim() ?? string.Empty;

        var apps = Filter(_allApps, query, a => a.Label);
        var outputs = Filter(_allOutputs, query, d => d.FriendlyName);
        var inputs = Filter(_allInputs, query, d => d.FriendlyName);

        // Only replace the list contents when something actually changed:
        // assigning ItemsSource rebuilds every row's visual tree, which is far
        // more expensive than the comparison.
        if (!SameItems(AppsList.ItemsSource as IEnumerable<AudioSessionInfo>, apps))
        {
            AppsList.ItemsSource = apps;
        }

        if (!SameItems(OutputsList.ItemsSource as IEnumerable<DeviceItem>, outputs))
        {
            OutputsList.ItemsSource = outputs;
        }

        if (!SameItems(InputsList.ItemsSource as IEnumerable<DeviceItem>, inputs))
        {
            InputsList.ItemsSource = inputs;
        }

        _filterCount++;
        _filterMs += (System.Diagnostics.Stopwatch.GetTimestamp() - start)
            / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0;
    }

    /// <summary>True when two lists hold the same items in the same order.</summary>
    private static bool SameItems<T>(IEnumerable<T>? current, List<T> next)
    {
        if (current == null)
        {
            return false;
        }

        using var e1 = current.GetEnumerator();
        using var e2 = next.GetEnumerator();
        while (true)
        {
            bool has1 = e1.MoveNext();
            bool has2 = e2.MoveNext();
            if (has1 != has2)
            {
                return false;
            }

            if (!has1)
            {
                return true;
            }

            if (!EqualityComparer<T>.Default.Equals(e1.Current, e2.Current))
            {
                return false;
            }
        }
    }

    private static List<T> Filter<T>(IReadOnlyList<T> source, string query, Func<T, string> selector)
    {
        if (string.IsNullOrEmpty(query))
        {
            return source.ToList();
        }

        return source
            .Where(item => selector(item).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private sealed class DeviceItem
    {
        public DeviceItem(MMDevice device)
        {
            Id = device.ID;
            FriendlyName = device.FriendlyName;
        }

        public DeviceItem(AsioDriverInfo driver)
        {
            Id = driver.Id;
            FriendlyName = driver.Name;
            Asio = driver;
        }

        public DeviceItem(AsioDriverInfo driver, bool asioInput)
            : this(driver)
        {
            IsAsioInput = asioInput;
        }

        public string Id { get; }
        public string FriendlyName { get; }

        /// <summary>Set when the entry is an ASIO driver rather than a WASAPI endpoint.</summary>
        public AsioDriverInfo? Asio { get; }

        /// <summary>True for ASIO entries, so the list can badge them.</summary>
        public bool IsAsio => Asio != null;

        /// <summary>True when this ASIO entry is listed as a capture device.</summary>
        public bool IsAsioInput { get; }

        /// <summary>Label shown in the device list.</summary>
        public string DisplayName => Asio != null ? FriendlyName + "  (ASIO)" : FriendlyName;
    }
}
