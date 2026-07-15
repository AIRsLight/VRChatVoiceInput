using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Core.Asr;

public static class AsrProviderFactory
{
    public static readonly IReadOnlyList<string> ProviderIds =
    [
        "paraformer-gguf",
        "sensevoice-gguf",
        "funasr-nano-gguf",
        "qwen3-asr",
        "whisper-cpp"
    ];

    public static IAsrProvider Create(AsrConfiguration configuration, string? providerOverride = null)
    {
        var providerId = providerOverride ?? configuration.Provider;
        return providerId.ToLowerInvariant() switch
        {
            "paraformer-gguf" => new ParaformerGgufProvider(configuration.Paraformer),
            "sensevoice-gguf" => new SenseVoiceGgufProvider(configuration.SenseVoice),
            "funasr-nano-gguf" => new FunAsrNanoGgufProvider(configuration.FunAsrNano),
            "qwen3-asr" => new Qwen3AsrProvider(configuration.Qwen3Asr),
            "whisper-cpp" => new WhisperCppProvider(configuration.WhisperCpp),
            _ => throw new InvalidOperationException(
                $"Unsupported ASR provider '{providerId}'. Available providers: {string.Join(", ", ProviderIds)}.")
        };
    }

    public static AsrProviderCapabilities GetCapabilities(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "qwen3-asr" =>
                AsrProviderCapabilities.TerminologyHints |
                AsrProviderCapabilities.SegmentedStreaming,
            "paraformer-gguf" or "sensevoice-gguf" or "funasr-nano-gguf" or "whisper-cpp" =>
                AsrProviderCapabilities.SegmentedStreaming,
            _ => throw new InvalidOperationException(
                $"Unsupported ASR provider '{providerId}'. Available providers: {string.Join(", ", ProviderIds)}.")
        };

    public static IReadOnlyList<AsrProviderAvailability> CheckAvailability(AsrConfiguration configuration) =>
    [
        Check(
            "paraformer-gguf",
            configuration.Streaming,
            ("Executable", configuration.Paraformer.ExecutablePath, false),
            ("Model", configuration.Paraformer.ModelPath, false),
            ("VAD model", configuration.Paraformer.VadModelPath, true),
            ("Punctuation model",
                configuration.Paraformer.UsePunctuation
                    ? configuration.Paraformer.PunctuationModelPath
                    : null,
                true)),
        Check(
            "sensevoice-gguf",
            configuration.Streaming,
            (string.Equals(configuration.SenseVoice.Backend, "vulkan", StringComparison.OrdinalIgnoreCase)
                    ? "Vulkan executable"
                    : "CPU executable",
                string.Equals(configuration.SenseVoice.Backend, "vulkan", StringComparison.OrdinalIgnoreCase)
                    ? configuration.SenseVoice.VulkanExecutablePath
                    : configuration.SenseVoice.CpuExecutablePath,
                false),
            ("Model", configuration.SenseVoice.ModelPath, false),
            ("VAD model", configuration.SenseVoice.VadModelPath, true)),
        Check(
            "funasr-nano-gguf",
            configuration.Streaming,
            (string.Equals(configuration.FunAsrNano.Backend, "vulkan", StringComparison.OrdinalIgnoreCase)
                    ? "Vulkan executable"
                    : "CPU executable",
                string.Equals(configuration.FunAsrNano.Backend, "vulkan", StringComparison.OrdinalIgnoreCase)
                    ? configuration.FunAsrNano.VulkanExecutablePath
                    : configuration.FunAsrNano.ExecutablePath,
                false),
            ("Encoder model", configuration.FunAsrNano.EncoderModelPath, false),
            ("Language model", configuration.FunAsrNano.LanguageModelPath, false),
            ("VAD model", configuration.FunAsrNano.VadModelPath, true)),
        Check(
            "qwen3-asr",
            configuration.Streaming,
            ("Convolution frontend", configuration.Qwen3Asr.ConvFrontendPath, false),
            ("Encoder model", configuration.Qwen3Asr.EncoderPath, false),
            ("Decoder model", configuration.Qwen3Asr.DecoderPath, false),
            ("Tokenizer merges", TokenizerFile(configuration.Qwen3Asr.TokenizerPath, "merges.txt"), false),
            ("Tokenizer configuration", TokenizerFile(configuration.Qwen3Asr.TokenizerPath, "tokenizer_config.json"), false),
            ("Tokenizer vocabulary", TokenizerFile(configuration.Qwen3Asr.TokenizerPath, "vocab.json"), false)),
        Check(
            "whisper-cpp",
            configuration.Streaming,
            (configuration.WhisperCpp.UseGpu ? "Vulkan server executable" : "CPU server executable",
                configuration.WhisperCpp.UseGpu
                    ? configuration.WhisperCpp.VulkanServerExecutablePath
                    : configuration.WhisperCpp.ServerExecutablePath,
                false),
            ("Model", configuration.WhisperCpp.ModelPath, false),
            ("VAD model", configuration.WhisperCpp.VadModelPath, true))
    ];

    private static string TokenizerFile(string tokenizerPath, string fileName) =>
        string.IsNullOrWhiteSpace(tokenizerPath) ? string.Empty : Path.Combine(tokenizerPath, fileName);

    private static AsrProviderAvailability Check(
        string providerId,
        StreamingAsrConfiguration streaming,
        params (string Label, string? Path, bool Optional)[] files)
    {
        var missingFiles = files
            .Where(file => !file.Optional || !string.IsNullOrWhiteSpace(file.Path))
            .Where(file => string.IsNullOrWhiteSpace(file.Path) || !File.Exists(file.Path))
            .Select(file => string.IsNullOrWhiteSpace(file.Path)
                ? $"{file.Label}: not configured"
                : file.Path!)
            .ToArray();
        var streamingMissingFiles = string.IsNullOrWhiteSpace(streaming.SileroVadModelPath) ||
            !File.Exists(streaming.SileroVadModelPath)
                ? new[]
                {
                    string.IsNullOrWhiteSpace(streaming.SileroVadModelPath)
                        ? "Silero VAD model: not configured"
                        : streaming.SileroVadModelPath
                }
                : Array.Empty<string>();
        return new AsrProviderAvailability(
            providerId,
            missingFiles.Length == 0,
            missingFiles,
            SupportsStreaming: true,
            StreamingAvailable: streamingMissingFiles.Length == 0,
            streamingMissingFiles,
            SupportsTerminologyHints: GetCapabilities(providerId)
                .HasFlag(AsrProviderCapabilities.TerminologyHints));
    }
}

public sealed record AsrProviderAvailability(
    string Id,
    bool Available,
    IReadOnlyList<string> MissingFiles,
    bool SupportsStreaming,
    bool StreamingAvailable,
    IReadOnlyList<string> StreamingMissingFiles,
    bool SupportsTerminologyHints);
