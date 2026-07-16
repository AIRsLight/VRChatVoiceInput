using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace VRChatVoiceInput.App;

public partial class MainWindow : Window, ISettingsWindow
{
    private const string AssetHost = "appassets.local";
    private readonly RuntimeController _runtimeController;
    private WebMessageBridge? _bridge;
    private Task<bool>? _closeTask;
    private bool _allowClose;
    private bool _webViewEventsAttached;
    private bool _webViewInitializing;
    private bool _runtimeSwitchTestStarted;

    public MainWindow(RuntimeController runtimeController)
    {
        _runtimeController = runtimeController;
        InitializeComponent();
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 243, 244, 242);
        LoadingText.Text = NativeLocalization.Translate(
            _runtimeController.LoadConfiguration().Application.UiLanguage,
            "Loading configuration...");
        RetryInterfaceButton.Content = NativeLocalization.Translate(
            _runtimeController.LoadConfiguration().Application.UiLanguage,
            "Retry interface");
        OpenLogsButton.Content = NativeLocalization.Translate(
            _runtimeController.LoadConfiguration().Application.UiLanguage,
            "Open logs");
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webViewInitializing)
        {
            return;
        }

        _webViewInitializing = true;
        ShowLoadingState();
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var userDataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VRChatVoiceInput",
                "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataDirectory);
            await WebView.EnsureCoreWebView2Async(environment);

            var assetsDirectory = Path.Combine(AppContext.BaseDirectory, "WebUI", "dist");
            if (!Directory.Exists(assetsDirectory))
            {
                throw new DirectoryNotFoundException($"Web UI assets were not found at '{assetsDirectory}'.");
            }

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AssetHost,
                assetsDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            ConfigureSecurity(WebView.CoreWebView2);
            if (!_webViewEventsAttached)
            {
                WebView.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;
                WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                WebView.CoreWebView2.NavigationCompleted += CaptureViewsForVisualTestingAsync;
                _webViewEventsAttached = true;
            }

            if (_bridge is not null)
            {
                _bridge.UiReady -= OnUiReady;
                _bridge.Dispose();
            }
            _bridge = new WebMessageBridge(WebView.CoreWebView2, _runtimeController, this);
            _bridge.UiReady += OnUiReady;
            WebView.Source = new Uri($"https://{AssetHost}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var message = NativeLocalization.Translate(
                _runtimeController.LoadConfiguration().Application.UiLanguage,
                "WebView2 runtime missing");
            AppFileLogger.Error("webview", message);
            ShowInitializationError(message);
        }
        catch (Exception exception)
        {
            AppFileLogger.Error("webview", "Unable to initialize the settings interface.", exception);
            ShowInitializationError(exception.Message);
        }
        finally
        {
            _webViewInitializing = false;
        }
    }

    private static void ConfigureSecurity(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.NavigationStarting += (_, eventArgs) =>
        {
            if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, AssetHost, StringComparison.OrdinalIgnoreCase))
            {
                eventArgs.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
        };
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        _ = CloseAfterSavingAsync();
    }

    public Task<bool> CloseAfterSavingAsync()
    {
        _closeTask ??= CloseAfterSavingCoreAsync();
        return _closeTask;
    }

    private async Task<bool> CloseAfterSavingCoreAsync()
    {
        try
        {
            await SavePendingConfigurationAsync();
            _allowClose = true;
            Close();
            return true;
        }
        catch (Exception exception)
        {
            if (WebView.CoreWebView2 is not null)
            {
                try
                {
                    await WebView.ExecuteScriptAsync("window.vrchatVoiceInputCancelClose?.()");
                }
                catch (InvalidOperationException)
                {
                }
            }

            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                NativeLocalization.Translate(
                    _runtimeController.LoadConfiguration().Application.UiLanguage,
                    "Unable to save settings"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _closeTask = null;
            return false;
        }
    }

    private async Task SavePendingConfigurationAsync()
    {
        if (WebView.CoreWebView2 is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var encodedState = await WebView.ExecuteScriptAsync(
                "window.vrchatVoiceInputPrepareClose?.() ?? null");
            var stateJson = JsonSerializer.Deserialize<string?>(encodedState);
            if (string.IsNullOrWhiteSpace(stateJson))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<NativeCloseState>(
                stateJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("The settings page returned an invalid close state.");
            if (state.SaveInProgress || state.Busy)
            {
                await Task.Delay(100);
                continue;
            }

            if (state.Dirty && state.Configuration.ValueKind == JsonValueKind.Object)
            {
                await _runtimeController.SaveConfigurationAsync(state.Configuration.GetRawText());
            }

            return;
        }

        throw new TimeoutException("Timed out while waiting for settings to finish saving.");
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (WebView.CoreWebView2 is not null)
        {
            WebView.CoreWebView2.ProcessFailed -= OnWebViewProcessFailed;
            WebView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            WebView.CoreWebView2.NavigationCompleted -= CaptureViewsForVisualTestingAsync;
        }

        if (_bridge is not null)
        {
            _bridge.UiReady -= OnUiReady;
            _bridge.Dispose();
        }
        _bridge = null;
        WebView.Dispose();

        var keepRunning = _runtimeController.LoadConfiguration().Application.CloseToTray;
        ((App)System.Windows.Application.Current).OnSettingsWindowClosed(this, keepRunning);
    }

    private void OnUiReady(object? sender, EventArgs eventArgs)
    {
        WebView.Visibility = Visibility.Visible;
        LoadingPanel.Visibility = Visibility.Collapsed;
        StartRuntimeSwitchTestIfRequested();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            return;
        }

        var message = $"Settings page navigation failed: {eventArgs.WebErrorStatus}.";
        AppFileLogger.Error("webview", message);
        _runtimeController.AddHostLog("error", message);
        ShowInitializationError(message);
    }

    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        var message = $"WebView2 process failed: {eventArgs.ProcessFailedKind}.";
        AppFileLogger.Error("webview", message);
        _runtimeController.AddHostLog("error", message);
        ShowInitializationError(message);
    }

    private async void CaptureViewsForVisualTestingAsync(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        var captureDirectory = Environment.GetEnvironmentVariable("VRCHAT_VOICE_INPUT_CAPTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(captureDirectory) || !eventArgs.IsSuccess)
        {
            return;
        }

        Directory.CreateDirectory(captureDirectory);
        await Task.Delay(700);
        if (string.Equals(
                Environment.GetEnvironmentVariable("VRCHAT_VOICE_INPUT_TEST_MICROPHONES"),
                "1",
                StringComparison.Ordinal))
        {
            await WebView.CoreWebView2.ExecuteScriptAsync(
                "document.querySelector('[data-view=\"general\"]')?.click();" +
                "document.querySelector('#microphone-test-toggle')?.click()");
            await Task.Delay(900);
        }
        var autoSaveTestValue = Environment.GetEnvironmentVariable("VRCHAT_VOICE_INPUT_AUTOSAVE_TEST_MS");
        if (int.TryParse(autoSaveTestValue, out var minimumDurationMs))
        {
            await WebView.CoreWebView2.ExecuteScriptAsync(
                "(() => {" +
                "const input = document.querySelector('[data-config-path=\"audio.minimumDurationMs\"]');" +
                $"if (input) {{ input.value = '{minimumDurationMs}'; input.dispatchEvent(new Event('change', {{ bubbles: true }})); }}" +
                "})()");
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VRCHAT_VOICE_INPUT_CLOSE_AFTER_AUTOSAVE_EDIT"),
                    "1",
                    StringComparison.Ordinal))
            {
                await Task.Delay(50);
                Close();
                return;
            }

            await Task.Delay(1800);
        }

        foreach (var view in new[] { "general", "profiles", "models", "diagnostics" })
        {
            await WebView.CoreWebView2.ExecuteScriptAsync(
                $"document.querySelector('[data-view=\"{view}\"]')?.click()");
            await Task.Delay(250);
            await CapturePreviewAsync(captureDirectory, view);
        }

        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('[data-view=\"models\"]')?.click();" +
            "document.querySelector('[data-model-tab=\"senseVoice\"]')?.click()");
        await Task.Delay(250);
        await CapturePreviewAsync(captureDirectory, "models-sensevoice");
        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('[data-model-tab=\"qwen3Asr\"]')?.click()");
        await Task.Delay(250);
        await CapturePreviewAsync(captureDirectory, "models-qwen3");
        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('.content .settings-section:last-child .notice')?.scrollIntoView({ block: 'end' })");
        await Task.Delay(50);
        await CapturePreviewAsync(captureDirectory, "models-streaming-settings");
        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('.content')?.scrollTo(0, 0);" +
            "document.querySelector('[data-model-tab=\"whisperCpp\"]')?.click();" +
            "document.querySelector('[data-config-path=\"asr.whisperCpp.useGpu\"]')?.click();" +
            "(() => { const gpu = document.querySelector('[data-gpu-target=\"whisper\"]');" +
            "if (gpu && gpu.options.length > 1) { gpu.value = gpu.options[1].value; gpu.dispatchEvent(new Event('change', { bubbles: true })); } })()");
        await Task.Delay(250);
        await CapturePreviewAsync(captureDirectory, "models-whisper");

        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('[data-view=\"profiles\"]')?.click(); document.querySelector('#open-process-picker')?.click()");
        await Task.Delay(700);
        await CapturePreviewAsync(captureDirectory, "process-picker");
        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('#process-picker-close')?.click()");

        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('[data-view=\"profiles\"]')?.click()");
        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('[data-profile-tab=\"input\"]')?.click(); " +
            "document.querySelector('[data-segment-value=\"mouse\"]')?.click()");
        await Task.Delay(250);
        await CapturePreviewAsync(captureDirectory, "profiles-mouse");
        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('[data-segment-value=\"keyboard\"]')?.click()");
        foreach (var tab in new[] { "processing", "output" })
        {
            await WebView.CoreWebView2.ExecuteScriptAsync(
                $"document.querySelector('[data-profile-tab=\"{tab}\"]')?.click()");
            await Task.Delay(250);
            await CapturePreviewAsync(captureDirectory, $"profiles-{tab}");
        }
        await WebView.CoreWebView2.ExecuteScriptAsync(
            "document.querySelector('[data-profile-id=\"vrchat\"]')?.click();" +
            "document.querySelector('[data-profile-tab=\"processing\"]')?.click()");
        await Task.Delay(250);
        await CapturePreviewAsync(captureDirectory, "profiles-streaming-osc");
    }

    private async Task CapturePreviewAsync(string captureDirectory, string name)
    {
        var capturePath = Path.Combine(captureDirectory, $"{name}.png");
        await using var stream = File.Create(capturePath);
        await WebView.CoreWebView2.CapturePreviewAsync(
            CoreWebView2CapturePreviewImageFormat.Png,
            stream);
    }

    public void ShowUnhandledError(Exception exception)
    {
        ShowInitializationError(
            $"{NativeLocalization.Translate(_runtimeController.LoadConfiguration().Application.UiLanguage, "The settings interface encountered an error.")} {exception.Message}");
    }

    private void ShowLoadingState()
    {
        WebView.Visibility = Visibility.Hidden;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingProgress.Visibility = Visibility.Visible;
        LoadingActions.Visibility = Visibility.Collapsed;
        ErrorLogPath.Visibility = Visibility.Collapsed;
        LoadingText.Text = NativeLocalization.Translate(
            _runtimeController.LoadConfiguration().Application.UiLanguage,
            "Loading configuration...");
    }

    private void ShowInitializationError(string message)
    {
        WebView.Visibility = Visibility.Hidden;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingProgress.Visibility = Visibility.Collapsed;
        LoadingText.Text = message;
        ErrorLogPath.Text = $"{NativeLocalization.Translate(_runtimeController.LoadConfiguration().Application.UiLanguage, "Log file")}: {AppFileLogger.CurrentLogPath}";
        ErrorLogPath.Visibility = Visibility.Visible;
        LoadingActions.Visibility = Visibility.Visible;
    }

    private async void OnRetryInterfaceClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_webViewInitializing)
        {
            return;
        }

        ShowLoadingState();
        try
        {
            if (WebView.CoreWebView2 is not null)
            {
                WebView.CoreWebView2.Reload();
            }
            else
            {
                await InitializeWebViewAsync();
            }
        }
        catch (Exception exception)
        {
            AppFileLogger.Error("webview", "Unable to retry the settings interface.", exception);
            ShowInitializationError(exception.Message);
        }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Directory.CreateDirectory(AppFileLogger.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{AppFileLogger.CurrentLogPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            AppFileLogger.Error("shell", "Unable to open the log location.", exception);
            LoadingText.Text = exception.Message;
        }
    }

    private sealed record NativeCloseState(
        bool Dirty,
        bool SaveInProgress,
        bool Busy,
        JsonElement Configuration);
}
