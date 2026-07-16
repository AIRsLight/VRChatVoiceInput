using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VRChatVoiceInput.Core.Asr;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Output;
using VRChatVoiceInput.Windows.Audio;
using VRChatVoiceInput.Windows.Input;
using VRChatVoiceInput.Windows.Output;
using VRChatVoiceInput.Windows.Runtime;

namespace VRChatVoiceInput.App;

public sealed class RuntimeController : IAsyncDisposable
{
    private const int MaximumLogEntries = 300;
    private const string VrChatTemplateId = "vrchat";
    private const string VrChatDesktopProfileName = "VRCHAT Desktop";
    private const string VrChatDesktopTemplateId = "vrchat-desktop";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _microphoneTestGate = new(1, 1);
    private readonly List<RuntimeLogEventArgs> _logs = new();
    private readonly ModelDownloadService _modelDownloads = new();
    private ProfileRuntimeHost? _host;
    private MicrophoneLevelMonitor? _microphoneLevelMonitor;
    private string? _profileOverride;
    private double _recognitionDurationTotalMilliseconds;
    private long _recognitionCount;

    public RuntimeController(string configurationPath)
    {
        ConfigurationPath = Path.GetFullPath(configurationPath);
        MigrateLegacyProfileNames(ConfigurationPath);
        MigrateVrChatDefaultInput(ConfigurationPath);
        EnsureVrChatDesktopProfile(ConfigurationPath);
        _profileOverride = LoadConfiguration().GetEffectiveDefaultProfileId();
    }

    public event EventHandler<RuntimeLogEventArgs>? LogReceived;

    public event EventHandler<RuntimeStateChangedEventArgs>? StateChanged;

    public event EventHandler? ConfigurationChanged;

    internal event EventHandler<ModelDownloadProgress>? ModelDownloadProgressChanged
    {
        add => _modelDownloads.ProgressChanged += value;
        remove => _modelDownloads.ProgressChanged -= value;
    }

    internal event EventHandler<MicrophoneLevelsChangedEventArgs>? MicrophoneLevelsChanged;

    public string ConfigurationPath { get; }

    public bool IsRunning => _host?.IsRunning == true;

    public string? ProfileOverride => _profileOverride;

    public AppConfiguration LoadConfiguration() => AppConfiguration.Load(ConfigurationPath);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
            {
                return;
            }

            ProfileRuntimeHost? host = null;
            try
            {
                var configuration = LoadConfiguration();
                EnsureSelectedProvidersAvailable(configuration, _profileOverride);
                host = new ProfileRuntimeHost(configuration, _profileOverride);
                host.LogReceived += OnHostLogReceived;
                host.StateChanged += OnHostStateChanged;
                await host.StartAsync(cancellationToken);
                _host = host;
            }
            catch (Exception exception)
            {
                if (host is not null)
                {
                    host.LogReceived -= OnHostLogReceived;
                    host.StateChanged -= OnHostStateChanged;
                    try
                    {
                        await host.DisposeAsync();
                    }
                    catch (Exception disposalException)
                    {
                        AppFileLogger.Warning(
                            "runtime",
                            "Runtime cleanup after a failed start also failed.",
                            disposalException);
                    }
                }

                if (exception is not OperationCanceledException)
                {
                    AddHostLog("error", $"Runtime start failed: {exception.Message}", exception: exception);
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_host is null)
            {
                return;
            }

            var host = _host;
            _host = null;
            await host.StopAsync(cancellationToken);
            host.LogReceived -= OnHostLogReceived;
            host.StateChanged -= OnHostStateChanged;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public async Task SetProfileOverrideAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        var normalizedProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim();
        if (string.Equals(_profileOverride, normalizedProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (normalizedProfileId is not null)
        {
            var configuration = LoadConfiguration();
            var resolver = new ApplicationProfileResolver(
                configuration.GetEffectiveProfiles(),
                configuration.GetEffectiveDefaultProfileId());
            var profile = resolver.GetById(normalizedProfileId);
            if (!profile.Enabled)
            {
                throw new InvalidOperationException($"Profile '{normalizedProfileId}' is disabled.");
            }

            EnsureSelectedProvidersAvailable(configuration, normalizedProfileId);
        }

        var wasRunning = IsRunning;
        if (wasRunning)
        {
            await StopAsync(cancellationToken);
        }

        _profileOverride = normalizedProfileId;
        AddHostLog(
            "profile",
            normalizedProfileId is null
                ? "Automatic foreground application profile routing is active."
                : $"Runtime profile override changed to '{normalizedProfileId}'.",
            normalizedProfileId);

        if (wasRunning)
        {
            await StartAsync(cancellationToken);
        }
    }

    public async Task SaveConfigurationAsync(string json, CancellationToken cancellationToken = default)
    {
        var previousConfiguration = LoadConfiguration();
        var previousProfiles = previousConfiguration.GetEffectiveProfiles();
        var previousProfileOverride = _profileOverride;
        var configuration = AppConfiguration.Parse(json);
        var profiles = configuration.GetEffectiveProfiles();
        if (_profileOverride is not null && !profiles.Any(profile =>
                profile.Enabled && string.Equals(profile.Id, _profileOverride, StringComparison.OrdinalIgnoreCase)))
        {
            var previousEntry = previousProfiles
                .Select((profile, index) => (profile, index))
                .FirstOrDefault(entry => string.Equals(
                    entry.profile.Id,
                    _profileOverride,
                    StringComparison.OrdinalIgnoreCase));
            _profileOverride = profiles.Count == previousProfiles.Count &&
                               previousEntry.profile is not null &&
                               previousEntry.index < profiles.Count &&
                               profiles[previousEntry.index].Enabled
                ? profiles[previousEntry.index].Id
                : null;
        }
        EnsureSelectedProvidersAvailable(configuration, _profileOverride);
        var restartRuntime = IsRunning && !string.Equals(
            CreateRuntimeConfigurationFingerprint(previousConfiguration, previousProfileOverride),
            CreateRuntimeConfigurationFingerprint(configuration, _profileOverride),
            StringComparison.Ordinal);
        if (restartRuntime)
        {
            await StopAsync(cancellationToken);
        }

        var directory = Path.GetDirectoryName(ConfigurationPath)
            ?? throw new InvalidOperationException("Configuration directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = ConfigurationPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, FormatJson(json), cancellationToken);
        File.Move(temporaryPath, ConfigurationPath, overwrite: true);
        StartupRegistration.Apply(configuration.Application.StartWithWindows, ConfigurationPath);
        AddHostLog("saved", $"Configuration saved to {ConfigurationPath}.");
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);

        if (restartRuntime)
        {
            try
            {
                await StartAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddHostLog(
                    "error",
                    $"Configuration was saved, but the runtime could not restart: {exception.Message}",
                    exception: exception);
            }
        }
    }

    public object CreateSnapshot(string webViewVersion)
    {
        var configurationJson = File.ReadAllText(ConfigurationPath);
        var configuration = AppConfiguration.Parse(configurationJson);
        _ = configuration.GetEffectiveProfiles();
        IReadOnlyList<AudioDeviceInfo> microphones;
        try
        {
            microphones = WasapiAudioRecorder.ListCaptureDevices();
        }
        catch (Exception exception)
        {
            microphones = Array.Empty<AudioDeviceInfo>();
            AddHostLog("warning", $"Unable to enumerate microphones: {exception.Message}");
        }

        RuntimeLogEventArgs[] logs;
        object diagnostics;
        lock (_logs)
        {
            logs = _logs.ToArray();
            diagnostics = CreateDiagnosticMetrics();
        }

        return new
        {
            configuration = JsonNode.Parse(configurationJson),
            runtime = new { isRunning = IsRunning, profileOverride = _profileOverride },
            environment = new
            {
                configurationPath = ConfigurationPath,
                logFilePath = AppFileLogger.CurrentLogPath,
                applicationVersion = ApplicationVersion.Current,
                webViewVersion
            },
            microphones,
            runningApplications = ForegroundApplicationInspector.ListApplications(),
            gpuDevices = VulkanDeviceInspector.ListDevices(),
            microphoneTest = new
            {
                isRunning = _microphoneLevelMonitor is not null,
                levels = _microphoneLevelMonitor?.CurrentLevels ?? Array.Empty<MicrophoneLevelInfo>()
            },
            providerStatuses = AsrProviderFactory.CheckAvailability(configuration.Asr),
            modelAssets = ModelDownloadCatalog.GetAssetStatuses(),
            modelDownload = _modelDownloads.Current,
            steamVr = SteamVrPttInput.GetRuntimeStatus(),
            diagnostics,
            logs
        };
    }

    public object GetDiagnosticMetrics()
    {
        lock (_logs)
        {
            return CreateDiagnosticMetrics();
        }
    }

    internal RuntimeDiagnosticSnapshot GetRuntimeDiagnosticSnapshot()
    {
        lock (_logs)
        {
            using var process = Process.GetCurrentProcess();
            return new RuntimeDiagnosticSnapshot(
                process.WorkingSet64 + (_host?.ExternalProviderWorkingSetBytes ?? 0),
                _logs.ToArray());
        }
    }

    public Task<GamepadButtonCapture> CaptureGamepadButtonAsync(
        CancellationToken cancellationToken = default) =>
        XInputGamepadPttInput.CaptureButtonAsync(
            TimeSpan.FromSeconds(10),
            cancellationToken);

    public Task<KeyboardChordCapture> CaptureKeyboardChordAsync(
        CancellationToken cancellationToken = default) =>
        GlobalKeyboardChordCapture.CaptureAsync(
            TimeSpan.FromSeconds(10),
            cancellationToken);

    public Task<MouseButtonCapture> CaptureMouseButtonAsync(
        CancellationToken cancellationToken = default) =>
        GlobalMouseButtonCapture.CaptureAsync(
            TimeSpan.FromSeconds(10),
            cancellationToken);

    public IReadOnlyList<RunningApplicationInfo> ListRunningApplications() =>
        ForegroundApplicationInspector.ListApplications();

    public SteamVrRuntimeStatus GetSteamVrStatus() =>
        SteamVrPttInput.GetRuntimeStatus();

    public IReadOnlyList<GpuDeviceInfo> ListGpuDevices() =>
        VulkanDeviceInspector.ListDevices();

    public async Task<object> StartMicrophoneTestAsync(CancellationToken cancellationToken = default)
    {
        await _microphoneTestGate.WaitAsync(cancellationToken);
        try
        {
            if (_microphoneLevelMonitor is not null)
            {
                return new { isRunning = true, levels = _microphoneLevelMonitor.CurrentLevels };
            }

            var monitor = new MicrophoneLevelMonitor();
            monitor.LevelsChanged += OnMicrophoneLevelsChanged;
            _microphoneLevelMonitor = monitor;
            try
            {
                var levels = await monitor.StartAsync(cancellationToken);
                return new { isRunning = true, levels };
            }
            catch
            {
                _microphoneLevelMonitor = null;
                monitor.LevelsChanged -= OnMicrophoneLevelsChanged;
                await monitor.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _microphoneTestGate.Release();
        }
    }

    public async Task StopMicrophoneTestAsync(CancellationToken cancellationToken = default)
    {
        await _microphoneTestGate.WaitAsync(cancellationToken);
        try
        {
            var monitor = _microphoneLevelMonitor;
            if (monitor is null)
            {
                return;
            }

            _microphoneLevelMonitor = null;
            monitor.LevelsChanged -= OnMicrophoneLevelsChanged;
            await monitor.DisposeAsync();
        }
        finally
        {
            _microphoneTestGate.Release();
        }
    }

    internal async Task<object> DownloadModelsAsync(
        string providerId,
        string asrConfigurationJson,
        CancellationToken cancellationToken = default)
    {
        var configuration = JsonSerializer.Deserialize<AsrConfiguration>(
            asrConfigurationJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("ASR configuration is required.");
        var completed = await _modelDownloads.DownloadAsync(providerId, configuration, cancellationToken);
        return new
        {
            completed,
            providerStatuses = AsrProviderFactory.CheckAvailability(configuration),
            modelAssets = ModelDownloadCatalog.GetAssetStatuses()
        };
    }

    internal async Task<object> DownloadModelAssetAsync(
        string assetId,
        string asrConfigurationJson,
        CancellationToken cancellationToken = default)
    {
        var configuration = JsonSerializer.Deserialize<AsrConfiguration>(
            asrConfigurationJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("ASR configuration is required.");
        var package = ModelDownloadCatalog.ResolvePackage(assetId);
        var isRuntimePackage = package.ComponentId.StartsWith("runtime-", StringComparison.Ordinal);
        var restartRuntime = isRuntimePackage && IsRunning;
        if (restartRuntime)
        {
            AddHostLog("download", $"Stopping the runtime to update {package.Variant} {package.ComponentId}.");
            await StopAsync(cancellationToken);
        }

        bool completed;
        try
        {
            completed = await _modelDownloads.DownloadAssetAsync(assetId, cancellationToken);
            AddHostLog("download", $"Downloaded and verified '{assetId}'.");
        }
        catch (Exception exception)
        {
            AddHostLog("error", $"Download '{assetId}' failed: {exception.Message}", exception: exception);
            if (restartRuntime)
            {
                await TryRestartAfterRuntimeDownloadAsync(assetId);
            }
            throw;
        }

        if (restartRuntime)
        {
            await TryRestartAfterRuntimeDownloadAsync(assetId);
        }
        return new
        {
            completed,
            providerStatuses = AsrProviderFactory.CheckAvailability(configuration),
            modelAssets = ModelDownloadCatalog.GetAssetStatuses()
        };
    }

    private async Task TryRestartAfterRuntimeDownloadAsync(string assetId)
    {
        try
        {
            await StartAsync();
            AddHostLog("download", $"Runtime restarted after '{assetId}'.");
        }
        catch (Exception exception)
        {
            AddHostLog(
                "error",
                $"Runtime download finished, but the runtime could not restart: {exception.Message}",
                exception: exception);
        }
    }

    internal bool CancelModelDownload() => _modelDownloads.Cancel();

    public async Task SendOutputTestAsync(
        string profileId,
        string text,
        int? targetProcessId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Test text cannot be empty.");
        }

        var configuration = LoadConfiguration();
        var resolver = new ApplicationProfileResolver(
            configuration.GetEffectiveProfiles(),
            configuration.GetEffectiveDefaultProfileId());
        var profile = resolver.GetById(profileId);
        ITextOutput output = profile.Output.Mode.ToLowerInvariant() switch
        {
            "captured-window" => new CapturedWindowTextOutput(profile.Output.Windows),
            "vrchat-osc" => new VrChatOscOutput(profile.Output.VrChat),
            _ => throw new InvalidOperationException(
                $"Unsupported output mode '{profile.Output.Mode}' in profile '{profileId}'.")
        };

        try
        {
            TextOutputTarget target;
            if (string.Equals(profile.Output.Mode, "captured-window", StringComparison.OrdinalIgnoreCase))
            {
                if (targetProcessId is null)
                {
                    throw new InvalidOperationException(
                        "A target application is required for captured-window output testing.");
                }

                var application = ForegroundApplicationInspector.Activate(targetProcessId.Value);
                await Task.Delay(150, cancellationToken);
                var foreground = ForegroundApplicationInspector.GetCurrent();
                if (foreground.ProcessId != application.ProcessId)
                {
                    throw new InvalidOperationException(
                        $"Target application '{application.DisplayName}' did not become the foreground window.");
                }

                target = new TextOutputTarget(
                    "captured-window",
                    foreground.DisplayName,
                    foreground.WindowHandle,
                    foreground.ProcessId);
            }
            else
            {
                target = output.CaptureTarget();
            }

            await output.SendAsync(text, target, cancellationToken);
            AddHostLog(
                "output-test",
                $"Output test sent through profile '{profileId}' to {target.DisplayName}.",
                profileId);
        }
        finally
        {
            (output as IDisposable)?.Dispose();
        }
    }

    public async Task OpenSteamVrBindingsAsync(CancellationToken cancellationToken = default)
    {
        await SteamVrPttInput.OpenBindingsAsync(cancellationToken);
        AddHostLog("steamvr-bindings", "SteamVR controller bindings opened.");
    }

    public void AddHostLog(
        string code,
        string message,
        string? profileId = null,
        double? recognitionDurationMilliseconds = null,
        Exception? exception = null)
    {
        var source = profileId is null ? "runtime" : $"runtime:{profileId}";
        if (string.Equals(code, "error", StringComparison.OrdinalIgnoreCase))
        {
            AppFileLogger.Error(source, message, exception);
        }
        else if (string.Equals(code, "warning", StringComparison.OrdinalIgnoreCase))
        {
            AppFileLogger.Warning(source, message, exception);
        }
        else
        {
            AppFileLogger.Info(source, $"[{code}] {message}");
        }

        var entry = new RuntimeLogEventArgs(
            DateTimeOffset.Now,
            code,
            message,
            profileId,
            recognitionDurationMilliseconds);
        lock (_logs)
        {
            if (recognitionDurationMilliseconds is >= 0)
            {
                _recognitionDurationTotalMilliseconds += recognitionDurationMilliseconds.Value;
                _recognitionCount++;
            }

            _logs.Add(entry);
            if (_logs.Count > MaximumLogEntries)
            {
                _logs.RemoveRange(0, _logs.Count - MaximumLogEntries);
            }
        }

        LogReceived?.Invoke(this, entry);
    }

    public async ValueTask DisposeAsync()
    {
        _modelDownloads.Dispose();
        await StopMicrophoneTestAsync(CancellationToken.None);
        await StopAsync(CancellationToken.None);
        _microphoneTestGate.Dispose();
        _gate.Dispose();
    }

    private static string FormatJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void MigrateLegacyProfileNames(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        var profiles = root?["profiles"] as JsonObject;
        var items = profiles?["items"] as JsonArray;
        if (root is null || profiles is null || items is null)
        {
            return;
        }
        if (!items.OfType<JsonObject>().Any(item => item.ContainsKey("displayName")))
        {
            return;
        }

        var changed = false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamedProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OfType<JsonObject>())
        {
            var oldId = item["id"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(oldId))
            {
                continue;
            }

            var legacyName = item["displayName"]?.GetValue<string>()?.Trim();
            var baseName = string.IsNullOrWhiteSpace(legacyName) ? oldId : legacyName;
            var name = baseName;
            var suffix = 2;
            while (!names.Add(name))
            {
                name = $"{baseName} ({suffix++})";
            }

            renamedProfiles[oldId] = name;
            if (!string.Equals(oldId, name, StringComparison.Ordinal))
            {
                item["id"] = name;
                changed = true;
            }
            if (item.Remove("displayName"))
            {
                changed = true;
            }
        }

        var defaultProfileId = profiles["defaultProfileId"]?.GetValue<string>();
        if (defaultProfileId is not null &&
            renamedProfiles.TryGetValue(defaultProfileId, out var renamedDefault) &&
            !string.Equals(defaultProfileId, renamedDefault, StringComparison.Ordinal))
        {
            profiles["defaultProfileId"] = renamedDefault;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        var temporaryPath = path + ".migration.tmp";
        File.WriteAllText(temporaryPath, FormatJson(root.ToJsonString()));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void EnsureVrChatDesktopProfile(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        var items = root?["profiles"]?["items"] as JsonArray;
        if (root is null || items is null || items.OfType<JsonObject>().Any(item =>
                string.Equals(
                    item["id"]?.GetValue<string>(),
                    VrChatDesktopProfileName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    item["builtInTemplate"]?.GetValue<string>(),
                    VrChatDesktopTemplateId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var source = items.OfType<JsonObject>().FirstOrDefault(item => string.Equals(
                         item["id"]?.GetValue<string>(),
                         "VRChat",
                         StringComparison.OrdinalIgnoreCase))
                     ?? items.OfType<JsonObject>().FirstOrDefault(item => string.Equals(
                         item["output"]?["mode"]?.GetValue<string>(),
                         "vrchat-osc",
                         StringComparison.OrdinalIgnoreCase));
        var profile = source?.DeepClone().AsObject() ?? new JsonObject();
        profile["id"] = VrChatDesktopProfileName;
        profile["builtInTemplate"] = VrChatDesktopTemplateId;
        profile.Remove("displayName");
        profile["enabled"] = true;
        profile["builtIn"] = true;
        profile["audio"] ??= new JsonObject
        {
            ["deviceId"] = null,
            ["minimumDurationMs"] = null
        };
        profile["match"] = new JsonObject
        {
            ["processNames"] = new JsonArray { "VRChat.exe" }
        };

        var input = profile["input"] as JsonObject ?? new JsonObject();
        profile["input"] = input;
        input["mode"] = "keyboard";
        input["keyboard"] = new JsonObject
        {
            ["virtualKeys"] = new JsonArray { 0xA2 },
            ["suppressKey"] = false
        };
        input["mouse"] ??= new JsonObject
        {
            ["button"] = "x1",
            ["suppressButton"] = false
        };
        input["gamepad"] ??= new JsonObject
        {
            ["userIndex"] = 0,
            ["buttonMask"] = 0x1000,
            ["pollIntervalMs"] = 8
        };
        input["steamVr"] ??= new JsonObject
        {
            ["actionPath"] = "/actions/voiceinput/in/ptt",
            ["pollIntervalMs"] = 8
        };

        var recognition = profile["recognition"] as JsonObject ?? new JsonObject();
        profile["recognition"] = recognition;
        recognition["provider"] ??= root["asr"]?["provider"]?.DeepClone() ?? "paraformer-gguf";
        recognition["language"] ??= "auto";
        recognition["hotwords"] ??= new JsonArray();
        recognition["streamingEnabled"] ??= false;

        var output = profile["output"] as JsonObject ?? new JsonObject();
        profile["output"] = output;
        output["mode"] = "vrchat-osc";
        output["vrChat"] ??= root["vrChat"]?.DeepClone() ?? new JsonObject
        {
            ["host"] = "127.0.0.1",
            ["port"] = 9000,
            ["sendImmediately"] = true,
            ["maxChatboxCharacters"] = 144
        };

        items.Add(profile);
        var temporaryPath = path + ".built-in-profile.tmp";
        File.WriteAllText(temporaryPath, FormatJson(root.ToJsonString()));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void MigrateVrChatDefaultInput(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        var items = root?["profiles"]?["items"] as JsonArray;
        var profile = items?.OfType<JsonObject>().FirstOrDefault(item => string.Equals(
                              item["builtInTemplate"]?.GetValue<string>(),
                              VrChatTemplateId,
                              StringComparison.OrdinalIgnoreCase))
                      ?? items?.OfType<JsonObject>().FirstOrDefault(item =>
                          item["builtIn"]?.GetValue<bool>() == true &&
                          string.Equals(
                              item["id"]?.GetValue<string>(),
                              "VRChat",
                              StringComparison.OrdinalIgnoreCase));
        if (root is null || profile is null || string.Equals(
                profile["builtInTemplate"]?.GetValue<string>(),
                VrChatTemplateId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        profile["builtInTemplate"] = VrChatTemplateId;
        var input = profile["input"] as JsonObject;
        var keyboard = input?["keyboard"] as JsonObject;
        var virtualKeys = keyboard?["virtualKeys"] as JsonArray;
        var usesF8 = virtualKeys is { Count: 1 }
            ? virtualKeys[0]?.GetValue<int>() == 0x77
            : (virtualKeys is null or { Count: 0 }) &&
              (keyboard?["virtualKey"]?.GetValue<int>() ?? 0x77) == 0x77;
        var usesLegacyDefaultF8 = string.Equals(
                                      input?["mode"]?.GetValue<string>(),
                                      "keyboard",
                                      StringComparison.OrdinalIgnoreCase) &&
                                  keyboard?["suppressKey"]?.GetValue<bool>() != true &&
                                  usesF8;
        if (usesLegacyDefaultF8 && input is not null)
        {
            input["mode"] = "steamvr";
            var steamVr = input["steamVr"] as JsonObject ?? new JsonObject();
            input["steamVr"] = steamVr;
            steamVr["actionPath"] = "/actions/voiceinput/in/ptt";
            steamVr["pollIntervalMs"] ??= 8;
        }

        var temporaryPath = path + ".vrchat-input-migration.tmp";
        File.WriteAllText(temporaryPath, FormatJson(root.ToJsonString()));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void EnsureSelectedProvidersAvailable(AppConfiguration configuration, string? profileOverride)
    {
        var profiles = configuration.GetEffectiveProfiles();
        var selectedProfiles = profileOverride is null
            ? profiles.Where(profile => profile.Enabled)
            : profiles.Where(profile =>
                profile.Enabled && string.Equals(profile.Id, profileOverride, StringComparison.OrdinalIgnoreCase));
        var statuses = AsrProviderFactory.CheckAvailability(configuration.Asr)
            .ToDictionary(status => status.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var profile in selectedProfiles)
        {
            if (!statuses.TryGetValue(profile.Recognition.Provider, out var status))
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' selects unknown provider '{profile.Recognition.Provider}'.");
            }

            if (!status.Available)
            {
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' cannot use '{status.Id}' because required files are missing: " +
                    string.Join(", ", status.MissingFiles));
            }
        }
    }

    private static string CreateRuntimeConfigurationFingerprint(
        AppConfiguration configuration,
        string? profileOverride)
    {
        var profiles = configuration.GetEffectiveProfiles();
        var selectedProfiles = (profileOverride is null
                ? profiles.Where(profile => profile.Enabled)
                : profiles.Where(profile =>
                    profile.Enabled && string.Equals(profile.Id, profileOverride, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var providerConfigurations = selectedProfiles
            .Select(profile => profile.Recognition.Provider)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(providerId => providerId, StringComparer.OrdinalIgnoreCase)
            .Select(providerId => new
            {
                id = providerId,
                settings = GetProviderConfiguration(configuration.Asr, providerId)
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            configuration.Audio,
            defaultProfileId = profileOverride is null
                ? configuration.GetEffectiveDefaultProfileId()
                : null,
            profiles = selectedProfiles,
            providers = providerConfigurations,
            streaming = selectedProfiles.Any(profile => profile.Recognition.StreamingEnabled)
                ? configuration.Asr.Streaming
                : null
        });
    }

    private static object GetProviderConfiguration(AsrConfiguration configuration, string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "paraformer-gguf" => configuration.Paraformer,
            "sensevoice-gguf" => configuration.SenseVoice,
            "funasr-nano-gguf" => configuration.FunAsrNano,
            "qwen3-asr" => configuration.Qwen3Asr,
            "whisper-cpp" => configuration.WhisperCpp,
            _ => throw new InvalidOperationException($"Unknown ASR provider '{providerId}'.")
        };

    private object CreateDiagnosticMetrics()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            workingSetBytes = process.WorkingSet64 + (_host?.ExternalProviderWorkingSetBytes ?? 0),
            averageRecognitionMilliseconds = _recognitionCount == 0
                ? (double?)null
                : _recognitionDurationTotalMilliseconds / _recognitionCount,
            recognitionCount = _recognitionCount
        };
    }

    private void OnHostLogReceived(object? sender, RuntimeLogEventArgs entry) =>
        AddHostLog(
            entry.Code,
            entry.Message,
            entry.ProfileId,
            entry.RecognitionDurationMilliseconds);

    private void OnHostStateChanged(object? sender, RuntimeStateChangedEventArgs state) =>
        StateChanged?.Invoke(this, state);

    private void OnMicrophoneLevelsChanged(object? sender, MicrophoneLevelsChangedEventArgs eventArgs) =>
        MicrophoneLevelsChanged?.Invoke(this, eventArgs);
}

internal sealed record RuntimeDiagnosticSnapshot(
    long WorkingSetBytes,
    IReadOnlyList<RuntimeLogEventArgs> Logs);
