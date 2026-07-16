using System.Reflection;

namespace VRChatVoiceInput.App;

internal static class ApplicationVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(ApplicationVersion).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
