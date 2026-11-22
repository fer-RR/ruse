using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using Ruse.App.Audio;
using Ruse.App.Diagnostics;

namespace Ruse.App;

internal sealed partial class WebShellController
{
    private const int SupportLogTailBytes = 256 * 1024;
    private const long SupportReportMaxBytes = 10L * 1024 * 1024;
    private bool _supportExporting;
    private static readonly HttpClient SupportUploadClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly Regex WindowsPathPattern = new(@"(?i)\b[A-Z]:\\[^\r\n'""]+", RegexOptions.Compiled);
    private static readonly Regex UncPathPattern = new(@"(?i)\\\\[^\s'""]+", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(@"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex Ipv4Pattern = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex EndpointIdPattern = new(@"(?i)\{[0-9a-f.-]{20,}\}", RegexOptions.Compiled);
    private static readonly Regex GuidPattern = new(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.Compiled);
    private static readonly Regex QuotedNamePattern = new(@"'[^'\r\n]{1,200}'", RegexOptions.Compiled);
    private static readonly Regex UserAudioFilePattern = new(@"(?i)\b[^\s/\\:*?""<>|]+\.(?:wav|mp3|flac|m4a|aac|ogg|opus|aiff|wma)\b", RegexOptions.Compiled);

    private bool HasNetworkNodes() =>
        _nodes.Values.Any(entry =>
            entry.Source?.Kind == SourceKind.Network || entry.Output?.IsNetworkOutput == true);

    private string RedactSensitiveContent(string text)
    {
        foreach (string value in SensitiveTextValues())
        {
            text = text.Replace(value, "<redacted-text>", StringComparison.Ordinal);
        }

        return text;
    }

    private List<string> SensitiveTextValues()
    {
        var values = new List<string>();
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3)
            {
                values.Add(value);
            }
        }

        foreach (var entry in _nodes.Values)
        {
            Add(entry.TextContent);
            Add(entry.TextCommittedContent);
            Add(entry.TtsText);
            if (entry.TranslatorText?.Snapshot() is { } networkText)
            {
                Add(networkText.SourceText);
                Add(networkText.TranslatedText);
            }

            if (entry.Modifier == null)
            {
                continue;
            }

            foreach (var processor in entry.Modifier.Processors)
            {
                if (processor is TranscriptionProcessor transcriber)
                {
                    var snapshot = TranscriptionRegistry.Snapshot(transcriber.TapId);
                    if (snapshot != null)
                    {
                        Add(snapshot.Transcript);
                        Add(snapshot.Partial);
                    }
                }
                else if (processor is TranslatorProcessor translator)
                {
                    var snapshot = TranslationRegistry.Snapshot(translator.TapId);
                    if (snapshot != null)
                    {
                        Add(snapshot.SourceText);
                        Add(snapshot.SourcePartial);
                        Add(snapshot.TranslatedText);
                        Add(snapshot.TranslatedPartial);
                    }
                }
            }
        }

        return values
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToList();
    }

    private string BuildDiagnosticsReport()
    {
        int installedCount = TranscriptionModels.All.Count(model => TranscriptionModels.IsInstalled(model.Id));
        long installedBytes = TranscriptionModels.All
            .Where(model => TranscriptionModels.IsInstalled(model.Id))
            .Sum(model => TranscriptionModels.GetInstalledSizeBytes(model.Id));
        int installedTranslationCount = TranslationModels.All.Count(model => TranslationModels.IsInstalled(model.Id));
        long installedTranslationBytes = TranslationModels.All
            .Where(model => TranslationModels.IsInstalled(model.Id))
            .Sum(model => TranslationModels.GetInstalledSizeBytes(model.Id));
        string version = typeof(WebShellController).Assembly.GetName().Version?.ToString(3) ?? "dev";
        string? networkIp = HasNetworkNodes() ? NetworkPcm.GetLocalAddress() : null;
        var lines = new List<string>
        {
            $"Ruse {version}",
            $"OS: {Environment.OSVersion.VersionString}",
            $".NET: {Environment.Version}",
            $"WebView2: {(_webView2Version.Length == 0 ? "not initialized" : _webView2Version)}",
            $"WPF render tier: {System.Windows.Media.RenderCapability.Tier >> 16}",
            $"Data: {AppSettings.Directory}",
            $"Models: {TranscriptionModels.Root}",
            $"Installed models: {installedCount} ({TranscriptionModels.FormatSize(installedBytes)})",
            $"Default model: {_settings.TranscriberModel}",
            $"Translation models: {installedTranslationCount} ({TranslationModels.FormatSize(installedTranslationBytes)})",
            $"Default translation: {_settings.TranslatorModel} -> {_settings.TranslatorTarget}",
            $"GPU: {(ModelRuntime.GpuAvailable ? ModelRuntime.GpuDeviceName : ModelRuntime.GpuStatusError ?? "not available")}",
            $"GPU fallback: {ModelRuntime.GpuFallbackReason ?? "none"}",
            $"Output underruns: {_graph.Outputs.Sum(output => output.Underruns)}",
            $"Endpoint starved frames: {_graph.Outputs.Sum(output => output.EndpointStarvedFrames)}",
            $"Endpoint dropped frames: {_graph.Outputs.Sum(output => output.EndpointDroppedFrames)}",
            $"Tap dropped frames: {_graph.TapDroppedFrames}",
            $"Network lost packets: {_graph.Sources.Sum(source => source.NetworkLostPackets)}",
            $"Network stream resets: {_graph.Sources.Sum(source => source.NetworkStreamResets)}",
        };
        if (networkIp != null)
        {
            lines.Add($"Network local LAN IP: {networkIp}");
        }

        lines.Add($"Nodes: {_nodes.Count} / links: {_links.Count} / running: {_running}");
        return SanitizeSupportText(RedactSensitiveContent(string.Join(Environment.NewLine, lines)), networkIp);
    }

    private string BuildBasicDiagnosticsReport()
    {
        string version = typeof(WebShellController).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var lines = new List<string>
        {
            $"Ruse {version}",
            $"OS: {Environment.OSVersion.VersionString}",
            $".NET: {Environment.Version}",
            $"WebView2: {(_webView2Version.Length == 0 ? "not initialized" : _webView2Version)}",
            $"WPF render tier: {System.Windows.Media.RenderCapability.Tier >> 16}",
            $"GPU: {(ModelRuntime.GpuAvailable ? ModelRuntime.GpuDeviceName : ModelRuntime.GpuStatusError ?? "not available")}",
            $"Nodes: {_nodes.Count} / links: {_links.Count} / running: {_running}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private string BuildSettingsReport()
    {
        string version = typeof(WebShellController).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var safe = new
        {
            appVersion = version,
            ui = new
            {
                language = _settings.UiLanguage,
                theme = _settings.Theme,
                scale = _settings.UiScale,
                motion = _settings.MotionMode,
            },
            audio = new
            {
                blockMs = _settings.AudioBlockSizeMs,
                bufferMode = _settings.OutputBufferMode,
                bufferManualMs = _settings.OutputBufferManualMs,
                cushionMode = _settings.EngineCushionMode,
                cushionManualMs = _settings.EngineCushionManualMs,
                elasticRing = _settings.ElasticRing,
                lowLatencyMode = _settings.LowLatencyMode,
                lowLatencyWasapi = _settings.LowLatencyWasapi,
                exclusiveOutput = _settings.ExclusiveOutput,
                nativePassthrough = _settings.PreferNativePassthrough,
                aggressiveCushion = _settings.AggressiveOutputCushion,
                clipGuard = _settings.ClipGuard,
            },
            speech = new
            {
                gpu = _settings.UseGpu,
                streamingAsr = _settings.StreamingAsrEnabled,
                transcriberModel = _settings.TranscriberModel,
                translatorModel = _settings.TranslatorModel,
                translatorTarget = _settings.TranslatorTarget,
                ttsModel = _settings.TtsModel,
                ttsSpeed = _settings.TtsSpeed,
            },
            automation = new
            {
                controlApi = _settings.ControlApiEnabled,
                midi = _settings.MidiEnabled,
                isolateVst3 = _settings.IsolateVst3,
            },
        };

        return SanitizeSupportText(RedactSensitiveContent(System.Text.Json.JsonSerializer.Serialize(safe, _json)));
    }

    private string BuildDevicesReport()
    {
        var lines = new List<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            TryAddDefaultEndpoint(lines, enumerator, DataFlow.Render, "Default output");
            TryAddDefaultEndpoint(lines, enumerator, DataFlow.Capture, "Default input");
            lines.Add("");
            AddEndpoints(lines, enumerator, DataFlow.Render, "Render endpoints");
            lines.Add("");
            AddEndpoints(lines, enumerator, DataFlow.Capture, "Capture endpoints");
        }
        catch (Exception ex)
        {
            lines.Add("Could not enumerate audio devices: " + ex.Message);
        }

        return SanitizeSupportText(string.Join(Environment.NewLine, lines));
    }

    private static void TryAddDefaultEndpoint(
        List<string> lines,
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        string label)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            lines.Add($"{label}: {device.FriendlyName}");
        }
        catch (Exception ex)
        {
            lines.Add($"{label}: unavailable ({ex.Message})");
        }
    }

    private static void AddEndpoints(
        List<string> lines,
        MMDeviceEnumerator enumerator,
        DataFlow flow,
        string label)
    {
        lines.Add(label + ":");
        try
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    lines.Add($"  {device.FriendlyName} [{device.ID}]");
                }
            }
        }
        catch (Exception ex)
        {
            lines.Add("  unavailable (" + ex.Message + ")");
        }
    }

    private string BuildRoutesReport()
    {
        var lines = new List<string>();
        lines.Add("Nodes:");
        foreach (var entry in _nodes.Values.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase))
        {
            string state = entry.Modifier != null
                ? entry.Modifier.Enabled ? "enabled" : "bypassed"
                : entry.Source?.Muted == true || entry.Output?.Muted == true ? "muted" : "active";
            string fault = entry.Modifier?.Faulted == true ? " faulted" : "";
            lines.Add($"  [{entry.Kind}/{entry.Flavor}] {SupportNodeName(entry)} ({state}{fault})");
        }

        lines.Add("");
        lines.Add("Links:");
        foreach (var link in _links.OrderBy(item => item.From, StringComparer.Ordinal))
        {
            string from = _nodes.TryGetValue(link.From, out var fromEntry) ? SupportNodeName(fromEntry) : link.From;
            string to = _nodes.TryGetValue(link.To, out var toEntry) ? SupportNodeName(toEntry) : link.To;
            string flags = link.Enabled ? "enabled" : "disabled";
            if (link.Native) flags += " native";
            lines.Add($"  {from} -> {to} [{link.Kind}, {flags}, gain {link.Gain:0.00}]");
        }

        return SanitizeSupportText(RedactSensitiveContent(string.Join(Environment.NewLine, lines)));
    }

    private string BuildNetworkReport()
    {
        string localIp = NetworkPcm.GetLocalAddress();
        var lines = new List<string>
        {
            $"Local LAN IP: {localIp}",
            "Network nodes:",
        };

        foreach (var entry in _nodes.Values.Where(entry =>
                     entry.Source?.Kind == SourceKind.Network || entry.Output?.IsNetworkOutput == true))
        {
            var source = entry.Source;
            var output = entry.Output;
            int voicePort = source?.NetworkPort ?? output?.NetworkPort ?? NetworkPcm.DefaultPort;
            bool running = source?.IsRunning == true || output?.IsRunning == true;
            lines.Add(
                $"  {SupportNodeName(entry)} | voice UDP {voicePort} | text TCP {entry.ChatPort} | " +
                $"running {running} | packets {source?.NetworkPackets ?? 0} | " +
                $"lost {source?.NetworkLostPackets ?? 0} | resets {source?.NetworkStreamResets ?? 0}");
        }

        return RedactSensitiveContent(string.Join(Environment.NewLine, lines));
    }

    private static string SupportNodeName(NodeEntry entry) =>
        entry.Source?.Kind == SourceKind.File
            ? "<file player>"
            : entry.Kind == "text"
                ? "<text note>"
                : entry.Title;

    private static string SanitizeSupportText(string text, string? preserveIp = null)
    {
        const string preserveMarker = "<network-local-ip>";
        if (!string.IsNullOrWhiteSpace(preserveIp))
        {
            text = Regex.Replace(
                text,
                $@"(?<![\d.]){Regex.Escape(preserveIp)}(?![\d.])",
                preserveMarker);
        }

        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = EndpointIdPattern.Replace(text, "<device-id>");
        text = GuidPattern.Replace(text, "<id>");
        text = WindowsPathPattern.Replace(text, "<path>");
        text = UncPathPattern.Replace(text, "<path>");
        text = EmailPattern.Replace(text, "<email>");
        text = Ipv4Pattern.Replace(text, match => match.Value == "127.0.0.1" ? match.Value : "<ip>");
        return text.Replace(preserveMarker, preserveIp ?? "", StringComparison.Ordinal);
    }

    private static string SanitizeLogText(string text)
    {
        text = SanitizeSupportText(text);
        text = UserAudioFilePattern.Replace(text, "<audio-file>");
        return QuotedNamePattern.Replace(text, "'<name>'");
    }

    private string ReadLogTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return SanitizeLogText(RedactSensitiveContent(reader.ReadToEnd()));
        }
        catch (Exception ex)
        {
            return "Could not read log: " + ex.Message;
        }
    }

    private static void AddBundleText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private string? ExportSupportBundle(
        string? directory = null,
        bool announce = true,
        bool includeDiagnostics = true,
        string? description = null,
        string? devicesReport = null)
    {
        try
        {
            string path = NextSupportBundlePath(directory);
            List<(string Name, string Text)> entries =
                BuildSupportBundleEntries(includeDiagnostics, description, devicesReport);
            WriteSupportBundle(path, entries);
            if (announce)
            {
                SetStatus($"Support bundle saved to {path}");
                ShowToast($"Support bundle saved: {Path.GetFileName(path)}");
            }

            return path;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not create the support bundle.", ex);
            if (announce)
            {
                SetStatus("Could not create the support bundle: " + ex.Message, isError: true);
            }

            return null;
        }
    }

    private async Task<string?> CreateSupportBundleAsync(
        bool includeDiagnostics,
        string? description,
        string? devicesReport,
        string? directory = null)
    {
        string path = NextSupportBundlePath(directory);
        List<(string Name, string Text)> entries =
            BuildSupportBundleEntries(includeDiagnostics, description, devicesReport);
        await Task.Run(() => WriteSupportBundle(path, entries)).ConfigureAwait(true);
        return path;
    }

    private static string NextSupportBundlePath(string? directory)
    {
        directory = RuseFolders.Ensure(directory ?? RuseFolders.Support);
        string stem = $"ruse-support-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(directory, stem + ".zip");
        for (int suffix = 2; File.Exists(path); suffix++)
        {
            path = Path.Combine(directory, $"{stem}-{suffix}.zip");
        }

        return path;
    }

    private List<(string Name, string Text)> BuildSupportBundleEntries(
        bool includeDiagnostics,
        string? description,
        string? devicesReport)
    {
        var entries = new List<(string Name, string Text)>();
        string version = typeof(WebShellController).Assembly.GetName().Version?.ToString(3) ?? "dev";
        string trimmedDescription = (description ?? "").Trim();
        bool hasNetwork = HasNetworkNodes();
        if (includeDiagnostics)
        {
            entries.Add(("README.txt", string.Join(Environment.NewLine, new[]
            {
                "Ruse support bundle",
                $"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                "",
                "Contents:",
                "  description.txt  - text entered by the user",
                "  diagnostics.txt  - app, audio and routing counters",
                "  settings.json    - non-secret settings only",
                "  devices.txt      - active Windows audio endpoints",
                "  routes.txt       - node and cable summary",
                "  network.txt      - local LAN IP and ports for network nodes (when present)",
                "  log-tail.txt     - sanitized last 256 KB of ruse.log",
                "",
                "No audio, recordings, models, clipboard contents, transcriptions,",
                "translations, text notes or TTS contents are included.",
                "The local LAN IP of a network node is kept for troubleshooting.",
                "Other IP addresses are redacted.",
                "Local paths, email addresses, endpoint IDs and quoted log names are redacted.",
                "Device names, app names and effect names may remain.",
                "Review this ZIP before sending it to anyone.",
                "",
            })));
            entries.Add(("diagnostics.txt", BuildDiagnosticsReport()));
            entries.Add(("settings.json", BuildSettingsReport()));
            entries.Add(("devices.txt", devicesReport ?? BuildDevicesReport()));
            entries.Add(("routes.txt", BuildRoutesReport()));
            if (hasNetwork)
            {
                entries.Add(("network.txt", BuildNetworkReport()));
            }

            string logTail = ReadLogTail(AppLog.LogPath, SupportLogTailBytes);
            if (hasNetwork)
            {
                logTail = $"Network local LAN IP: {NetworkPcm.GetLocalAddress()}" +
                    Environment.NewLine + logTail;
            }

            entries.Add(("log-tail.txt", logTail));
        }
        else
        {
            entries.Add(("README.txt", string.Join(Environment.NewLine, new[]
            {
                "Ruse bug report",
                $"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                "",
                "Logs and diagnostics were omitted at the user's request.",
                "Only the description and app version are included.",
                "",
            })));
            entries.Add(("version.txt", $"Ruse {version}{Environment.NewLine}"));
        }

        if (trimmedDescription.Length > 0)
        {
            entries.Add(("description.txt", SanitizeSupportText(trimmedDescription)));
        }

        return entries;
    }

    private static void WriteSupportBundle(string path, List<(string Name, string Text)> entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            AddBundleText(archive, item.Name, item.Text);
        }
    }
    private async Task ExportSupportBundleAsync()
    {
        _supportExporting = true;
        try
        {
            string? devicesReport = await Task.Run(BuildDevicesReport).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            string? path;
            try
            {
                path = await CreateSupportBundleAsync(
                    includeDiagnostics: true,
                    description: null,
                    devicesReport: devicesReport).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not create the support bundle.", ex);
                SetStatus("Could not create the support bundle: " + ex.Message, isError: true);
                return;
            }

            if (_disposed)
            {
                return;
            }

            if (path == null)
            {
                SetStatus("Could not create the support bundle. See the log for details.", isError: true);
                return;
            }

            SetStatus($"Support bundle saved to {path}");
            ShowToast($"Support bundle saved: {Path.GetFileName(path)}");
        }
        finally
        {
            _supportExporting = false;
        }
    }

    private async Task SendSupportReportAsync(string note, bool includeDiagnostics)
    {
        try
        {
            string trimmedNote = (note ?? "").Trim();
            if (trimmedNote.Length == 0)
            {
                PostSupportReportError("DESCRIPTION", "A description is required.");
                return;
            }

            if (trimmedNote.Length > 4000)
            {
                trimmedNote = trimmedNote[..4000];
            }

            string uploadUrl = SupportReportConfig.ResolveUrl();
            string uploadToken = SupportReportConfig.ResolveToken();
            if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(uploadToken))
            {
                PostSupportReportError("CONFIG", "The bug report endpoint is not configured.");
                return;
            }

            if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            {
                PostSupportReportError("URL", "The bug report endpoint URL is not valid.");
                return;
            }

            string? devicesReport = includeDiagnostics
                ? await Task.Run(BuildDevicesReport).ConfigureAwait(true)
                : null;
            if (_disposed)
            {
                return;
            }

            string? path;
            _supportExporting = true;
            try
            {
                path = await CreateSupportBundleAsync(
                    includeDiagnostics: includeDiagnostics,
                    description: trimmedNote,
                    devicesReport: devicesReport).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not create the support bundle.", ex);
                path = null;
            }
            finally
            {
                _supportExporting = false;
            }

            if (path == null)
            {
                PostSupportReportError("BUNDLE", "Could not create the bug report bundle.");
                return;
            }

            var info = new FileInfo(path);
            if (info.Length > SupportReportMaxBytes)
            {
                PostSupportReportError(
                    "SIZE",
                    $"The bug report bundle is too large ({DiskSpace.Format(info.Length)}; limit is 10 MB).");
                return;
            }

            string version = typeof(WebShellController).Assembly.GetName().Version?.ToString(3) ?? "dev";
            string payload = JsonSerializer.Serialize(new
            {
                token = uploadToken,
                version,
                note = trimmedNote,
                includeDiagnostics,
                data = Convert.ToBase64String(await File.ReadAllBytesAsync(path)),
            });

            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            request.Headers.UserAgent.ParseAdd("Ruse/" + version);
            using var response = await SupportUploadClient.SendAsync(request);
            string responseText = await response.Content.ReadAsStringAsync();
            bool ok = false;
            string reportId = "";
            string uploadError = "";
            try
            {
                using var document = JsonDocument.Parse(responseText);
                var root = document.RootElement;
                ok = root.TryGetProperty("ok", out var okProperty) &&
                    okProperty.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.String)
                {
                    reportId = idProperty.GetString() ?? "";
                }

                if (root.TryGetProperty("error", out var errorProperty) && errorProperty.ValueKind == JsonValueKind.String)
                {
                    uploadError = errorProperty.GetString() ?? "";
                }
            }
            catch
            {
            }

            if (!response.IsSuccessStatusCode || !ok)
            {
                string message = string.IsNullOrWhiteSpace(uploadError)
                    ? $"HTTP {(int)response.StatusCode}"
                    : uploadError;
                string code = response.IsSuccessStatusCode
                    ? "UPLOAD"
                    : $"HTTP_{(int)response.StatusCode}";
                PostSupportReportError(code, message);
                return;
            }

            Post("support-report-result", new { ok = true, id = reportId });
            SetStatus("Bug report sent.");
            ShowToast("Bug report sent.");
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("Could not send the bug report.", ex);
            PostSupportReportError("NETWORK", ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not send the bug report.", ex);
            PostSupportReportError("EXCEPTION", ex.Message);
        }
    }

    private void PostSupportReportError(string code, string message)
    {
        Post("support-report-result", new { ok = false, code, error = message });
        SetStatus($"Bug report failed [{code}]: {message}", isError: true);
    }

    private void OpenSupportFolder()
    {
        try
        {
            RuseFolders.OpenFolder(Path.Combine(DisplayedUserFolder, "Support"));
        }
        catch (Exception ex)
        {
            SetStatus("Could not open the support folder: " + ex.Message, isError: true);
        }
    }
}
