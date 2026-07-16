using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VRChatVoiceInput.Windows.Output;
using VRChatVoiceInput.Windows.Runtime;

namespace VRChatVoiceInput.App;

public partial class NativeMainWindow
{
    private UIElement BuildDiagnosticsPage()
    {
        var root = new StackPanel { MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Stretch };
        var snapshot = _controller.GetRuntimeDiagnosticSnapshot();
        var durations = snapshot.Logs.Where(log => log.RecognitionDurationMilliseconds.HasValue)
            .Select(log => log.RecognitionDurationMilliseconds!.Value).ToArray();
        _diagnosticMemoryText = MetricValue(FormatBytes(snapshot.WorkingSetBytes));
        _diagnosticAverageText = MetricValue(durations.Length == 0
            ? T("No samples")
            : $"{durations.Average():0} ms · {durations.Length}");

        var metrics = new UniformGrid { Columns = 5 };
        metrics.Children.Add(Metric(T("Service status"), T(_controller.IsRunning ? "Running" : "Stopped")));
        metrics.Children.Add(Metric(T("Microphones"), GetMicrophones().Count.ToString()));
        metrics.Children.Add(Metric(T("Profiles"), Profiles.Count.ToString()));
        metrics.Children.Add(Metric(T("Current memory"), _diagnosticMemoryText));
        metrics.Children.Add(Metric(T("Average generation time"), _diagnosticAverageText));
        root.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 0, 20),
            ClipToBounds = true,
            Child = metrics
        });

        root.Children.Add(BuildOutputTest());

        var logPath = new TextBox { Text = AppFileLogger.CurrentLogPath, IsReadOnly = true };
        var reveal = IconButton("\uE8B7", T("Open log file"), (_, _) => RevealPath(AppFileLogger.CurrentLogPath));
        var logPathRow = new Grid();
        logPathRow.ColumnDefinitions.Add(new ColumnDefinition());
        logPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        logPathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        Grid.SetColumn(logPath, 0);
        Grid.SetColumn(reveal, 2);
        logPathRow.Children.Add(logPath);
        logPathRow.Children.Add(reveal);
        root.Children.Add(Section(
            T("Local log file"),
            T("Errors and runtime events are retained for 14 days"),
            Field(T("Log file"), logPathRow)));

        var logHeader = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 22, 0, 10) };
        var logActions = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = IconButton("\uE72C", T("Refresh"), (_, _) => BuildCurrentPage());
        refresh.Margin = new Thickness(0, 0, 6, 0);
        var clear = IconButton("\uE74D", T("Clear view"), (_, _) =>
        {
            _diagnosticLogPanel?.Children.Clear();
            _diagnosticLogPanel?.Children.Add(new TextBlock
            {
                Text = T("No log entries"),
                Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 20)
            });
        }, true);
        logActions.Children.Add(refresh);
        logActions.Children.Add(clear);
        DockPanel.SetDock(logActions, Dock.Right);
        logHeader.Children.Add(logActions);
        logHeader.Children.Add(new TextBlock
        {
            Text = T("Runtime log"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        root.Children.Add(logHeader);

        _diagnosticLogPanel = new StackPanel();
        foreach (var log in snapshot.Logs.Reverse())
        {
            _diagnosticLogPanel.Children.Add(BuildLogRow(log));
        }
        if (snapshot.Logs.Count == 0)
        {
            _diagnosticLogPanel.Children.Add(new TextBlock
            {
                Text = T("No log entries"), Foreground = MutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 20)
            });
        }
        root.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x25, 0x22)),
            MinHeight = 260,
            ClipToBounds = true,
            Child = _diagnosticLogPanel
        });
        return Page(root, 920, 24);
    }

    private UIElement BuildOutputTest()
    {
        var profiles = Profiles.OfType<System.Text.Json.Nodes.JsonObject>().ToArray();
        if (profiles.All(profile => !string.Equals(profile["id"]?.GetValue<string>(), _diagnosticProfileId, StringComparison.OrdinalIgnoreCase)))
        {
            _diagnosticProfileId = profiles.FirstOrDefault(profile =>
                string.Equals(profile["id"]?.GetValue<string>(), _controller.ProfileOverride, StringComparison.OrdinalIgnoreCase))?["id"]?.GetValue<string>()
                ?? profiles.FirstOrDefault()?["id"]?.GetValue<string>();
        }
        var selectedProfile = profiles.FirstOrDefault(profile =>
            string.Equals(profile["id"]?.GetValue<string>(), _diagnosticProfileId, StringComparison.OrdinalIgnoreCase));
        var profileCombo = new ComboBox
        {
            ItemsSource = profiles.Select(profile => new Option(
                profile["id"]?.GetValue<string>() ?? string.Empty,
                $"{profile["id"]?.GetValue<string>()} · {profile["output"]?["mode"]?.GetValue<string>()}")),
            DisplayMemberPath = nameof(Option.Label),
            SelectedValuePath = nameof(Option.Value),
            SelectedValue = _diagnosticProfileId
        };
        profileCombo.SelectionChanged += (_, _) =>
        {
            if (_building || profileCombo.SelectedValue is not string value) return;
            _diagnosticProfileId = value;
            BuildCurrentPage();
        };
        var applications = _controller.ListRunningApplications();
        var applicationCombo = new ComboBox
        {
            ItemsSource = applications.Select(application => new Option(
                application.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                application.DisplayName)),
            DisplayMemberPath = nameof(Option.Label),
            SelectedValuePath = nameof(Option.Value),
            SelectedIndex = applications.Count > 0 ? 0 : -1
        };
        var text = new TextBox { Text = "VRChat Voice Input test" };
        var send = ActionButton(T("Send test"), async (_, _) =>
        {
            if (profileCombo.SelectedValue is not string profileId) return;
            try
            {
                int? processId = applicationCombo.SelectedValue is string value && int.TryParse(value, out var parsed)
                    ? parsed
                    : null;
                await _controller.SendOutputTestAsync(profileId, text.Text, processId);
                ShowToast(T("Test output sent."));
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        });
        send.HorizontalAlignment = HorizontalAlignment.Left;
        send.Margin = new Thickness(0, 8, 0, 0);
        var fields = new List<UIElement>
        {
            TwoColumns(Field(T("Profile"), profileCombo), Field(T("Message"), text))
        };
        if (selectedProfile?["output"]?["mode"]?.GetValue<string>() == "captured-window")
        {
            fields.Add(Field(T("Target application"), applicationCombo));
        }
        fields.Add(send);
        return Section(
            T("Output test"),
            T("Send text through the selected preset output"),
            fields.ToArray());
    }

    private Border Metric(string label, string value) => Metric(label, MetricValue(value));

    private Border Metric(string label, TextBlock value)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = MutedBrush });
        stack.Children.Add(value);
        return new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(14),
            Child = stack
        };
    }

    private static TextBlock MetricValue(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private Border BuildLogRow(RuntimeLogEventArgs log)
    {
        var grid = new Grid { Margin = new Thickness(10, 7, 10, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var time = new TextBlock
        {
            Text = log.Timestamp.ToLocalTime().ToString("HH:mm:ss"), FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x97, 0xA5, 0x9D))
        };
        var code = new TextBlock
        {
            Text = log.Code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10,
            Foreground = log.Code == "error"
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x96))
                : log.Code == "warning"
                    ? new SolidColorBrush(Color.FromRgb(0xF3, 0xC4, 0x6C))
                    : new SolidColorBrush(Color.FromRgb(0x97, 0xA5, 0x9D))
        };
        var message = new TextBlock
        {
            Text = (log.ProfileId is null ? string.Empty : $"[{log.ProfileId}] ") + log.Message,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE4, 0xDF)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(time, 0);
        Grid.SetColumn(code, 1);
        Grid.SetColumn(message, 2);
        grid.Children.Add(time);
        grid.Children.Add(code);
        grid.Children.Add(message);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x3A, 0x35)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid
        };
    }

    private void UpdateDiagnosticMetrics()
    {
        if (_diagnosticMemoryText is null || _diagnosticAverageText is null) return;
        var snapshot = _controller.GetRuntimeDiagnosticSnapshot();
        var durations = snapshot.Logs.Where(log => log.RecognitionDurationMilliseconds.HasValue)
            .Select(log => log.RecognitionDurationMilliseconds!.Value).ToArray();
        _diagnosticMemoryText.Text = FormatBytes(snapshot.WorkingSetBytes);
        _diagnosticAverageText.Text = durations.Length == 0 ? T("No samples") : $"{durations.Average():0} ms · {durations.Length}";
    }
}
