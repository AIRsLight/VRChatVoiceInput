using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valve.VR;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Input;

namespace VRChatVoiceInput.Windows.Input;

public sealed class SteamVrPttInput : IPushToTalkInput, IDisposable
{
    public const string DefaultActionPath = "/actions/voiceinput/in/ptt";
    private const int ReleaseDebounceMilliseconds = 60;
    private readonly SteamVrInputConfiguration _configuration;
    private readonly Func<bool> _isActive;
    private bool _isPressed;
    private bool _started;
    private bool _wasPhysicallyPressed;
    private long? _releaseCandidateTimestamp;

    public SteamVrPttInput(SteamVrInputConfiguration configuration, Func<bool>? isActive = null)
    {
        if (!string.Equals(configuration.ActionPath, DefaultActionPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SteamVR actionPath must be '{DefaultActionPath}' for the bundled action manifest.");
        }

        if (configuration.PollIntervalMs is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                "SteamVR pollIntervalMs must be between 1 and 1000.");
        }

        _configuration = configuration;
        _isActive = isActive ?? (() => true);
    }

    public event EventHandler<PushToTalkChangedEventArgs>? StateChanged;

    internal int PollIntervalMs => _configuration.PollIntervalMs;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_started)
        {
            throw new InvalidOperationException("SteamVR PTT input is already started.");
        }

        _started = true;
        try
        {
            SteamVrActionRuntime.Register(this);
        }
        catch
        {
            _started = false;
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        await SteamVrActionRuntime.UnregisterAsync(this, cancellationToken);
        ResetPhysicalState();
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    public static SteamVrRuntimeStatus GetRuntimeStatus() => SteamVrActionRuntime.GetStatus();

    public static Task OpenBindingsAsync(CancellationToken cancellationToken = default) =>
        SteamVrActionRuntime.OpenBindingsAsync(cancellationToken);

    internal void UpdatePhysicalState(bool physicallyPressed)
    {
        if (physicallyPressed)
        {
            _releaseCandidateTimestamp = null;
            if (!_isPressed && !_wasPhysicallyPressed && _isActive())
            {
                SetPressed(true);
            }

            _wasPhysicallyPressed = true;
            return;
        }

        _wasPhysicallyPressed = false;
        if (!_isPressed)
        {
            _releaseCandidateTimestamp = null;
            return;
        }

        var now = Environment.TickCount64;
        _releaseCandidateTimestamp ??= now;
        if (now - _releaseCandidateTimestamp.Value >= ReleaseDebounceMilliseconds)
        {
            _releaseCandidateTimestamp = null;
            SetPressed(false);
        }
    }

    internal void ResetPhysicalState()
    {
        _releaseCandidateTimestamp = null;
        _wasPhysicallyPressed = false;
        SetPressed(false);
    }

    private void SetPressed(bool pressed)
    {
        if (_isPressed == pressed)
        {
            return;
        }

        _isPressed = pressed;
        StateChanged?.Invoke(this, new PushToTalkChangedEventArgs(pressed, DateTimeOffset.UtcNow));
    }
}

public sealed record SteamVrRuntimeStatus(bool RuntimeInstalled, bool Connected, string Message);

internal static class SteamVrActionRuntime
{
    private const string ActionSetPath = "/actions/voiceinput";
    private const string ApplicationKey = "io.vrchatvoiceinput.desktop";
    private const int ReconnectDelayMs = 2000;
    private static readonly object LifecycleSync = new();
    private static readonly object ApiSync = new();
    private static readonly List<SteamVrPttInput> Subscribers = [];
    private static CancellationTokenSource? _loopCancellation;
    private static Task? _loopTask;
    private static bool _connected;
    private static bool _openVrInitialized;
    private static string _statusMessage = "SteamVR is not connected.";
    private static ulong _actionHandle;
    private static ulong _actionSetHandle;

    public static void Register(SteamVrPttInput input)
    {
        lock (LifecycleSync)
        {
            if (_loopCancellation?.IsCancellationRequested == true)
            {
                throw new InvalidOperationException("SteamVR input is still stopping.");
            }

            Subscribers.Add(input);
            if (_loopTask is null)
            {
                _loopCancellation = new CancellationTokenSource();
                _loopTask = Task.Run(() => PollAsync(_loopCancellation.Token));
            }
        }
    }

    public static async Task UnregisterAsync(SteamVrPttInput input, CancellationToken cancellationToken)
    {
        Task? stoppingTask = null;
        CancellationTokenSource? stoppingCancellation = null;
        lock (LifecycleSync)
        {
            Subscribers.Remove(input);
            if (Subscribers.Count == 0 && _loopTask is not null)
            {
                stoppingTask = _loopTask;
                stoppingCancellation = _loopCancellation;
                stoppingCancellation?.Cancel();
            }
        }

        if (stoppingTask is null)
        {
            return;
        }

        try
        {
            await stoppingTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (stoppingCancellation?.IsCancellationRequested == true)
        {
        }

        lock (LifecycleSync)
        {
            if (ReferenceEquals(_loopTask, stoppingTask))
            {
                _loopTask = null;
                _loopCancellation?.Dispose();
                _loopCancellation = null;
            }
        }
    }

    public static SteamVrRuntimeStatus GetStatus()
    {
        var installed = false;
        try
        {
            lock (ApiSync)
            {
                installed = OpenVR.IsRuntimeInstalled();
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            return new SteamVrRuntimeStatus(
                false,
                false,
                exception is BadImageFormatException
                    ? "SteamVR input is only available in the win-x64 build."
                    : "The bundled OpenVR runtime library is missing.");
        }

        lock (LifecycleSync)
        {
            return new SteamVrRuntimeStatus(installed, _connected, _statusMessage);
        }
    }

    public static Task OpenBindingsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (ApiSync)
        {
            if (!_connected || _actionSetHandle == OpenVR.k_ulInvalidActionSetHandle)
            {
                throw new InvalidOperationException(
                    "SteamVR input is not connected. Start SteamVR, save a SteamVR profile, and try again.");
            }

            var error = OpenVR.Input.OpenBindingUI(
                ApplicationKey,
                _actionSetHandle,
                OpenVR.k_ulInvalidInputValueHandle,
                bShowOnDesktop: true);
            EnsureInputSuccess(error, "open the SteamVR binding UI");
        }

        return Task.CompletedTask;
    }

    private static async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_connected)
                    {
                        InitializeOpenVr();
                    }

                    if (ConsumeRuntimeQuitEvent())
                    {
                        ResetSubscribers();
                        ShutdownOpenVr("SteamVR exited. Waiting for it to restart.");
                        await Task.Delay(ReconnectDelayMs, cancellationToken);
                        continue;
                    }

                    var subscribers = GetSubscribers();
                    var pressed = ReadPttState();
                    foreach (var subscriber in subscribers)
                    {
                        subscriber.UpdatePhysicalState(pressed);
                    }

                    var delay = subscribers.Count == 0
                        ? 100
                        : subscribers.Min(subscriber => subscriber.PollIntervalMs);
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    ResetSubscribers();
                    ShutdownOpenVr(exception.Message);
                    await Task.Delay(ReconnectDelayMs, cancellationToken);
                }
            }
        }
        finally
        {
            ResetSubscribers();
            ShutdownOpenVr("SteamVR input stopped.");
        }
    }

    private static void InitializeOpenVr()
    {
        var assets = SteamVrManifestStore.EnsureExtracted();
        lock (ApiSync)
        {
            var initError = EVRInitError.None;
            _ = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Background);
            if (initError != EVRInitError.None)
            {
                throw new InvalidOperationException(
                    $"SteamVR connection failed: {OpenVR.GetStringForHmdError(initError)}");
            }

            _openVrInitialized = true;

            var applicationError = OpenVR.Applications.AddApplicationManifest(
                assets.ApplicationManifestPath,
                bTemporary: false);
            if (applicationError is not EVRApplicationError.None and not EVRApplicationError.AppKeyAlreadyExists)
            {
                throw new InvalidOperationException(
                    $"SteamVR application manifest registration failed: {applicationError}.");
            }

            applicationError = OpenVR.Applications.IdentifyApplication(
                unchecked((uint)Environment.ProcessId),
                ApplicationKey);
            if (applicationError != EVRApplicationError.None)
            {
                throw new InvalidOperationException(
                    $"SteamVR application identification failed: {applicationError}.");
            }

            EnsureInputSuccess(
                OpenVR.Input.SetActionManifestPath(assets.ActionManifestPath),
                "load the SteamVR action manifest");

            _actionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
            EnsureInputSuccess(
                OpenVR.Input.GetActionSetHandle(ActionSetPath, ref _actionSetHandle),
                "resolve the SteamVR action set");

            _actionHandle = OpenVR.k_ulInvalidActionHandle;
            EnsureInputSuccess(
                OpenVR.Input.GetActionHandle(SteamVrPttInput.DefaultActionPath, ref _actionHandle),
                "resolve the SteamVR PTT action");
        }

        SetStatus(true, "SteamVR action input is connected.");
    }

    private static bool ReadPttState()
    {
        lock (ApiSync)
        {
            var actionSets = new[]
            {
                new VRActiveActionSet_t
                {
                    ulActionSet = _actionSetHandle,
                    ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                    ulSecondaryActionSet = OpenVR.k_ulInvalidActionSetHandle,
                    nPriority = 0
                }
            };
            EnsureInputSuccess(
                OpenVR.Input.UpdateActionState(
                    actionSets,
                    unchecked((uint)Marshal.SizeOf<VRActiveActionSet_t>())),
                "update SteamVR actions");

            var data = new InputDigitalActionData_t();
            EnsureInputSuccess(
                OpenVR.Input.GetDigitalActionData(
                    _actionHandle,
                    ref data,
                    unchecked((uint)Marshal.SizeOf<InputDigitalActionData_t>()),
                    OpenVR.k_ulInvalidInputValueHandle),
                "read the SteamVR PTT action");
            return data.bActive && data.bState;
        }
    }

    private static bool ConsumeRuntimeQuitEvent()
    {
        lock (ApiSync)
        {
            var systemEvent = new VREvent_t();
            while (OpenVR.System.PollNextEvent(
                       ref systemEvent,
                       unchecked((uint)Marshal.SizeOf<VREvent_t>())))
            {
                if (IsRuntimeQuitEvent((EVREventType)systemEvent.eventType))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal static bool IsRuntimeQuitEvent(EVREventType eventType) =>
        eventType == EVREventType.VREvent_Quit;

    private static void ShutdownOpenVr(string message)
    {
        lock (ApiSync)
        {
            if (_openVrInitialized)
            {
                OpenVR.Shutdown();
                _openVrInitialized = false;
            }

            _actionHandle = OpenVR.k_ulInvalidActionHandle;
            _actionSetHandle = OpenVR.k_ulInvalidActionSetHandle;
        }

        SetStatus(false, message);
    }

    private static IReadOnlyList<SteamVrPttInput> GetSubscribers()
    {
        lock (LifecycleSync)
        {
            return Subscribers.ToArray();
        }
    }

    private static void ResetSubscribers()
    {
        foreach (var subscriber in GetSubscribers())
        {
            subscriber.ResetPhysicalState();
        }
    }

    private static void SetStatus(bool connected, string message)
    {
        lock (LifecycleSync)
        {
            _connected = connected;
            _statusMessage = message;
        }
    }

    private static void EnsureInputSuccess(EVRInputError error, string operation)
    {
        if (error != EVRInputError.None)
        {
            throw new InvalidOperationException($"Failed to {operation}: {error}.");
        }
    }
}

internal static class SteamVrManifestStore
{
    private static readonly string[] AssetNames =
    [
        "voiceinput_actions.json",
        "bindings_generic.json",
        "bindings_vive_controller.json",
        "bindings_knuckles.json",
        "bindings_oculus_touch.json"
    ];

    public static SteamVrManifestPaths EnsureExtracted()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRChatVoiceInput",
            "SteamVR");
        Directory.CreateDirectory(directory);

        var assembly = typeof(SteamVrPttInput).Assembly;
        foreach (var assetName in AssetNames)
        {
            var resourceName = $"VRChatVoiceInput.Windows.SteamVR.{assetName}";
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded SteamVR asset '{assetName}' is missing.");
            using var destination = File.Create(Path.Combine(directory, assetName));
            source.CopyTo(destination);
        }

        var actionManifestPath = Path.Combine(directory, "voiceinput_actions.json");
        var applicationManifestPath = Path.Combine(directory, "voiceinput.vrmanifest");
        var executablePath = ResolveApplicationExecutable();
        var applicationManifest = new Dictionary<string, object?>
        {
            ["source"] = "builtin",
            ["applications"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["app_key"] = "io.vrchatvoiceinput.desktop",
                    ["launch_type"] = "binary",
                    ["binary_path_windows"] = executablePath,
                    ["working_directory"] = Path.GetDirectoryName(executablePath),
                    ["action_manifest_path"] = actionManifestPath,
                    ["is_dashboard_overlay"] = false,
                    ["strings"] = new Dictionary<string, object?>
                    {
                        ["en_us"] = new Dictionary<string, string>
                        {
                            ["name"] = "VRChat Voice Input",
                            ["description"] = "Background push-to-talk input for speech recognition"
                        },
                        ["zh_cn"] = new Dictionary<string, string>
                        {
                            ["name"] = "VRChat 语音输入",
                            ["description"] = "用于语音识别的后台按住说话输入"
                        }
                    }
                }
            }
        };
        File.WriteAllText(
            applicationManifestPath,
            JsonSerializer.Serialize(applicationManifest, new JsonSerializerOptions { WriteIndented = true }));

        return new SteamVrManifestPaths(actionManifestPath, applicationManifestPath);
    }

    private static string ResolveApplicationExecutable()
    {
        var appExecutable = Path.Combine(AppContext.BaseDirectory, "VRChatVoiceInput.App.exe");
        return File.Exists(appExecutable)
            ? appExecutable
            : Environment.ProcessPath
              ?? throw new InvalidOperationException("The current executable path is unavailable.");
    }
}

internal sealed record SteamVrManifestPaths(string ActionManifestPath, string ApplicationManifestPath);
