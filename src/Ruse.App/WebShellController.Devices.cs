using NAudio.CoreAudioApi;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDeviceNotificationClient? _deviceNotifications;
    private int _deviceRefreshPending;
    private int _deviceScanRunning;

    /// <summary>
    /// Subscribes to Core Audio endpoint/default-device notifications. Changes
    /// are debounced onto the UI thread and use the same device refresh path as
    /// the manual button; there is no steady-state polling.
    /// </summary>
    private void RegisterDeviceNotifications()
    {
        if (_deviceEnumerator != null || _disposed)
        {
            return;
        }

        try
        {
            var enumerator = new MMDeviceEnumerator();
            var client = enumerator.CreateNotificationClient(useSynchronizationContext: true);
            client.DeviceStateChanged += OnDeviceStateChanged;
            client.DeviceAdded += OnDeviceAdded;
            client.DeviceRemoved += OnDeviceRemoved;
            client.DefaultDeviceChanged += OnDefaultDeviceChanged;
            client.PropertyValueChanged += OnDevicePropertyChanged;
            _deviceEnumerator = enumerator;
            _deviceNotifications = client;
        }
        catch (Exception ex)
        {
            _deviceNotifications?.Dispose();
            _deviceNotifications = null;
            _deviceEnumerator?.Dispose();
            _deviceEnumerator = null;
            AppLog.Warning("Core Audio device notifications are unavailable: " + ex.Message);

            // Keep a slow safety net rather than the old 2 s poll.
            _devicesTimer.Interval = TimeSpan.FromSeconds(30);
            _devicesTimer.Start();
        }
    }

    private void UnregisterDeviceNotifications()
    {
        var client = _deviceNotifications;
        var enumerator = _deviceEnumerator;
        _deviceNotifications = null;
        _deviceEnumerator = null;

        if (client != null)
        {
            try
            {
                client.DeviceStateChanged -= OnDeviceStateChanged;
                client.DeviceAdded -= OnDeviceAdded;
                client.DeviceRemoved -= OnDeviceRemoved;
                client.DefaultDeviceChanged -= OnDefaultDeviceChanged;
                client.PropertyValueChanged -= OnDevicePropertyChanged;
                client.Dispose();
            }
            catch
            {
            }
        }

        try
        {
            enumerator?.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Reconnects device outputs that stopped after an endpoint disappeared and
    /// came back. Only runs while routing is active, or after a fault stopped
    /// routing, so a manual device refresh never starts a stopped session.
    /// </summary>
    private bool RecoverOutputDevices()
    {
        if (_disposed || (!_running && !_routingFaultStopped))
        {
            return false;
        }

        bool recovered = false;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var entry in _nodes.Values)
        {
            if (entry.Output is not { IsRunning: false } output ||
                output.IsNetworkOutput ||
                output.IsFileRecorder ||
                string.IsNullOrWhiteSpace(output.DeviceId))
            {
                continue;
            }

            MMDevice? device = null;
            bool rebound = false;
            try
            {
                device = enumerator.GetDevice(output.DeviceId);
                if (device.State != DeviceState.Active)
                {
                    device.Dispose();
                    continue;
                }

                if (!output.RebindDevice(device))
                {
                    device.Dispose();
                    continue;
                }

                rebound = true;
                recovered = true;
                if (_running)
                {
                    output.Start();
                }
            }
            catch (Exception ex)
            {
                if (!rebound)
                {
                    device?.Dispose();
                }

                AppLog.Warning($"Could not reconnect output '{entry.Title}': {ex.Message}");
            }
        }

        bool restartRouting = recovered && _routingFaultStopped;
        if (restartRouting)
        {
            StartRouting();
        }
        else if (recovered)
        {
            SetStatus("Audio device reconnected; routing resumed.");
        }

        return recovered;
    }

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e) =>
        RequestDeviceRefresh();

    private void OnDeviceAdded(object? sender, DeviceNotificationEventArgs e) =>
        RequestDeviceRefresh();

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e) =>
        RequestDeviceRefresh();

    private void OnDefaultDeviceChanged(object? sender, DefaultDeviceChangedEventArgs e) =>
        RequestDeviceRefresh();

    private void OnDevicePropertyChanged(object? sender, DevicePropertyChangedEventArgs e) =>
        RequestDeviceRefresh();

    private void RequestDeviceRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _deviceRefreshPending, 1) == 1)
        {
            return;
        }

        try
        {
            _window.Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _deviceRefreshPending, 0);
                if (_disposed)
                {
                    return;
                }

                _devicesTimer.Stop();
                _devicesTimer.Start();
            }));
        }
        catch
        {
            Interlocked.Exchange(ref _deviceRefreshPending, 0);
        }
    }
}
