using System.IO;
using Microsoft.Win32;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private int _storageChangeBusy;
    private bool _storageRestartRequired;

    private bool StorageRestartRequired => _storageRestartRequired;

    private string DisplayedDataFolder => AppSettings.Directory;
    private string DisplayedModelsFolder => ModelStorage.Root;
    private string DisplayedUserFolder => RuseFolders.UserRoot;

    private void ChooseStorageFolder(string kind)
    {
        if (!StorageFolderService.IsKnownKind(kind))
        {
            return;
        }

        string current = kind switch
        {
            StorageFolderService.Data => DisplayedDataFolder,
            StorageFolderService.Models => DisplayedModelsFolder,
            StorageFolderService.User => DisplayedUserFolder,
            _ => DisplayedDataFolder,
        };

        var dialog = new OpenFolderDialog
        {
            Title = kind switch
            {
                StorageFolderService.Data => "Choose a new data folder",
                StorageFolderService.Models => "Choose a new models folder",
                _ => "Choose a new user folder",
            },
            InitialDirectory = RuseFolders.Ensure(current),
            Multiselect = false,
        };

        if (dialog.ShowDialog(_window) == true)
        {
            Post("storage-folder-chosen", new { kind, path = dialog.FolderName });
        }
    }

    private async Task SetStorageLocationAsync(string kind, string path, bool migrate)
    {
        if (!StorageFolderService.IsKnownKind(kind))
        {
            return;
        }

        if (Interlocked.Exchange(ref _storageChangeBusy, 1) != 0)
        {
            SetStatus("A storage folder change is already running.", isError: true);
            return;
        }

        try
        {
            if (HasActiveStorageWrites())
            {
                SetStatus("Stop downloads, recordings and offline renders before changing storage folders.", isError: true);
                return;
            }

            SetStatus("Changing storage folder...");
            StorageFolderChangeResult result = await Task.Run(
                () => StorageFolderService.Change(kind, path, migrate)).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (!result.Ok)
            {
                SetStatus(result.Error, isError: true);
                return;
            }

            AppSettings.ApplyStorageLocations(result.Locations!);
            _storageRestartRequired = true;

            string copyNote = result.Copied > 0
                ? $" Copied {result.Copied} file{(result.Copied == 1 ? "" : "s")}."
                : "";
            string skippedNote = result.Skipped > 0
                ? $" Skipped {result.Skipped} internal file{(result.Skipped == 1 ? "" : "s")}."
                : "";
            SetStatus(
                "Storage folder changed. Restart Ruse to use it." +
                copyNote + skippedNote);
            ShowToast("Restart Ruse to use the new storage folder.");
            PublishSettings();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not change the storage folder.", ex);
            SetStatus("Could not change the storage folder: " + ex.Message, isError: true);
        }
        finally
        {
            Interlocked.Exchange(ref _storageChangeBusy, 0);
        }
    }

    private bool HasActiveStorageWrites()
    {
        if (_modelDownloadCancel != null ||
            _translationDownloadCancel != null ||
            _ttsDownloadCancel != null ||
            _voskDownloadCancel != null)
        {
            return true;
        }

        if (_timelineRecordingActive || _timelineBounceActive || _freezingSources.Count > 0)
        {
            return true;
        }

        return _nodes.Values.Any(entry => entry.Output?.IsRecording == true);
    }
}
