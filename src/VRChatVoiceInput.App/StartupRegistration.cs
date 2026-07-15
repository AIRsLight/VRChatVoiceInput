using Microsoft.Win32;

namespace VRChatVoiceInput.App;

internal static class StartupRegistration
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VRChatVoiceInput";

    public static void Apply(bool enabled, string configurationPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the Windows startup registry key.");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Application executable path is unavailable.");
        key.SetValue(
            ValueName,
            $"\"{executable}\" --minimized --config \"{configurationPath}\"",
            RegistryValueKind.String);
    }
}
