using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VRChatVoiceInput.Windows.Audio;
using VRChatVoiceInput.Windows.Output;
using VRChatVoiceInput.Windows.Runtime;

namespace VRChatVoiceInput.App;

public partial class NativeMainWindow : Window, ISettingsWindow
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x08, 0x7E, 0x72));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x70, 0x6A));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x76, 0x5D));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0x5A, 0x00));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xB1, 0x3B, 0x32));

    private readonly RuntimeController _controller;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _diagnosticTimer;
    private JsonObject _configuration = new();
    private readonly Dictionary<string, string> _selectedModelAssetIds = new(StringComparer.OrdinalIgnoreCase);
    private string _view = "general";
    private int _selectedProfileIndex;
    private string _profileTab = "input";
    private string _selectedProvider = "sensevoice-gguf";
    private bool _advancedModelSettingsOpen;
    private bool _building;
    private bool _dirty;
    private bool _saving;
    private bool _savingOwnConfiguration;
    private bool _allowClose;
    private bool _microphoneTestRunning;
    private Task<bool>? _closeTask;
    private Panel? _microphoneMeters;
    private TextBlock? _diagnosticMemoryText;
    private TextBlock? _diagnosticAverageText;
    private StackPanel? _diagnosticLogPanel;
    private string? _diagnosticProfileId;
    private ModelDownloadProgress? _downloadProgress;

    public NativeMainWindow(RuntimeController controller)
    {
        _controller = controller;
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _saveTimer.Tick += OnSaveTimer;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        _diagnosticTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _diagnosticTimer.Tick += (_, _) =>
        {
            if (_view == "diagnostics")
            {
                UpdateDiagnosticMetrics();
            }
        };

        LoadConfiguration();
        AttachControllerEvents();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        ApplyLocalization();
        BuildCurrentPage();
        UpdateTopbar();
    }

    private void AttachControllerEvents()
    {
        _controller.StateChanged += OnRuntimeStateChanged;
        _controller.ConfigurationChanged += OnConfigurationChanged;
        _controller.LogReceived += OnLogReceived;
        _controller.ModelDownloadProgressChanged += OnModelDownloadProgressChanged;
        _controller.MicrophoneLevelsChanged += OnMicrophoneLevelsChanged;
    }

    private void DetachControllerEvents()
    {
        _controller.StateChanged -= OnRuntimeStateChanged;
        _controller.ConfigurationChanged -= OnConfigurationChanged;
        _controller.LogReceived -= OnLogReceived;
        _controller.ModelDownloadProgressChanged -= OnModelDownloadProgressChanged;
        _controller.MicrophoneLevelsChanged -= OnMicrophoneLevelsChanged;
    }

    private void LoadConfiguration()
    {
        _configuration = JsonNode.Parse(File.ReadAllText(_controller.ConfigurationPath))?.AsObject()
            ?? throw new InvalidOperationException("Configuration JSON is empty.");
        var profiles = Profiles;
        _selectedProfileIndex = profiles.Count == 0
            ? 0
            : Math.Clamp(_selectedProfileIndex, 0, profiles.Count - 1);
        _selectedProvider = GetString("asr.provider", "sensevoice-gguf");
    }

    private void ApplyLocalization()
    {
        BrandTitle.Text = T("Voice Input");
        BrandSubtitle.Text = T("VRChat companion");
        GeneralNavText.Text = T("General");
        ProfilesNavText.Text = T("Profiles");
        ModelsNavText.Text = T("Models");
        DiagnosticsNavText.Text = T("Diagnostics");
        VersionText.Text = $"App {ApplicationVersion.Current}";
    }

    private string T(string key) => NativeWpfLocalization.Translate(GetString("application.uiLanguage", "auto"), key);

    private void OnNavigate(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { CommandParameter: string view })
        {
            return;
        }

        _view = view;
        BuildCurrentPage();
    }

    private void BuildCurrentPage()
    {
        _building = true;
        try
        {
            _diagnosticTimer.IsEnabled = _view == "diagnostics";
            PageContent.Content = _view switch
            {
                "profiles" => BuildProfilesPage(),
                "models" => BuildModelsPage(),
                "diagnostics" => BuildDiagnosticsPage(),
                _ => BuildGeneralPage()
            };
            GeneralNav.Tag = _view == "general" ? "active" : null;
            ProfilesNav.Tag = _view == "profiles" ? "active" : null;
            ModelsNav.Tag = _view == "models" ? "active" : null;
            DiagnosticsNav.Tag = _view == "diagnostics" ? "active" : null;
            UpdateTopbar();
        }
        finally
        {
            _building = false;
        }
    }

    private void UpdateTopbar()
    {
        var context = _view switch
        {
            "profiles" => CurrentProfile?["id"]?.GetValue<string>() ?? string.Empty,
            "models" => _selectedProvider,
            "diagnostics" => FormatLogCount(_controller.GetRuntimeDiagnosticSnapshot().Logs.Count),
            _ => _controller.ConfigurationPath
        };
        PageTitle.Text = _view switch
        {
            "profiles" => T("Application profiles"),
            "models" => T("Local models"),
            "diagnostics" => T("Diagnostics"),
            _ => T("General")
        };
        PageContext.Text = context;

        var running = _controller.IsRunning;
        RuntimeDot.Fill = running ? SuccessBrush : new SolidColorBrush(Color.FromRgb(0x9D, 0xA5, 0x9F));
        var profile = _controller.ProfileOverride ?? T("Automatic");
        RuntimeStatusText.Text = $"{T(running ? "Running" : "Stopped")} · {profile}";
        RuntimeToggleGlyph.Text = running ? "\uE71A" : "\uE768";
        RuntimeToggleButton.ToolTip = NativeLocalization.Translate(
            GetString("application.uiLanguage", "auto"),
            running ? "Stop service" : "Start service");
        SaveStatusText.Text = T(_saving ? "Saving..." : _dirty ? "Waiting to save" : "Saved");
        SaveStatusText.Foreground = _saving ? MutedBrush : _dirty ? WarningBrush : SuccessBrush;
    }

    private string FormatLogCount(int count) => NativeLocalization.Resolve(GetString("application.uiLanguage", "auto")) switch
    {
        "zh" => $"{count} 条日志",
        "ja" => $"{count} 件のログ",
        _ => $"{count} log entries"
    };

    private async void OnRuntimeToggle(object sender, RoutedEventArgs eventArgs)
    {
        RuntimeToggleButton.IsEnabled = false;
        try
        {
            await SaveNowAsync();
            if (_controller.IsRunning)
            {
                await _controller.StopAsync();
            }
            else
            {
                await _controller.StartAsync();
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            RuntimeToggleButton.IsEnabled = true;
            UpdateTopbar();
        }
    }

    private void MarkDirty(bool rebuild = false)
    {
        if (_building)
        {
            return;
        }

        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateTopbar();
        if (rebuild)
        {
            BuildCurrentPage();
        }
    }

    private async void OnSaveTimer(object? sender, EventArgs eventArgs)
    {
        _saveTimer.Stop();
        await SaveNowAsync();
    }

    private async Task SaveNowAsync()
    {
        _saveTimer.Stop();
        if (!_dirty || _saving)
        {
            return;
        }

        _saving = true;
        UpdateTopbar();
        try
        {
            _savingOwnConfiguration = true;
            await _controller.SaveConfigurationAsync(_configuration.ToJsonString(JsonOptions));
            _dirty = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            _savingOwnConfiguration = false;
            _saving = false;
            UpdateTopbar();
        }
    }

    private void OnRuntimeStateChanged(object? sender, RuntimeStateChangedEventArgs eventArgs) =>
        Dispatcher.InvokeAsync(UpdateTopbar);

    private void OnConfigurationChanged(object? sender, EventArgs eventArgs)
    {
        if (_savingOwnConfiguration || _dirty)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            LoadConfiguration();
            ApplyLocalization();
            BuildCurrentPage();
        });
    }

    private void OnLogReceived(object? sender, RuntimeLogEventArgs eventArgs)
    {
        if (_view == "diagnostics")
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (_diagnosticLogPanel is not null)
                {
                    _diagnosticLogPanel.Children.Insert(0, BuildLogRow(eventArgs));
                }
                UpdateDiagnosticMetrics();
            });
        }
    }

    private void OnModelDownloadProgressChanged(object? sender, ModelDownloadProgress progress)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _downloadProgress = progress;
            var active = progress.State is "checking" or "downloading" or "verifying" or "extracting";
            DownloadPanel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            DownloadText.Text = $"{progress.Message} {FormatBytes(progress.BytesDownloaded)} / {FormatBytes(progress.TotalBytes)}";
            DownloadProgress.Value = progress.TotalBytes <= 0
                ? 0
                : Math.Clamp(progress.BytesDownloaded * 100d / progress.TotalBytes, 0, 100);
            if (!active && _view == "models")
            {
                BuildCurrentPage();
            }
            if (progress.State == "error")
            {
                ShowToast(progress.Message, true);
            }
        });
    }

    private void OnMicrophoneLevelsChanged(object? sender, MicrophoneLevelsChangedEventArgs eventArgs)
    {
        if (!_microphoneTestRunning)
        {
            return;
        }

        Dispatcher.InvokeAsync(() => UpdateMicrophoneMeters(eventArgs.Levels));
    }

    private void OnCancelDownload(object sender, RoutedEventArgs eventArgs) => _controller.CancelModelDownload();

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
        await SaveNowAsync();
        if (_dirty)
        {
            _closeTask = null;
            return false;
        }

        _allowClose = true;
        Close();
        return true;
    }

    private async void OnClosed(object? sender, EventArgs eventArgs)
    {
        _saveTimer.Stop();
        _diagnosticTimer.Stop();
        DetachControllerEvents();
        if (_microphoneTestRunning)
        {
            await _controller.StopMicrophoneTestAsync();
        }
        ((App)System.Windows.Application.Current).OnSettingsWindowClosed(
            this,
            GetBool("application.closeToTray", true));
    }

    public void ShowUnhandledError(Exception exception) => ShowError(exception);

    private void ShowError(Exception exception)
    {
        AppFileLogger.Error("native-wpf", "Native WPF interface operation failed.", exception);
        ShowToast($"{T("Operation failed")}: {exception.Message}", true);
    }

    private void ShowToast(string message, bool error = false)
    {
        Toast.Background = error ? DangerBrush : new SolidColorBrush(Color.FromRgb(0x20, 0x28, 0x24));
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private ScrollViewer Page(UIElement content, double maxWidth = 920, double topPadding = 4)
    {
        var container = new Border
        {
            MaxWidth = maxWidth,
            Padding = new Thickness(28, topPadding, 28, 48),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = container
        };
    }

    private Border Section(string title, string subtitle, params UIElement[] children)
    {
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            heading.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16,
                Margin = new Thickness(0, 5, 0, 0)
            });
        }

        var body = new StackPanel();
        foreach (var child in children)
        {
            body.Children.Add(child);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(heading, 0);
        Grid.SetColumn(body, 2);
        grid.Children.Add(heading);
        grid.Children.Add(body);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 22, 0, 26),
            Child = grid
        };
    }

    private FrameworkElement Field(string label, UIElement control, string? help = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0x58, 0x52)),
            Margin = new Thickness(0, 0, 0, 5)
        });
        stack.Children.Add(control);
        if (!string.IsNullOrWhiteSpace(help))
        {
            stack.Children.Add(new TextBlock
            {
                Text = help,
                Foreground = MutedBrush,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 14.5,
                Margin = new Thickness(0, 5, 0, 0)
            });
        }
        return stack;
    }

    private Grid TwoColumns(UIElement left, UIElement right)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        if (left is FrameworkElement leftElement) leftElement.Margin = new Thickness(0, 0, 7, 0);
        if (right is FrameworkElement rightElement) rightElement.Margin = new Thickness(7, 0, 0, 0);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private Button ActionButton(string text, RoutedEventHandler handler, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(primary ? "PrimaryButtonStyle" : "ActionButtonStyle")
        };
        button.Click += handler;
        return button;
    }

    private Button IconButton(string glyph, string toolTip, RoutedEventHandler handler, bool danger = false)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButtonStyle"),
            ToolTip = toolTip,
            Foreground = danger ? DangerBrush : new SolidColorBrush(Color.FromRgb(0x20, 0x26, 0x22)),
            Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 14
            }
        };
        button.Click += handler;
        return button;
    }

    private CheckBox BoundCheckBox(string text, string path, bool rebuild = false)
    {
        var checkBox = new CheckBox { Content = text, IsChecked = GetBool(path) };
        checkBox.Checked += (_, _) => { Set(path, true); MarkDirty(rebuild); };
        checkBox.Unchecked += (_, _) => { Set(path, false); MarkDirty(rebuild); };
        return checkBox;
    }

    private TextBox BoundTextBox(string path, bool multiline = false)
    {
        var textBox = new TextBox
        {
            Text = GetString(path),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 78 : 34,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden
        };
        textBox.TextChanged += (_, _) =>
        {
            if (_building) return;
            Set(path, textBox.Text);
            MarkDirty();
        };
        return textBox;
    }

    private TextBox BoundNumberBox(string path, int minimum, int maximum, bool nullable = false)
    {
        var node = GetNode(path);
        var textBox = new TextBox { Text = node is null ? string.Empty : GetInt(path).ToString(CultureInfo.InvariantCulture) };
        textBox.LostKeyboardFocus += (_, _) =>
        {
            if (nullable && string.IsNullOrWhiteSpace(textBox.Text))
            {
                Set(path, null);
                MarkDirty();
                return;
            }
            if (!int.TryParse(textBox.Text, out var value))
            {
                textBox.Text = GetInt(path).ToString(CultureInfo.InvariantCulture);
                return;
            }
            value = Math.Clamp(value, minimum, maximum);
            textBox.Text = value.ToString(CultureInfo.InvariantCulture);
            Set(path, value);
            MarkDirty();
        };
        return textBox;
    }

    private ComboBox BoundCombo(string path, IEnumerable<Option> options, bool rebuild = false, string? fallback = null)
    {
        var combo = new ComboBox
        {
            ItemsSource = options.ToArray(),
            DisplayMemberPath = nameof(Option.Label),
            SelectedValuePath = nameof(Option.Value),
            SelectedValue = GetString(path, fallback ?? string.Empty)
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (_building || combo.SelectedValue is not string value) return;
            Set(path, value);
            MarkDirty(rebuild);
        };
        return combo;
    }

    private FrameworkElement SegmentedControl(string path, IEnumerable<Option> options, bool rebuild = true)
    {
        var values = options.ToArray();
        var grid = new Grid();
        var current = GetString(path);
        for (var index = 0; index < values.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var option = values[index];
            var button = new Button
            {
                Style = (Style)FindResource("SegmentButtonStyle"),
                MinWidth = 88,
                Tag = string.Equals(option.Value, current, StringComparison.OrdinalIgnoreCase) ? "active" : null,
                BorderThickness = index == values.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 1, 0)
            };
            button.Content = string.IsNullOrWhiteSpace(option.Glyph)
                ? option.Label
                : new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = option.Glyph,
                            FontFamily = new FontFamily("Segoe Fluent Icons"),
                            FontSize = 14,
                            Margin = new Thickness(0, 0, 7, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = option.Label,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                };
            button.Click += (_, _) =>
            {
                Set(path, option.Value);
                MarkDirty(rebuild);
            };
            Grid.SetColumn(button, index);
            grid.Children.Add(button);
        }
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC3, 0xCB, 0xC5)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = grid
        };
    }

    private JsonArray Profiles => GetNode("profiles.items")?.AsArray() ?? new JsonArray();

    private JsonObject? CurrentProfile => Profiles.Count == 0
        ? null
        : Profiles[Math.Clamp(_selectedProfileIndex, 0, Profiles.Count - 1)]?.AsObject();

    private JsonNode? GetNode(string path)
    {
        JsonNode? node = _configuration;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            node = node switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            };
            if (node is null) return null;
        }
        return node;
    }

    private void Set(string path, object? value)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonNode node = _configuration;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            node = node switch
            {
                JsonObject obj => obj[segments[index]] ?? throw new InvalidOperationException($"Missing configuration path '{path}'."),
                JsonArray array => array[int.Parse(segments[index], CultureInfo.InvariantCulture)]
                    ?? throw new InvalidOperationException($"Missing configuration path '{path}'."),
                _ => throw new InvalidOperationException($"Invalid configuration path '{path}'.")
            };
        }

        var replacement = value switch
        {
            null => null,
            JsonNode jsonNode => jsonNode,
            string stringValue => JsonValue.Create(stringValue),
            bool boolValue => JsonValue.Create(boolValue),
            int intValue => JsonValue.Create(intValue),
            long longValue => JsonValue.Create(longValue),
            float floatValue => JsonValue.Create(floatValue),
            double doubleValue => JsonValue.Create(doubleValue),
            _ => throw new InvalidOperationException($"Unsupported configuration value type '{value.GetType().Name}'.")
        };
        if (node is JsonObject parentObject)
        {
            parentObject[segments[^1]] = replacement;
        }
        else if (node is JsonArray parentArray)
        {
            parentArray[int.Parse(segments[^1], CultureInfo.InvariantCulture)] = replacement;
        }
    }

    private string GetString(string path, string fallback = "") =>
        GetNode(path)?.GetValue<string>() ?? fallback;

    private int GetInt(string path, int fallback = 0) =>
        GetNode(path)?.GetValue<int>() ?? fallback;

    private bool GetBool(string path, bool fallback = false) =>
        GetNode(path)?.GetValue<bool>() ?? fallback;

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var amount = (double)value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }
        return $"{amount:0.#} {units[unit]}";
    }

    private sealed record Option(string Value, string Label, string? Glyph = null)
    {
        public override string ToString() => Label;
    }
}
