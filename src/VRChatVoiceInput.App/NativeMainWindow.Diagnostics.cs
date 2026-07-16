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

        var metrics = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 20) };
        metrics.Children.Add(Metric(T("Service status"), T(_controller.IsRunning ? "Running" : "Stopped")));
        metrics.Children.Add(Metric("Microphones", GetMicrophones().Count.ToString()));
        metrics.Children.Add(Metric(T("Profiles"), Profiles.Count.ToString()));
        metrics.Children.Add(Metric(T("Current memory"), _diagnosticMemoryText));
        metrics.Children.Add(Metric(T("Average generation time"), _diagnosticAverageText));
        root.Children.Add(metrics);

        root.Children.Add(BuildOutputTest());

        var logHeader = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        var reveal = ActionButton(T("Open log file"), (_, _) => RevealPath(AppFileLogger.CurrentLogPath));
        DockPanel.SetDock(reveal, Dock.Right);
        logHeader.Children.Add(reveal);
        logHeader.Children.Add(new TextBlock
        {
            Text = T("Logs"), FontSize = 14, FontWeight = FontWeights.SemiBold,
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
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFB, 0xFA)),
            Child = _diagnosticLogPanel
        });
        return Page(root);
    }

    private UIElement BuildOutputTest()
    {
        var profiles = Profiles.OfType<System.Text.Json.Nodes.JsonObject>().ToArray();
        var selectedProfile = profiles.FirstOrDefault(profile =>
            string.Equals(profile["id"]?.GetValue<string>(), _controller.ProfileOverride, StringComparison.OrdinalIgnoreCase)) ?? profiles.FirstOrDefault();
        var profileCombo = new ComboBox
        {
            ItemsSource = profiles.Select(profile => new Option(
                profile["id"]?.GetValue<string>() ?? string.Empty,
                $"{profile["id"]?.GetValue<string>()} · {profile["output"]?["mode"]?.GetValue<string>()}")),
            DisplayMemberPath = nameof(Option.Label),
            SelectedValuePath = nameof(Option.Value),
            SelectedValue = selectedProfile?["id"]?.GetValue<string>()
        };
        var applications = _controller.ListRunningApplications();
        var applicationCombo = new ComboBox
        {
            ItemsSource = applications,
            DisplayMemberPath = nameof(RunningApplicationInfo.DisplayName),
            SelectedValuePath = nameof(RunningApplicationInfo.ProcessId),
            SelectedIndex = applications.Count > 0 ? 0 : -1
        };
        var text = new TextBox { Text = "VRChat Voice Input test", MinHeight = 62, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        var send = ActionButton(T("Send test"), async (_, _) =>
        {
            if (profileCombo.SelectedValue is not string profileId) return;
            try
            {
                int? processId = applicationCombo.SelectedValue is int value ? value : null;
                await _controller.SendOutputTestAsync(profileId, text.Text, processId);
                ShowToast("Test output sent.");
            }
            catch (Exception exception)
            {
                ShowError(exception);
            }
        }, true);
        send.HorizontalAlignment = HorizontalAlignment.Left;
        send.Margin = new Thickness(0, 8, 0, 0);
        return Section(
            T("Output test"),
            string.Empty,
            TwoColumns(Field(T("Target profile"), profileCombo), Field(T("Target application"), applicationCombo)),
            Field(T("Test text"), text),
            send);
    }

    private Border Metric(string label, string value) => Metric(label, MetricValue(value));

    private Border Metric(string label, TextBlock value)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = MutedBrush });
        stack.Children.Add(value);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xDE, 0xD9)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 10, 12, 10),
            Child = stack
        };
    }

    private static TextBlock MetricValue(string text) => new()
    {
        Text = text,
        FontSize = 15,
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
            FontSize = 10, Foreground = MutedBrush
        };
        var code = new TextBlock
        {
            Text = log.Code, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 10,
            Foreground = log.Code == "error" ? DangerBrush : log.Code == "sent" ? SuccessBrush : MutedBrush
        };
        var message = new TextBlock
        {
            Text = (log.ProfileId is null ? string.Empty : $"[{log.ProfileId}] ") + log.Message,
            FontSize = 11, TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(time, 0);
        Grid.SetColumn(code, 1);
        Grid.SetColumn(message, 2);
        grid.Children.Add(time);
        grid.Children.Add(code);
        grid.Children.Add(message);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE8, 0xE5)),
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
