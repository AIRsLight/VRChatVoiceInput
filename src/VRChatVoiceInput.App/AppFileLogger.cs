using System.IO;
using System.Text;

namespace VRChatVoiceInput.App;

internal static class AppFileLogger
{
    private const int RetentionDays = 14;
    private static readonly object Sync = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VRChatVoiceInput",
        "Logs");

    public static string CurrentLogPath => Path.Combine(
        LogDirectory,
        $"vrchat-voice-input-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var path in Directory.EnumerateFiles(LogDirectory, "vrchat-voice-input-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        Info("application", $"Application starting. Version {typeof(AppFileLogger).Assembly.GetName().Version}.");
    }

    public static void Info(string source, string message) => Write("INFO", source, message);

    public static void Warning(string source, string message, Exception? exception = null) =>
        Write("WARN", source, message, exception);

    public static void Error(string source, string message, Exception? exception = null) =>
        Write("ERROR", source, message, exception);

    public static void Write(string level, string source, string message, Exception? exception = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                using var stream = new FileStream(
                    CurrentLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write('[');
                writer.Write(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
                writer.Write("] [");
                writer.Write(level);
                writer.Write("] [");
                writer.Write(source);
                writer.Write("] ");
                writer.WriteLine(message.ReplaceLineEndings(" "));
                if (exception is not null)
                {
                    writer.WriteLine(exception);
                }
            }
        }
        catch
        {
        }
    }
}
