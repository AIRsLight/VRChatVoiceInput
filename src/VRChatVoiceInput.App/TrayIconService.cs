using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VRChatVoiceInput.App;

internal sealed class TrayIconService : IDisposable
{
    private readonly RuntimeController _controller;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon _applicationIcon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _openSettingsItem;
    private readonly Forms.ToolStripMenuItem _runtimeItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Action _showSettings;
    private readonly Func<Task> _exitAsync;
    private string _languageSetting;

    public TrayIconService(
        RuntimeController controller,
        Action showSettings,
        Func<Task> exitAsync)
    {
        _controller = controller;
        _showSettings = showSettings;
        _exitAsync = exitAsync;
        _languageSetting = _controller.LoadConfiguration().Application.UiLanguage;
        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _openSettingsItem = new Forms.ToolStripMenuItem(null, null, (_, _) => showSettings());
        _runtimeItem = new Forms.ToolStripMenuItem();
        _exitItem = new Forms.ToolStripMenuItem(null, null, async (_, _) => await _exitAsync());
        _runtimeItem.Click += OnRuntimeClick;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_openSettingsItem);
        menu.Items.Add(_runtimeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_exitItem);

        var processPath = Environment.ProcessPath;
        _applicationIcon = !string.IsNullOrWhiteSpace(processPath)
            ? Drawing.Icon.ExtractAssociatedIcon(processPath)
                ?? (Drawing.Icon)Drawing.SystemIcons.Application.Clone()
            : (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "VRChat Voice Input",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => showSettings();
        _controller.StateChanged += OnStateChanged;
        _controller.ConfigurationChanged += OnConfigurationChanged;
        UpdateState(_controller.IsRunning);
    }

    public void Dispose()
    {
        _controller.StateChanged -= OnStateChanged;
        _controller.ConfigurationChanged -= OnConfigurationChanged;
        _runtimeItem.Click -= OnRuntimeClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private async void OnRuntimeClick(object? sender, EventArgs eventArgs)
    {
        _runtimeItem.Enabled = false;
        try
        {
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
            if (exception is RuntimeNotReadyException notReady)
            {
                _controller.AddHostLog("warning", exception.Message, exception: exception);
                ShowRuntimeStartFailure(notReady);
            }
            else
            {
                _controller.AddHostLog("error", exception.Message, exception: exception);
            }
        }
        finally
        {
            _runtimeItem.Enabled = true;
            UpdateState(_controller.IsRunning);
        }
    }

    internal void ShowRuntimeStartFailure(RuntimeNotReadyException exception)
    {
        _showSettings();
        var issue = exception.Readiness.Issues[0];
        var message = NativeLocalization.Resolve(_languageSetting) switch
        {
            "zh" => $"预设“{issue.ProfileId}”缺少 {issue.MissingRequirements.Count} 个必需组件。请在模型页面完成安装。",
            "ja" => $"プリセット「{issue.ProfileId}」には {issue.MissingRequirements.Count} 件の必須コンポーネントが不足しています。モデル画面でインストールしてください。",
            _ => $"Profile '{issue.ProfileId}' is missing {issue.MissingRequirements.Count} required components. Install them on the Models page."
        };
        _notifyIcon.ShowBalloonTip(
            8000,
            NativeLocalization.Translate(_languageSetting, "Service cannot start"),
            message,
            Forms.ToolTipIcon.Warning);
    }

    private void OnStateChanged(object? sender, VRChatVoiceInput.Windows.Runtime.RuntimeStateChangedEventArgs state) =>
        UpdateState(state.IsRunning);

    private void OnConfigurationChanged(object? sender, EventArgs eventArgs)
    {
        _languageSetting = _controller.LoadConfiguration().Application.UiLanguage;
        UpdateState(_controller.IsRunning);
    }

    private void UpdateState(bool running)
    {
        _statusItem.Text = NativeLocalization.Translate(
            _languageSetting,
            running ? "Service running" : "Service stopped");
        _openSettingsItem.Text = NativeLocalization.Translate(_languageSetting, "Open settings");
        _runtimeItem.Text = NativeLocalization.Translate(
            _languageSetting,
            running ? "Stop service" : "Start service");
        _exitItem.Text = NativeLocalization.Translate(_languageSetting, "Exit");
        _notifyIcon.Text = $"VRChat Voice Input - {NativeLocalization.Translate(_languageSetting, running ? "Running" : "Stopped")}";
    }
}
