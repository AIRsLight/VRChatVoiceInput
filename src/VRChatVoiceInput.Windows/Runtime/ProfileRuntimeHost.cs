using VRChatVoiceInput.Core.Asr;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Input;
using VRChatVoiceInput.Core.Output;
using VRChatVoiceInput.Core.Sessions;
using VRChatVoiceInput.Windows.Audio;
using VRChatVoiceInput.Windows.Input;
using VRChatVoiceInput.Windows.Output;

namespace VRChatVoiceInput.Windows.Runtime;

public sealed class ProfileRuntimeHost : IAsyncDisposable
{
    private readonly AppConfiguration _configuration;
    private readonly ApplicationProfileResolver _resolver;
    private readonly string? _profileOverride;
    private readonly string? _providerOverride;
    private readonly List<ProfileRuntime> _runtimes = new();
    private readonly Dictionary<string, IAsrProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Task> _activeTransitions = new();
    private readonly object _sync = new();
    private ProfileRuntime? _activeRuntime;
    private bool _disposed;

    public ProfileRuntimeHost(
        AppConfiguration configuration,
        string? profileOverride = null,
        string? providerOverride = null)
    {
        _configuration = configuration;
        _profileOverride = profileOverride;
        _providerOverride = providerOverride;
        var profiles = configuration.GetEffectiveProfiles();
        _resolver = new ApplicationProfileResolver(profiles, configuration.GetEffectiveDefaultProfileId());

        var enabledProfiles = profileOverride is null
            ? profiles.Where(profile => profile.Enabled)
            : [_resolver.GetById(profileOverride)];
        foreach (var profile in enabledProfiles)
        {
            _runtimes.Add(CreateRuntime(profile));
        }
    }

    public event EventHandler<RuntimeLogEventArgs>? LogReceived;

    public event EventHandler<RuntimeStateChangedEventArgs>? StateChanged;

    public bool IsRunning { get; private set; }

    public long ExternalProviderWorkingSetBytes => _providers.Values
        .OfType<IExternalAsrProviderMetrics>()
        .Sum(provider => provider.WorkingSetBytes);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("Profile runtime is already running.");
        }

        try
        {
            foreach (var runtime in _runtimes)
            {
                runtime.Input.StateChanged += runtime.StateChangedHandler;
                await runtime.Input.StartAsync(cancellationToken);
                Report(
                    "ready",
                    $"Profile '{runtime.Profile.Id}' ready: {DescribeInput(runtime.Profile.Input)} -> " +
                    $"{runtime.Provider.Id} -> {runtime.Profile.Output.Mode}",
                    runtime.Profile.Id);
                if (runtime.Profile.Recognition.Hotwords.Count > 0 &&
                    !runtime.Provider.Capabilities.HasFlag(AsrProviderCapabilities.TerminologyHints))
                {
                    Report(
                        "warning",
                        $"Profile '{runtime.Profile.Id}' has {runtime.Profile.Recognition.Hotwords.Count} hotwords, " +
                        $"but provider '{runtime.Provider.Id}' does not implement them yet.",
                        runtime.Profile.Id);
                }

                if (runtime.Profile.Recognition.StreamingEnabled &&
                    !string.Equals(runtime.Profile.Output.Mode, "vrchat-osc", StringComparison.OrdinalIgnoreCase))
                {
                    Report(
                        "info",
                        $"Profile '{runtime.Profile.Id}' streams recognition internally and sends joined final text to " +
                        $"'{runtime.Profile.Output.Mode}'.",
                        runtime.Profile.Id);
                }

                if (runtime.Profile.Recognition.StreamingEnabled &&
                    string.Equals(runtime.Profile.Output.Mode, "vrchat-osc", StringComparison.OrdinalIgnoreCase) &&
                    !runtime.Profile.Output.VrChat.SendImmediately)
                {
                    Report(
                        "warning",
                        $"Profile '{runtime.Profile.Id}' has streaming recognition enabled, but OSC immediate sending is off; " +
                        "only the joined final text will be placed in the Chatbox input.",
                        runtime.Profile.Id);
                }
            }

            IsRunning = true;
            StateChanged?.Invoke(this, new RuntimeStateChangedEventArgs(true));
            Report("started", "Foreground application profile routing is active.");
        }
        catch
        {
            await StopInputsAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await StopInputsAsync(cancellationToken);

        Task[] transitions;
        lock (_sync)
        {
            transitions = _activeTransitions.ToArray();
        }

        await Task.WhenAll(transitions).WaitAsync(cancellationToken);
        foreach (var runtime in _runtimes)
        {
            await runtime.Coordinator.CancelAsync(cancellationToken);
            runtime.Dispose();
        }

        foreach (var provider in _providers.Values)
        {
            (provider as IDisposable)?.Dispose();
        }
        _providers.Clear();

        _disposed = true;
        var wasRunning = IsRunning;
        IsRunning = false;
        if (wasRunning)
        {
            StateChanged?.Invoke(this, new RuntimeStateChangedEventArgs(false));
            Report("stopped", "Profile runtime stopped.");
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    private async Task StopInputsAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in _runtimes)
        {
            runtime.Input.StateChanged -= runtime.StateChangedHandler;
            await runtime.Input.StopAsync(cancellationToken);
        }
    }

    private ProfileRuntime CreateRuntime(ApplicationProfileConfiguration profile)
    {
        bool IsActive() => IsProfileActive(profile);

        IPushToTalkInput input = profile.Input.Mode.ToLowerInvariant() switch
        {
            "keyboard" => new GlobalKeyboardPttInput(profile.Input.Keyboard, IsActive),
            "mouse" => new GlobalMousePttInput(profile.Input.Mouse, IsActive),
            "xinput" => new XInputGamepadPttInput(profile.Input.Gamepad, IsActive),
            "steamvr" => new SteamVrPttInput(profile.Input.SteamVr, IsActive),
            _ => throw new InvalidOperationException(
                $"Unsupported input mode '{profile.Input.Mode}' in profile '{profile.Id}'.")
        };
        var providerId = _providerOverride ?? profile.Recognition.Provider;
        if (!_providers.TryGetValue(providerId, out var provider))
        {
            provider = _profileOverride is null
                ? new DeferredAsrProvider(
                    providerId,
                    AsrProviderFactory.GetCapabilities(providerId),
                    () => AsrProviderFactory.Create(_configuration.Asr, providerId))
                : AsrProviderFactory.Create(_configuration.Asr, providerId);
            _providers.Add(providerId, provider);
        }
        if (profile.Recognition.StreamingEnabled &&
            !provider.Capabilities.HasFlag(AsrProviderCapabilities.SegmentedStreaming))
        {
            throw new NotSupportedException(
                $"ASR provider '{provider.Id}' does not support streaming recognition.");
        }
        var output = CreateOutput(profile.Output);
        var recorder = new WasapiAudioRecorder(new AudioConfiguration
        {
            DeviceId = profile.Audio.DeviceId ?? _configuration.Audio.DeviceId,
            MinimumDurationMs = profile.Audio.MinimumDurationMs ?? _configuration.Audio.MinimumDurationMs
        });

        var recognitionOptions = new RecognitionOptions
        {
            TerminologyHints = profile.Recognition.Hotwords.Count > 0 &&
                provider.Capabilities.HasFlag(AsrProviderCapabilities.TerminologyHints)
                    ? profile.Recognition.Hotwords
                    : Array.Empty<string>(),
            Language = profile.Recognition.Language
        };

        var coordinator = new PttSessionCoordinator(
            recorder,
            provider,
            output,
            recognitionOptions,
            profile.Recognition.StreamingEnabled ? _configuration.Asr.Streaming : null);
        coordinator.StatusChanged += (_, status) =>
            Report(
                status.Code,
                status.Message,
                profile.Id,
                status.RecognitionDurationMilliseconds);

        ProfileRuntime? runtime = null;
        EventHandler<PushToTalkChangedEventArgs> handler = (_, eventArgs) =>
            HandleStateChanged(runtime!, eventArgs);
        runtime = new ProfileRuntime(profile, input, provider, output, recorder, coordinator, handler);
        return runtime;
    }

    private void HandleStateChanged(ProfileRuntime runtime, PushToTalkChangedEventArgs eventArgs)
    {
        lock (_sync)
        {
            if (eventArgs.IsPressed)
            {
                if (_activeRuntime is not null || !IsProfileActive(runtime.Profile))
                {
                    return;
                }

                _activeRuntime = runtime;
            }
            else if (!ReferenceEquals(_activeRuntime, runtime))
            {
                return;
            }
        }

        var transition = runtime.Coordinator.HandlePushToTalkAsync(eventArgs.IsPressed);
        lock (_sync)
        {
            _activeTransitions.Add(transition);
        }

        _ = ObserveTransitionAsync(runtime, transition, completesSession: !eventArgs.IsPressed);
    }

    private async Task ObserveTransitionAsync(ProfileRuntime runtime, Task transition, bool completesSession)
    {
        try
        {
            await transition;
        }
        catch (Exception exception)
        {
            Report("error", exception.Message, runtime.Profile.Id);
            completesSession = true;
        }
        finally
        {
            lock (_sync)
            {
                _activeTransitions.Remove(transition);
                if (completesSession && ReferenceEquals(_activeRuntime, runtime))
                {
                    _activeRuntime = null;
                }
            }
        }
    }

    private bool IsProfileActive(ApplicationProfileConfiguration profile)
    {
        if (_profileOverride is not null)
        {
            return string.Equals(profile.Id, _profileOverride, StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var application = ForegroundApplicationInspector.GetCurrent();
            var selected = _resolver.Resolve(application.ProcessName);
            return string.Equals(profile.Id, selected.Id, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return string.Equals(
                profile.Id,
                _configuration.GetEffectiveDefaultProfileId(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Report(
        string code,
        string message,
        string? profileId = null,
        double? recognitionDurationMilliseconds = null) =>
        LogReceived?.Invoke(
            this,
            new RuntimeLogEventArgs(
                DateTimeOffset.Now,
                code,
                message,
                profileId,
                recognitionDurationMilliseconds));

    private static ITextOutput CreateOutput(OutputConfiguration configuration) =>
        configuration.Mode.ToLowerInvariant() switch
        {
            "captured-window" => new CapturedWindowTextOutput(configuration.Windows),
            "vrchat-osc" => new VrChatOscOutput(configuration.VrChat),
            _ => throw new InvalidOperationException($"Unsupported output mode '{configuration.Mode}'.")
        };

    private static string DescribeInput(InputConfiguration configuration) =>
        configuration.Mode.ToLowerInvariant() switch
        {
            "keyboard" => $"keyboard [{string.Join("+", GetKeyboardKeys(configuration.Keyboard).Select(key => $"0x{key:X2}"))}]",
            "mouse" => $"mouse {MousePttButtons.Normalize(configuration.Mouse.Button)}",
            "xinput" => $"XInput mask 0x{configuration.Gamepad.ButtonMask:X4} on controller {configuration.Gamepad.UserIndex}",
            "steamvr" => $"SteamVR action {configuration.SteamVr.ActionPath}",
            _ => configuration.Mode
        };

    private static IReadOnlyList<int> GetKeyboardKeys(KeyboardInputConfiguration configuration) =>
        configuration.VirtualKeys.Count > 0 ? configuration.VirtualKeys : [configuration.VirtualKey];

    private sealed record ProfileRuntime(
        ApplicationProfileConfiguration Profile,
        IPushToTalkInput Input,
        IAsrProvider Provider,
        ITextOutput Output,
        WasapiAudioRecorder Recorder,
        PttSessionCoordinator Coordinator,
        EventHandler<PushToTalkChangedEventArgs> StateChangedHandler) : IDisposable
    {
        public void Dispose()
        {
            (Input as IDisposable)?.Dispose();
            (Output as IDisposable)?.Dispose();
            Recorder.Dispose();
        }
    }
}

public sealed record RuntimeLogEventArgs(
    DateTimeOffset Timestamp,
    string Code,
    string Message,
    string? ProfileId,
    double? RecognitionDurationMilliseconds = null);

public sealed record RuntimeStateChangedEventArgs(bool IsRunning);
