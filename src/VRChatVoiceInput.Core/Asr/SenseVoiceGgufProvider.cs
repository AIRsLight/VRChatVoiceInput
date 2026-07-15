using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Core.Asr;

public sealed class SenseVoiceGgufProvider : IAsrProvider, IExternalAsrProviderMetrics, IDisposable
{
    private readonly ResidentExternalAsrWorker _worker;

    public SenseVoiceGgufProvider(SenseVoiceConfiguration configuration)
    {
        var useVulkan = string.Equals(configuration.Backend, "vulkan", StringComparison.OrdinalIgnoreCase);
        if (!useVulkan && !string.Equals(configuration.Backend, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported SenseVoice backend '{configuration.Backend}'. Available backends: cpu, vulkan.");
        }

        var executablePath = useVulkan
            ? configuration.VulkanExecutablePath
            : configuration.CpuExecutablePath;
        ExternalProcessAsrRunner.RequireFile(
            executablePath,
            useVulkan ? "SenseVoice Vulkan executable" : "SenseVoice CPU executable");
        ExternalProcessAsrRunner.RequireFile(configuration.ModelPath, "SenseVoice GGUF model");
        var arguments = new List<string>
        {
            "-m",
            Path.GetFullPath(configuration.ModelPath)
        };
        if (useVulkan)
        {
            arguments.Add("--backend");
            arguments.Add("vulkan");
            if (configuration.VulkanDeviceIndex is { } deviceIndex)
            {
                if (deviceIndex < 0)
                {
                    throw new InvalidOperationException("SenseVoice Vulkan device index cannot be negative.");
                }

                arguments.Add("--device");
                arguments.Add(deviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (!string.IsNullOrWhiteSpace(configuration.VadModelPath))
        {
            ExternalProcessAsrRunner.RequireFile(configuration.VadModelPath, "FSMN VAD GGUF model");
            arguments.Add("--vad");
            arguments.Add(Path.GetFullPath(configuration.VadModelPath));
        }

        _worker = new ResidentExternalAsrWorker(
            useVulkan ? "SenseVoice Vulkan" : "SenseVoice CPU",
            executablePath,
            arguments);
    }

    public string Id => "sensevoice-gguf";

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
