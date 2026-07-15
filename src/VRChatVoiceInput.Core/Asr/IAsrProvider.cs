namespace VRChatVoiceInput.Core.Asr;

public interface IAsrProvider
{
    string Id { get; }

    AsrProviderCapabilities Capabilities { get; }

    Task<RecognitionResult> TranscribeAsync(
        AudioInput audio,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IExternalAsrProviderMetrics
{
    long WorkingSetBytes { get; }
}

public sealed record AudioInput(string FilePath);

public sealed record RecognitionOptions
{
    public IReadOnlyList<string> TerminologyHints { get; init; } = Array.Empty<string>();

    public string? Language { get; init; }
}

[Flags]
public enum AsrProviderCapabilities
{
    None = 0,
    TerminologyHints = 1,
    WeightedTerminologyHints = 2,
    SegmentedStreaming = 4
}

public sealed record RecognitionResult(string Text, string? DetectedLanguage, TimeSpan Duration);

public sealed class NoSpeechRecognizedException : InvalidOperationException
{
    public NoSpeechRecognizedException(string message) : base(message)
    {
    }
}

internal static class RecognitionOptionsValidator
{
    public static void Validate(IAsrProvider provider, RecognitionOptions? options)
    {
        if (options?.TerminologyHints.Count > 0 &&
            !provider.Capabilities.HasFlag(AsrProviderCapabilities.TerminologyHints))
        {
            throw new NotSupportedException($"ASR provider '{provider.Id}' does not support terminology hints yet.");
        }
    }
}
