using System.Globalization;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Core.Asr;

public sealed class FunAsrNanoGgufProvider : IAsrProvider, IExternalAsrProviderMetrics, IDisposable
{
    private readonly ResidentExternalAsrWorker _worker;

    public FunAsrNanoGgufProvider(FunAsrNanoConfiguration configuration)
    {
        var useVulkan = string.Equals(configuration.Backend, "vulkan", StringComparison.OrdinalIgnoreCase);
        if (!useVulkan && !string.Equals(configuration.Backend, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported Fun-ASR-Nano backend '{configuration.Backend}'. Available backends: cpu, vulkan.");
        }

        var executablePath = useVulkan
            ? configuration.VulkanExecutablePath
            : configuration.ExecutablePath;
        ExternalProcessAsrRunner.RequireFile(
            executablePath,
            useVulkan ? "Fun-ASR-Nano Vulkan executable" : "Fun-ASR-Nano CPU executable");
        ExternalProcessAsrRunner.RequireFile(configuration.EncoderModelPath, "Fun-ASR-Nano encoder GGUF model");
        ExternalProcessAsrRunner.RequireFile(configuration.LanguageModelPath, "Fun-ASR-Nano language GGUF model");
        var arguments = new List<string>
        {
            "--enc",
            Path.GetFullPath(configuration.EncoderModelPath),
            "-m",
            Path.GetFullPath(configuration.LanguageModelPath)
        };

        if (useVulkan)
        {
            arguments.Add("--backend");
            arguments.Add("vulkan");
            if (configuration.VulkanDeviceIndex is { } deviceIndex)
            {
                if (deviceIndex < 0)
                {
                    throw new InvalidOperationException("Fun-ASR-Nano Vulkan device index cannot be negative.");
                }

                arguments.Add("--device");
                arguments.Add(deviceIndex.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration.VadModelPath))
        {
            ExternalProcessAsrRunner.RequireFile(configuration.VadModelPath, "FSMN VAD GGUF model");
            arguments.Add("--vad");
            arguments.Add(Path.GetFullPath(configuration.VadModelPath));
        }
        else if (configuration.ChunkSeconds > 0)
        {
            arguments.Add("--chunk");
            arguments.Add(configuration.ChunkSeconds.ToString(CultureInfo.InvariantCulture));
        }

        _worker = new ResidentExternalAsrWorker(
            useVulkan ? "Fun-ASR-Nano Vulkan" : "Fun-ASR-Nano CPU",
            executablePath,
            arguments);
    }

    public string Id => "funasr-nano-gguf";

    public AsrProviderCapabilities Capabilities => AsrProviderCapabilities.SegmentedStreaming;

    public long WorkingSetBytes => _worker.WorkingSetBytes;

    public Task<RecognitionResult> TranscribeAsync(
        AudioInput audio,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        RecognitionOptionsValidator.Validate(this, options);
        ExternalProcessAsrRunner.RequireFile(audio.FilePath, "Audio input");
        return _worker.TranscribeAsync(audio.FilePath, cancellationToken);
    }

    public void Dispose() => _worker.Dispose();
}
