using System.Globalization;

namespace VRChatVoiceInput.App;

internal static class NativeLocalization
{
    public static string Resolve(string? setting)
    {
        var requested = string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(setting)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : setting;

        return requested.ToLowerInvariant() switch
        {
            "zh" => "zh",
            "ja" => "ja",
            "en" => "en",
            _ => "en"
        };
    }

    public static string Translate(string? setting, string key) => (Resolve(setting), key) switch
    {
        ("zh", "Loading configuration...") => "正在加载配置...",
        ("zh", "Unable to initialize settings") => "无法初始化设置",
        ("zh", "Unable to save settings") => "无法保存设置",
        ("zh", "Already running") => "VRChat Voice Input 已在运行。",
        ("zh", "Startup failed") => "VRChat Voice Input 启动失败",
        ("zh", "The settings interface encountered an error.") => "设置界面发生错误。",
        ("zh", "Retry interface") => "重试界面",
        ("zh", "Open logs") => "打开日志",
        ("zh", "Log file") => "日志文件",
        ("zh", "Service running") => "服务正在运行",
        ("zh", "Service stopped") => "服务已停止",
        ("zh", "Service cannot start") => "服务无法启动",
        ("zh", "Start service") => "启动服务",
        ("zh", "Stop service") => "停止服务",
        ("zh", "Open settings") => "打开设置",
        ("zh", "Exit") => "退出",
        ("zh", "Running") => "运行中",
        ("zh", "Stopped") => "已停止",

        ("ja", "Loading configuration...") => "設定を読み込んでいます...",
        ("ja", "Unable to initialize settings") => "設定を初期化できません",
        ("ja", "Unable to save settings") => "設定を保存できません",
        ("ja", "Already running") => "VRChat Voice Input は既に実行中です。",
        ("ja", "Startup failed") => "VRChat Voice Input の起動に失敗しました",
        ("ja", "The settings interface encountered an error.") => "設定画面でエラーが発生しました。",
        ("ja", "Retry interface") => "画面を再試行",
        ("ja", "Open logs") => "ログを開く",
        ("ja", "Log file") => "ログファイル",
        ("ja", "Service running") => "サービスは実行中です",
        ("ja", "Service stopped") => "サービスは停止しています",
        ("ja", "Service cannot start") => "サービスを開始できません",
        ("ja", "Start service") => "サービスを開始",
        ("ja", "Stop service") => "サービスを停止",
        ("ja", "Open settings") => "設定を開く",
        ("ja", "Exit") => "終了",
        ("ja", "Running") => "実行中",
        ("ja", "Stopped") => "停止",

        _ => key
    };
}
