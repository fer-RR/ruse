using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ruse.App;

public sealed class MixerGroupSettings
{
    public float Gain { get; set; } = 1f;
    public bool Muted { get; set; }
    public bool Solo { get; set; }
}

/// <summary>User settings stored in %APPDATA%\Ruse\settings.json.</summary>
public sealed class AppSettings
{
    public double Width { get; set; } = 1345;
    public double Height { get; set; } = 762;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool Maximized { get; set; }
    public bool AutoLoadSession { get; set; } = true;

    /// <summary>True when this was loaded without an existing settings file.</summary>
    [JsonIgnore]
    public bool IsFirstRun { get; private set; }
    public bool OnboardingCompleted { get; set; }
    public bool ShowWelcomeScreen { get; set; } = true;

    /// <summary>Hide the window to the notification area when minimized.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Start with the window hidden in the notification area.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Hide the window to the notification area when minimized.</summary>
    public bool MinimizeToTrayOnMinimize { get; set; }

    /// <summary>True after the first-minimize prompt has been answered.</summary>
    public bool TrayPromptShown { get; set; }

    public bool FollowPlayingApps { get; set; }

    /// <summary>Per-group gain, mute and solo state keyed by source:#color or output:#color.</summary>
    public Dictionary<string, MixerGroupSettings> MixerGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reconnect a removed app's previous outgoing route when it is added again.</summary>
    public bool RestoreAppRoutes { get; set; }
    public string TimelineBounceOutputId { get; set; } = "";
    public double TimelineBounceTailSeconds { get; set; } = 2.0;

    /// <summary>Output device used when auto-adding a newly playing app.</summary>
    public string FollowOutputId { get; set; } = "";

    /// <summary>UI language code; the engine and audio status stay English for now.</summary>
    public string UiLanguage { get; set; } = "en";

    /// <summary>Project media folders shown in the Media tab.</summary>
    public List<string> MediaFolders { get; set; } = new();

    /// <summary>Workspace colour scheme: "dark" or "light".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>WebView zoom factor for the whole interface. 1.0 is 100%.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>Animation preference: "system" follows Windows, "reduced" or "full" overrides it.</summary>
    public string MotionMode { get; set; } = "system";

    /// <summary>Draw a red border around muted nodes so they stand out.</summary>
    public bool HighlightMutedNodes { get; set; } = true;

    /// <summary>Glow nodes and library items while signal is flowing.</summary>
    public bool SignalGlow { get; set; } = true;

    /// <summary>Align dragged nodes to the 20 px canvas grid.</summary>
    public bool SnapToGrid { get; set; } = true;

    /// <summary>Highlight a cable when the pointer is over it.</summary>
    public bool HighlightCableOnHover { get; set; } = true;

    /// <summary>Animate small packets along cables that carry signal.</summary>
    public bool CableAnimation { get; set; } = true;

    /// <summary>Let cables sag and spring as nodes move.</summary>
    public bool CablePhysics { get; set; } = false;

    /// <summary>Pulse node input/output connectors while signal flows through the node.</summary>
    public bool ConnectorAnimation { get; set; } = true;

    /// <summary>Run speech and translation model inference on the GPU when a Vulkan driver is available.</summary>
    public bool UseGpu { get; set; }

    /// <summary>Accept MIDI notes and control changes for parameter learning.</summary>
    public bool MidiEnabled { get; set; }

    /// <summary>Selected MIDI input device name.</summary>
    public string MidiInputDevice { get; set; } = "";

    /// <summary>Run VST3 plugins in a child process so a plugin crash cannot close Ruse.</summary>
    public bool IsolateVst3 { get; set; } = true;

    /// <summary>Use Vosk for live partial transcripts while Whisper produces finals.</summary>
    public bool StreamingAsrEnabled { get; set; }

    /// <summary>Default Whisper model id for new transcriber nodes.</summary>
    public string TranscriberModel { get; set; } = "whisper-small.en";
    public string TranslatorModel { get; set; } = "qwen2.5-1.5b-instruct-q4_k_m";
    public string TranslatorTarget { get; set; } = "en";

    /// <summary>Optional glossary, one source=target term per line.</summary>
    public string TranslationGlossary { get; set; } = "";

    /// <summary>Reuse exact source segments from previous translations.</summary>
    public bool TranslationMemoryEnabled { get; set; } = true;

    /// <summary>Translation engine: "qwen" (default) or "opus" (ONNX Runtime).</summary>
    public string TranslationProvider { get; set; } = "qwen";

    /// <summary>Default local text-to-speech model id.</summary>
    public string TtsModel { get; set; } = "supertonic-3-int8";

    /// <summary>Speaking rate for new text-to-speech nodes (1.0 = normal).</summary>
    public float TtsSpeed { get; set; } = 1f;

    /// <summary>When the mini-map is shown (0 = always, 1 = never, 2 = when zoomed in).</summary>
    public int MinimapMode { get; set; } = 2;

    /// <summary>Show the live engine/UI diagnostics overlay for troubleshooting.</summary>
    public bool ShowDiagnostics { get; set; }

    /// <summary>Allow other local apps to control Ruse through the loopback API.</summary>
    public bool ControlApiEnabled { get; set; }

    /// <summary>Preferred loopback port; 0 or a busy port falls back to a free one.</summary>
    public int ControlApiPort { get; set; } = 47100;

    /// <summary>Check GitHub for a new release on startup and periodically.</summary>
    public bool UpdateAutoCheck { get; set; }

    /// <summary>Days between automatic update checks (1, 7 or 30).</summary>
    public int UpdateCheckIntervalDays { get; set; } = 1;

    /// <summary>UTC time of the last update check.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>Ask WASAPI for an IAudioClient3 low-latency shared stream.</summary>
    public bool LowLatencyWasapi { get; set; } = true;

    /// <summary>Try exclusive-mode output; falls back to shared mode.</summary>
    public bool ExclusiveOutput { get; set; }

    /// <summary>Automatically use native passthrough when the route is eligible.</summary>
    public bool PreferNativePassthrough { get; set; } = true;

    /// <summary>Limit output peaks before hard clipping.</summary>
    public bool ClipGuard { get; set; } = true;

    /// <summary>Use automatic audio timing choices (5 ms blocks, balanced buffer).</summary>
    public bool AudioAutoMode { get; set; } = true;

    /// <summary>Experimental 2.5 ms block mode with a larger safety ring.</summary>
    public bool LowLatencyMode { get; set; }

    /// <summary>Manual engine block size in ms (2.5/5/7.5/10).</summary>
    public double AudioBlockSizeMs { get; set; } = 5.0;

    /// <summary>Manual device buffer profile: low, balanced, safe or manual.</summary>
    public string OutputBufferMode { get; set; } = "balanced";

    /// <summary>Manual device buffer size in ms when the profile is manual.</summary>
    public int OutputBufferManualMs { get; set; } = 10;

    /// <summary>Engine cushion profile: auto (2 blocks) or manual.</summary>
    public string EngineCushionMode { get; set; } = "auto";

    /// <summary>Manual engine cushion size in ms when the profile is manual.</summary>
    public int EngineCushionManualMs { get; set; } = 10;

    /// <summary>Start with one block and grow the engine ring only after underruns.</summary>
    public bool ElasticRing { get; set; }

    /// <summary>Target one endpoint period of output cushion instead of two.</summary>
    public bool AggressiveOutputCushion { get; set; }

    /// <summary>Start the restored graph automatically once the window is ready.</summary>
    public bool StartRoutingOnLaunch { get; set; }

    private static StorageLocations _locations = new();

    static AppSettings()
    {
        _locations = ReadStorageLocations();
    }

    /// <summary>Fixed pointer folder that always lives in Roaming AppData.</summary>
    [JsonIgnore]
    public static string BootstrapDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ruse");

    [JsonIgnore]
    public static string StorageLocationsPath => Path.Combine(BootstrapDirectory, "storage.json");

    [JsonIgnore]
    public static string DefaultDataDirectory => BootstrapDirectory;

    /// <summary>Settings/session folder; self-tests redirect it.</summary>
    [JsonIgnore]
    public static string Directory =>
        OverrideDirectory ?? _locations.DataDirectory ?? DefaultDataDirectory;

    [JsonIgnore]
    public static string? ConfiguredModelsDirectory => _locations.ModelsDirectory;

    [JsonIgnore]
    public static string? ConfiguredUserDirectory => _locations.UserDirectory;

    /// <summary>Redirects settings/session storage (used by the UI self-test).</summary>
    [JsonIgnore]
    public static string? OverrideDirectory { get; set; }

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(Directory, "settings.json");

    [JsonIgnore]
    public static string SessionPath => Path.Combine(Directory, "session.ruse.json");

    public static StorageLocations SnapshotStorageLocations() => new()
    {
        DataDirectory = _locations.DataDirectory,
        ModelsDirectory = _locations.ModelsDirectory,
        UserDirectory = _locations.UserDirectory,
    };

    public static bool SaveStorageLocations(StorageLocations locations, out string error)
    {
        System.IO.Directory.CreateDirectory(BootstrapDirectory);
        return AtomicFile.TryWriteAllText(
            StorageLocationsPath,
            JsonSerializer.Serialize(locations, Options),
            out error);
    }

    public static void ApplyStorageLocations(StorageLocations locations)
    {
        _locations = new StorageLocations
        {
            DataDirectory = NormalizeStoredPath(locations.DataDirectory),
            ModelsDirectory = NormalizeStoredPath(locations.ModelsDirectory),
            UserDirectory = NormalizeStoredPath(locations.UserDirectory),
        };
    }

    private static StorageLocations ReadStorageLocations()
    {
        try
        {
            if (File.Exists(StorageLocationsPath))
            {
                StorageLocations? loaded = JsonSerializer.Deserialize<StorageLocations>(
                    File.ReadAllText(StorageLocationsPath), Options);
                if (loaded != null)
                {
                    loaded.DataDirectory = NormalizeStoredPath(loaded.DataDirectory);
                    loaded.ModelsDirectory = NormalizeStoredPath(loaded.ModelsDirectory);
                    loaded.UserDirectory = NormalizeStoredPath(loaded.UserDirectory);
                    return loaded;
                }
            }
        }
        catch
        {
        }

        return new StorageLocations();
    }

    private static string? NormalizeStoredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };
    public static AppSettings Load()
    {
        bool exists = File.Exists(SettingsPath);
        AppSettings settings = new();

        try
        {
            if (exists)
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options) ?? new AppSettings();
            }
        }
        catch
        {
        }

        settings.IsFirstRun = !exists;
        return settings;
    }

    public void Save()
    {
        if (!AtomicFile.TryWriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options), out string error))
        {
            Diagnostics.AppLog.Warning("Could not save settings: " + error);
        }
    }
}
