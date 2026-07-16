using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VRChatVoiceInput.Windows.Audio;

namespace VRChatVoiceInput.App;

public partial class NativeMainWindow
{
    private UIElement BuildGeneralPage()
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

        var language = new ComboBox
        {
            ItemsSource = new[]
            {
                new Option("auto", T("System language")),
                new Option("zh", T("Chinese")),
                new Option("ja", T("Japanese")),
                new Option("en", T("English"))
            },
            DisplayMemberPath = nameof(Option.Label),
            SelectedValuePath = nameof(Option.Value),
            SelectedValue = GetString("application.uiLanguage", "auto")
        };
        language.SelectionChanged += (_, _) =>
        {
            if (_building || language.SelectedValue is not string selected) return;
            Set("application.uiLanguage", selected);
            MarkDirty();
            ApplyLocalization();
            BuildCurrentPage();
        };

        content.Children.Add(Section(
            T("Service"),
            T("Desktop lifecycle"),
            BoundCheckBox(T("Start service when the application opens"), "application.startRuntimeOnLaunch"),
            BoundCheckBox(T("Keep running when the settings window closes"), "application.closeToTray"),
            BoundCheckBox(T("Start with Windows"), "application.startWithWindows")));

        content.Children.Add(Section(
            T("Interface language"),
            T("Unsupported system languages fall back to English."),
            Field(T("Interface language"), language)));

        content.Children.Add(Section(
            T("Download source"),
            T("HF Mirror is a third-party service. GitHub downloads keep their original source, and every file is still verified by SHA-256."),
            SegmentedControl(
                "application.modelDownloadSource",
                [
                    new Option("official", "Hugging Face"),
                    new Option("hf-mirror", "HF Mirror")
                ],
                fallback: "official")));

        var microphones = GetMicrophones();
        var microphoneOptions = new List<Option>
        {
            new(string.Empty, T("Default communications device"))
        };
        microphoneOptions.AddRange(microphones.Select(item => new Option(item.Id, item.Name)));
        var microphone = new ComboBox
        {
            ItemsSource = microphoneOptions,
            DisplayMemberPath = nameof(Option.Label),
            SelectedValuePath = nameof(Option.Value),
            SelectedValue = GetString("audio.deviceId")
        };
        if (microphone.SelectedIndex < 0) microphone.SelectedIndex = 0;
        microphone.SelectionChanged += (_, _) =>
        {
            if (_building || microphone.SelectedValue is not string selected) return;
            Set("audio.deviceId", string.IsNullOrWhiteSpace(selected) ? null : selected);
            MarkDirty();
        };

        var microphoneTestButton = ActionButton(
            T(_microphoneTestRunning ? "Stop test" : "Start test"),
            OnMicrophoneTestToggle,
            _microphoneTestRunning,
            _microphoneTestRunning ? MaterialIconPaths.Stop : MaterialIconPaths.Microphone);
        var testHeader = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 4, 0, 10) };
        var testTitle = new TextBlock
        {
            Text = T("Microphone test"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(microphoneTestButton, Dock.Right);
        testHeader.Children.Add(microphoneTestButton);
        var testCopy = new StackPanel();
        testCopy.Children.Add(testTitle);
        testCopy.Children.Add(new TextBlock
        {
            Text = T("Shows live input from every active microphone."),
            FontSize = 10,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 4, 0, 0)
        });
        testHeader.Children.Add(testCopy);
        _microphoneMeters = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var profileOptions = Profiles
            .OfType<JsonObject>()
            .Select(profile => new Option(profile["id"]?.GetValue<string>() ?? string.Empty, profile["id"]?.GetValue<string>() ?? string.Empty));

        content.Children.Add(Section(
            T("Audio"),
            T("Shared capture device"),
            Field(T("Microphone"), microphone),
            TwoColumns(
                Field(T("Minimum recording (ms)"), BoundNumberBox("audio.minimumDurationMs", 100, 5000)),
                Field(T("Default profile on startup"), BoundCombo("profiles.defaultProfileId", profileOptions))),
            testHeader,
            _microphoneMeters));

        var path = new TextBox { Text = _controller.ConfigurationPath, IsReadOnly = true };
        var reveal = IconButton(
            MaterialIconPaths.FolderOpenOutline,
            T("Show configuration file"),
            (_, _) => RevealPath(_controller.ConfigurationPath));
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition());
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        Grid.SetColumn(path, 0);
        Grid.SetColumn(reveal, 2);
        pathRow.Children.Add(path);
        pathRow.Children.Add(reveal);
        content.Children.Add(Section(
            T("Configuration"),
            T("Schema 1"),
            Field(T("File"), pathRow)));

        return Page(content);
    }

    private IReadOnlyList<AudioDeviceInfo> GetMicrophones()
    {
        try
        {
            return WasapiAudioRecorder.ListCaptureDevices();
        }
        catch (Exception exception)
        {
            AppFileLogger.Warning("native-wpf", $"Unable to enumerate microphones: {exception.Message}");
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    private async void OnMicrophoneTestToggle(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button button) button.IsEnabled = false;
        try
        {
            if (_microphoneTestRunning)
            {
                await _controller.StopMicrophoneTestAsync();
                _microphoneTestRunning = false;
            }
            else
            {
                await _controller.StartMicrophoneTestAsync();
                _microphoneTestRunning = true;
            }
            BuildCurrentPage();
        }
        catch (Exception exception)
        {
            ShowError(exception);
            if (sender is Button failedButton) failedButton.IsEnabled = true;
        }
    }

    private void UpdateMicrophoneMeters(IReadOnlyList<MicrophoneLevelInfo> levels)
    {
        if (_microphoneMeters is null || _view != "general")
        {
            return;
        }

        _microphoneMeters.Children.Clear();
        foreach (var level in levels)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            var name = new TextBlock
            {
                Text = level.Name,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 1,
                Value = level.Available ? Math.Clamp(level.Level, 0, 1) : 0,
                Height = 7,
                Foreground = AccentBrush,
                Margin = new Thickness(10, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var value = new TextBlock
            {
                Text = level.Available ? $"{level.Decibels:0.0} dB" : "-",
                FontSize = 10,
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(value, 2);
            row.Children.Add(name);
            row.Children.Add(bar);
            row.Children.Add(value);
            _microphoneMeters.Children.Add(row);
        }
    }

    private Border Notice(string text, bool warning = false)
    {
        return new Border
        {
            Background = new SolidColorBrush(warning ? Color.FromRgb(0xFF, 0xF4, 0xDC) : Color.FromRgb(0xF1, 0xF6, 0xF4)),
            BorderBrush = new SolidColorBrush(warning ? Color.FromRgb(0xE5, 0xC6, 0x8E) : Color.FromRgb(0xD4, 0xE5, 0xE0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = warning ? WarningBrush : MutedBrush
            }
        };
    }

    private void RevealPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }
}
