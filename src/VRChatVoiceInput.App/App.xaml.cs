using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace VRChatVoiceInput.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\VRChatVoiceInput.App";
    private Mutex? _singleInstanceMutex;
    private RuntimeController? _runtimeController;
    private TrayIconService? _trayIcon;
    private bool _isExiting;
    private bool _reopenLifecycleTestCompleted;

    public bool IsExiting => _isExiting;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        AppFileLogger.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(eventArgs);
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                NativeLocalization.Translate("auto", "Already running"),
                "VRChat Voice Input",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            var configPath = ConfigurationPathResolver.Resolve(eventArgs.Args);
            AppFileLogger.Info("application", $"Using configuration '{configPath}'.");
            var configDirectory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(configDirectory))
            {
                Directory.SetCurrentDirectory(configDirectory);
            }

            _runtimeController = new RuntimeController(configPath);
            _trayIcon = new TrayIconService(_runtimeController, ShowSettings, RequestExitAsync);

            var minimized = eventArgs.Args.Any(arg =>
                string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase));
            if (!minimized)
            {
                ShowSettings();
            }

            var configuration = _runtimeController.LoadConfiguration();
            if (configuration.Application.StartRuntimeOnLaunch)
            {
                try
                {
                    await _runtimeController.StartAsync();
                }
                catch (Exception exception)
                {
                    AppFileLogger.Warning(
                        "startup",
                        $"Automatic runtime startup failed: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            AppFileLogger.Error("startup", "Application startup failed.", exception);
            System.Windows.MessageBox.Show(
                exception.Message,
                NativeLocalization.Translate(
                    _runtimeController?.LoadConfiguration().Application.UiLanguage ?? "auto",
                    "Startup failed"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await RequestExitAsync();
        }
    }

    public void ShowSettings()
    {
        if (_runtimeController is null)
        {
            return;
        }

        var window = MainWindow;
        if (window is null)
        {
            window = new NativeMainWindow(_runtimeController);
            MainWindow = window;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    internal void OnSettingsWindowClosed(Window window, bool keepRunning)
    {
        if (ReferenceEquals(MainWindow, window))
        {
            MainWindow = null;
        }

        if (!_isExiting && !keepRunning)
        {
            _ = RequestExitAsync();
        }
        else if (!_isExiting &&
                 keepRunning &&
                 !_reopenLifecycleTestCompleted &&
                 string.Equals(
                     Environment.GetEnvironmentVariable("VRCHAT_VOICE_INPUT_REOPEN_AFTER_CLOSE"),
                     "1",
                     StringComparison.Ordinal))
        {
            _reopenLifecycleTestCompleted = true;
            _ = ReopenSettingsForLifecycleTestAsync();
        }
    }

    private async Task ReopenSettingsForLifecycleTestAsync()
    {
        await Task.Delay(1000);
        ShowSettings();
    }

    public async Task RequestExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        if (MainWindow is ISettingsWindow window && !await window.CloseAfterSavingAsync())
        {
            _isExiting = false;
            return;
        }

        _trayIcon?.Dispose();
        if (_runtimeController is not null)
        {
            await _runtimeController.DisposeAsync();
        }
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown();
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        AppFileLogger.Error("wpf", "Unhandled UI exception.", eventArgs.Exception);
        eventArgs.Handled = true;
        if (MainWindow is ISettingsWindow window)
        {
            window.ShowUnhandledError(eventArgs.Exception);
            return;
        }

        System.Windows.MessageBox.Show(
            $"{eventArgs.Exception.Message}\n\nLog: {AppFileLogger.CurrentLogPath}",
            "VRChat Voice Input",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        var exception = eventArgs.ExceptionObject as Exception;
        AppFileLogger.Error(
            "appdomain",
            eventArgs.IsTerminating ? "Unhandled terminating exception." : "Unhandled application exception.",
            exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        AppFileLogger.Error("task", "Unobserved background task exception.", eventArgs.Exception);
        eventArgs.SetObserved();
    }
}

internal static class ConfigurationPathResolver
{
    public static string Resolve(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--config", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Count)
                {
                    throw new ArgumentException("--config requires a file path.");
                }

                return Path.GetFullPath(args[index]);
            }
        }

        var workingDirectoryConfig = Path.GetFullPath("appsettings.json");
        if (File.Exists(workingDirectoryConfig))
        {
            return workingDirectoryConfig;
        }

        var nearbyConfig = FindConfigInParentDirectories(AppContext.BaseDirectory);
        if (nearbyConfig is not null)
        {
            return nearbyConfig;
        }

        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRChatVoiceInput");
        var appDataConfig = Path.Combine(appDataDirectory, "appsettings.json");
        if (!File.Exists(appDataConfig))
        {
            Directory.CreateDirectory(appDataDirectory);
            var template = Path.Combine(AppContext.BaseDirectory, "appsettings.example.json");
            if (!File.Exists(template))
            {
                throw new FileNotFoundException("Configuration template was not found.", template);
            }

            File.Copy(template, appDataConfig);
        }

        return appDataConfig;
    }

    private static string? FindConfigInParentDirectories(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
