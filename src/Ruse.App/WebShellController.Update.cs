using System;
using System.Windows;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    internal void PostUpdateState(UpdateState state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _window.Dispatcher.BeginInvoke(new Action(() => Post("update-status", state)));
        }
        catch
        {
        }
    }

    internal void ShutdownForUpdate()
    {
        try
        {
            _window.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
        }
        catch
        {
        }
    }

    internal void RecordUpdateCheck()
    {
        _settings.LastUpdateCheckUtc = DateTime.UtcNow;
        _settings.Save();
    }
}
