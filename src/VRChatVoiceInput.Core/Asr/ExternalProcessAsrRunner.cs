using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace VRChatVoiceInput.Core.Asr;

internal static class ExternalProcessAsrRunner
{
    public static async Task<RecognitionResult> RunAsync(
        string providerName,
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {providerName} executable '{startInfo.FileName}'.");
        var stopwatch = Stopwatch.StartNew();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        stopwatch.Stop();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{providerName} exited with code {process.ExitCode}: {GetFailureDetail(stderr, stdout)}");
        }

        var text = TranscriptOutputParser.Parse(stdout);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"{providerName} returned no transcription text. {GetFailureDetail(stderr, stdout)}");
        }

        return new RecognitionResult(text, null, stopwatch.Elapsed);
    }

    public static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("ASR executable path is not configured.");
        }

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("ASR executable was not found.", executablePath);
        }

        return new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    public static void RequireFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{description} path is not configured.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} was not found.", path);
        }
    }

    private static string GetFailureDetail(string stderr, string stdout)
    {
        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        detail = detail.Trim();
        return detail.Length <= 1000 ? detail : detail[^1000..];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

internal static partial class TranscriptOutputParser
{
    [GeneratedRegex("<\\|[^|>]+\\|>", RegexOptions.CultureInvariant)]
    private static partial Regex SpecialTokenRegex();

    public static string Parse(string stdout)
    {
        var lines = stdout
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(IsTranscriptLine)
            .Select(line => SpecialTokenRegex().Replace(line, string.Empty))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return JoinLines(lines);
    }

    private static bool IsTranscriptLine(string line)
    {
        var trimmed = line.TrimStart();
        return !trimmed.StartsWith("[", StringComparison.Ordinal) &&
               !trimmed.StartsWith("log_", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("ggml_", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("whisper_", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("main:", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.StartsWith("system_info:", StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var text = lines[0].Trim();
        for (var index = 1; index < lines.Count; index++)
        {
            var next = lines[index].Trim();
            var needsSpace = text.Length > 0 && next.Length > 0 &&
                             IsAsciiWordCharacter(text[^1]) && IsAsciiWordCharacter(next[0]);
            text += needsSpace ? $" {next}" : next;
        }

        return text;
    }

    private static bool IsAsciiWordCharacter(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
