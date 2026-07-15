using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace VRChatVoiceInput.App;

internal sealed class WebMessageBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly CoreWebView2 _webView;
    private readonly RuntimeController _controller;
    private readonly Window _owner;
    private bool _disposed;

    public event EventHandler? UiReady;

    public WebMessageBridge(CoreWebView2 webView, RuntimeController controller, Window owner)
    {
        _webView = webView;
        _controller = controller;
        _owner = owner;
        _webView.WebMessageReceived += OnWebMessageReceived;
        _controller.LogReceived += OnLogReceived;
        _controller.StateChanged += OnStateChanged;
        _controller.ConfigurationChanged += OnConfigurationChanged;
        _controller.ModelDownloadProgressChanged += OnModelDownloadProgressChanged;
        _controller.MicrophoneLevelsChanged += OnMicrophoneLevelsChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _webView.WebMessageReceived -= OnWebMessageReceived;
        _controller.LogReceived -= OnLogReceived;
        _controller.StateChanged -= OnStateChanged;
        _controller.ConfigurationChanged -= OnConfigurationChanged;
        _controller.ModelDownloadProgressChanged -= OnModelDownloadProgressChanged;
        _controller.MicrophoneLevelsChanged -= OnMicrophoneLevelsChanged;
        _ = _controller.StopMicrophoneTestAsync();
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        BridgeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<BridgeRequest>(eventArgs.WebMessageAsJson, JsonOptions)
                ?? throw new InvalidOperationException("Bridge request is empty.");
            if (request.Version != 1 || string.IsNullOrWhiteSpace(request.Id))
            {
                throw new InvalidOperationException("Unsupported bridge request envelope.");
            }

            var payload = await DispatchAsync(request);
            Post(new BridgeResponse(1, request.Id, request.Type + ".result", true, payload, null));
        }
        catch (Exception exception)
        {
            var operation = request?.Type ?? "unknown";
            if (operation is not "runtime.start" and not "runtime.restart")
            {
                _controller.AddHostLog(
                    "error",
                    $"Operation '{operation}' failed: {exception.Message}",
                    exception: exception);
            }
            Post(new BridgeResponse(
                1,
                request?.Id ?? string.Empty,
                (request?.Type ?? "request") + ".result",
                false,
                null,
                new BridgeError("request_failed", exception.Message)));
        }
    }

    private async Task<object?> DispatchAsync(BridgeRequest request)
    {
        switch (request.Type)
        {
            case "snapshot.get":
                return _controller.CreateSnapshot(_webView.Environment.BrowserVersionString);
            case "ui.ready":
                UiReady?.Invoke(this, EventArgs.Empty);
                return new { ready = true };
            case "ui.error":
                {
                    var message = request.Payload.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString() ?? "Unknown web UI error."
                        : "Unknown web UI error.";
                    var stack = request.Payload.TryGetProperty("stack", out var stackElement)
                        ? stackElement.GetString()
                        : null;
                    _controller.AddHostLog(
                        "error",
                        string.IsNullOrWhiteSpace(stack)
                            ? $"Web UI error: {message}"
                            : $"Web UI error: {message} | {stack.ReplaceLineEndings(" ")}");
                    return new { logged = true };
                }
            case "configuration.save":
                {
                    var configuration = request.Payload.GetProperty("configuration");
                    await _controller.SaveConfigurationAsync(configuration.GetRawText());
                    return _controller.CreateSnapshot(_webView.Environment.BrowserVersionString);
                }
            case "runtime.start":
                await _controller.StartAsync();
                return new { isRunning = true };
            case "runtime.stop":
                await _controller.StopAsync();
                return new { isRunning = false };
            case "runtime.restart":
                await _controller.RestartAsync();
                return new { isRunning = true };
            case "runtime.profile.select":
                {
                    var profileId = request.Payload.TryGetProperty("profileId", out var profileIdElement)
                        ? profileIdElement.GetString()
                        : null;
                    await _controller.SetProfileOverrideAsync(profileId);
                    return _controller.CreateSnapshot(_webView.Environment.BrowserVersionString);
                }
            case "gamepad.capture":
                return await _controller.CaptureGamepadButtonAsync();
            case "keyboard.capture":
                return await _controller.CaptureKeyboardChordAsync();
            case "mouse.capture":
                return await _controller.CaptureMouseButtonAsync();
            case "processes.list":
                return _controller.ListRunningApplications();
            case "gpu.devices.get":
                return _controller.ListGpuDevices();
            case "microphone.test.start":
                return await _controller.StartMicrophoneTestAsync();
            case "microphone.test.stop":
                await _controller.StopMicrophoneTestAsync();
                return new { isRunning = false };
            case "dialog.pickFile":
                return PickFile(request.Payload);
            case "diagnostic.metrics.get":
                return _controller.GetDiagnosticMetrics();
            case "diagnostic.outputTest":
                {
                    var profileId = request.Payload.GetProperty("profileId").GetString()
                        ?? throw new InvalidOperationException("profileId is required.");
                    var text = request.Payload.TryGetProperty("text", out var textElement)
                        ? textElement.GetString() ?? string.Empty
                        : string.Empty;
                    int? targetProcessId = request.Payload.TryGetProperty(
                            "targetProcessId",
                            out var targetProcessElement) &&
                        targetProcessElement.ValueKind == JsonValueKind.Number
                            ? targetProcessElement.GetInt32()
                            : null;
                    await _controller.SendOutputTestAsync(profileId, text, targetProcessId);
                    return new { sent = true };
                }
            case "steamvr.openBindings":
                await _controller.OpenSteamVrBindingsAsync();
                return new { opened = true };
            case "steamvr.status.get":
                return _controller.GetSteamVrStatus();
            case "model.download.start":
                {
                    var providerId = request.Payload.GetProperty("providerId").GetString()
                        ?? throw new InvalidOperationException("providerId is required.");
                    var asrConfiguration = request.Payload.GetProperty("asr");
                    return await _controller.DownloadModelsAsync(
                        providerId,
                        asrConfiguration.GetRawText());
                }
            case "model.asset.download.start":
                {
                    var assetId = request.Payload.GetProperty("assetId").GetString()
                        ?? throw new InvalidOperationException("assetId is required.");
                    var asrConfiguration = request.Payload.GetProperty("asr");
                    return await _controller.DownloadModelAssetAsync(
                        assetId,
                        asrConfiguration.GetRawText());
                }
            case "model.download.cancel":
                return new { canceled = _controller.CancelModelDownload() };
            case "configuration.reveal":
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_controller.ConfigurationPath}\"")
                {
                    UseShellExecute = true
                });
                return new { opened = true };
            case "logs.reveal":
                Directory.CreateDirectory(AppFileLogger.LogDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{AppFileLogger.CurrentLogPath}\"")
                {
                    UseShellExecute = true
                });
                return new { opened = true };
            default:
                throw new InvalidOperationException($"Unknown bridge operation '{request.Type}'.");
        }
    }

    private object PickFile(JsonElement payload)
    {
        var kind = payload.TryGetProperty("kind", out var kindElement)
            ? kindElement.GetString()
            : null;
        var currentPath = payload.TryGetProperty("currentPath", out var currentPathElement)
            ? currentPathElement.GetString()
            : null;
        if (string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase))
        {
            using var folderDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                ShowNewFolderButton = false,
                SelectedPath = !string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath)
                    ? Path.GetFullPath(currentPath)
                    : string.Empty
            };
            var folder = folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                ? folderDialog.SelectedPath
                : null;
            return new { path = folder };
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = kind switch
            {
                "executable" => "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                "model" => "Model files (*.gguf;*.bin;*.onnx)|*.gguf;*.bin;*.onnx|All files (*.*)|*.*",
                _ => "All files (*.*)|*.*"
            }
        };
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var fullPath = Path.GetFullPath(currentPath);
            dialog.InitialDirectory = Path.GetDirectoryName(fullPath);
            dialog.FileName = Path.GetFileName(fullPath);
        }

        var selected = dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
        return new { path = selected };
    }

    private void OnLogReceived(object? sender, VRChatVoiceInput.Windows.Runtime.RuntimeLogEventArgs entry) =>
        _ = _owner.Dispatcher.InvokeAsync(() => PostEvent("runtime.log", entry));

    private void OnStateChanged(object? sender, VRChatVoiceInput.Windows.Runtime.RuntimeStateChangedEventArgs state) =>
        _ = _owner.Dispatcher.InvokeAsync(() => PostEvent("runtime.state", state));

    private void OnConfigurationChanged(object? sender, EventArgs eventArgs) =>
        _ = _owner.Dispatcher.InvokeAsync(() => PostEvent("configuration.changed", new { }));

    private void OnModelDownloadProgressChanged(object? sender, ModelDownloadProgress progress) =>
        _ = _owner.Dispatcher.InvokeAsync(() => PostEvent("model.download.progress", progress));

    private void OnMicrophoneLevelsChanged(object? sender, VRChatVoiceInput.Windows.Audio.MicrophoneLevelsChangedEventArgs eventArgs) =>
        _ = _owner.Dispatcher.InvokeAsync(() => PostEvent("microphone.levels", eventArgs.Levels));

    private void PostEvent(string type, object payload) =>
        Post(new BridgeEvent(1, type, payload));

    private void Post(object message) =>
        PostIfActive(JsonSerializer.Serialize(message, JsonOptions));

    private void PostIfActive(string json)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _webView.PostWebMessageAsJson(json);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AppFileLogger.Error("webview-bridge", "Unable to post a message to the web UI.", exception);
        }
    }

    private sealed record BridgeRequest(int Version, string Id, string Type, JsonElement Payload);

    private sealed record BridgeResponse(
        int Version,
        string Id,
        string Type,
        bool Ok,
        object? Payload,
        BridgeError? Error);

    private sealed record BridgeError(string Code, string Message);

    private sealed record BridgeEvent(int Version, string Type, object Payload);
}
