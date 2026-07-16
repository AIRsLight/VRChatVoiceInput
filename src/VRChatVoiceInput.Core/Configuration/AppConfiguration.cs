using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRChatVoiceInput.Core.Configuration;

public sealed class AppConfiguration
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("asr")]
    public AsrConfiguration Asr { get; init; } = new();

    [JsonPropertyName("application")]
    public ApplicationHostConfiguration Application { get; init; } = new();

    [JsonPropertyName("vrChat")]
    public VrChatConfiguration VrChat { get; init; } = new();

    [JsonPropertyName("audio")]
    public AudioConfiguration Audio { get; init; } = new();

    [JsonPropertyName("input")]
    public InputConfiguration Input { get; init; } = new();

    [JsonPropertyName("output")]
    public OutputConfiguration Output { get; init; } = new();

    [JsonPropertyName("profiles")]
    public ProfilesConfiguration Profiles { get; init; } = new();

    public static AppConfiguration Load(string path)
    {
        return Parse(File.ReadAllText(path));
    }

    public static AppConfiguration Parse(string json)
    {
        var configuration = JsonSerializer.Deserialize<AppConfiguration>(json, JsonOptions)
            ?? throw new InvalidOperationException("Configuration JSON is empty.");
        if (configuration.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported configuration schemaVersion '{configuration.SchemaVersion}'. Expected 1.");
        }

        return configuration;
    }

    public IReadOnlyList<ApplicationProfileConfiguration> GetEffectiveProfiles()
    {
        IReadOnlyList<ApplicationProfileConfiguration> profiles = Profiles.Items.Count > 0
            ? Profiles.Items
            :
            [
                new ApplicationProfileConfiguration
                {
                    Id = "legacy-default",
                    Input = Input,
                    Recognition = new ProfileRecognitionConfiguration { Provider = Asr.Provider },
                    Output = new OutputConfiguration
                    {
                        Mode = Output.Mode,
                        Windows = Output.Windows,
                        VrChat = VrChat
                    }
                }
            ];

        ProfileConfigurationValidator.Validate(
            profiles,
            Profiles.Items.Count > 0 ? Profiles.DefaultProfileId : "legacy-default");
        return profiles;
    }

    public string GetEffectiveDefaultProfileId() =>
        Profiles.Items.Count > 0 ? Profiles.DefaultProfileId : "legacy-default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}

public sealed class ApplicationHostConfiguration
{
    [JsonPropertyName("uiLanguage")]
    public string UiLanguage { get; init; } = "auto";

    [JsonPropertyName("startRuntimeOnLaunch")]
    public bool StartRuntimeOnLaunch { get; init; } = true;

    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; init; } = true;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; init; }

    [JsonPropertyName("modelDownloadSource")]
    public string ModelDownloadSource { get; init; } = "official";
}

public sealed class AsrConfiguration
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "sensevoice-gguf";

    [JsonPropertyName("senseVoice")]
    public SenseVoiceConfiguration SenseVoice { get; init; } = new();

    [JsonPropertyName("paraformer")]
    public ParaformerConfiguration Paraformer { get; init; } = new();

    [JsonPropertyName("funAsrNano")]
    public FunAsrNanoConfiguration FunAsrNano { get; init; } = new();

    [JsonPropertyName("whisperCpp")]
    public WhisperCppConfiguration WhisperCpp { get; init; } = new();

    [JsonPropertyName("qwen3Asr")]
    public Qwen3AsrConfiguration Qwen3Asr { get; init; } = new();

    [JsonPropertyName("streaming")]
    public StreamingAsrConfiguration Streaming { get; init; } = new();
}

public sealed class StreamingAsrConfiguration
{
    [JsonPropertyName("sileroVadModelPath")]
    public string SileroVadModelPath { get; init; } = "models/silero_vad.onnx";

    [JsonPropertyName("threshold")]
    public float Threshold { get; init; } = 0.5f;

    [JsonPropertyName("minimumSilenceSeconds")]
    public float MinimumSilenceSeconds { get; init; } = 0.5f;

    [JsonPropertyName("minimumSpeechSeconds")]
    public float MinimumSpeechSeconds { get; init; } = 0.25f;

    [JsonPropertyName("maximumSegmentSeconds")]
    public float MaximumSegmentSeconds { get; init; } = 15f;
}

public sealed class SenseVoiceConfiguration
{
    [JsonPropertyName("backend")]
    public string Backend { get; init; } = "cpu";

    [JsonPropertyName("cpuExecutablePath")]
    public string CpuExecutablePath { get; init; } = string.Empty;

    [JsonPropertyName("vulkanExecutablePath")]
    public string VulkanExecutablePath { get; init; } = "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe";

    [JsonPropertyName("vulkanDeviceName")]
    public string? VulkanDeviceName { get; init; }

    [JsonPropertyName("vulkanDeviceIndex")]
    public int? VulkanDeviceIndex { get; init; }

    [JsonPropertyName("modelPath")]
    public string ModelPath { get; init; } = string.Empty;

    [JsonPropertyName("vadModelPath")]
    public string? VadModelPath { get; init; }
}

public sealed class ParaformerConfiguration
{
    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; init; } = string.Empty;

    [JsonPropertyName("modelPath")]
    public string ModelPath { get; init; } = "models/paraformer-q5_0.gguf";

    [JsonPropertyName("vadModelPath")]
    public string? VadModelPath { get; init; }

    [JsonPropertyName("usePunctuation")]
    public bool UsePunctuation { get; init; }

    [JsonPropertyName("punctuationModelPath")]
    public string PunctuationModelPath { get; init; } = "models/paraformer-punctuation-int8.onnx";
}

public sealed class FunAsrNanoConfiguration
{
    [JsonPropertyName("backend")]
    public string Backend { get; init; } = "cpu";

    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; init; } = string.Empty;

    [JsonPropertyName("vulkanExecutablePath")]
    public string VulkanExecutablePath { get; init; } = "runtimes/funasr-nano-vulkan/llama-funasr-cli.exe";

    [JsonPropertyName("vulkanDeviceName")]
    public string? VulkanDeviceName { get; init; }

    [JsonPropertyName("vulkanDeviceIndex")]
    public int? VulkanDeviceIndex { get; init; }

    [JsonPropertyName("encoderModelPath")]
    public string EncoderModelPath { get; init; } = "models/funasr-encoder-q8_0.gguf";

    [JsonPropertyName("languageModelPath")]
    public string LanguageModelPath { get; init; } = string.Empty;

    [JsonPropertyName("vadModelPath")]
    public string? VadModelPath { get; init; }

    [JsonPropertyName("chunkSeconds")]
    public int ChunkSeconds { get; init; } = 15;
}

public sealed class WhisperCppConfiguration
{
    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; init; } = string.Empty;

    [JsonPropertyName("vulkanExecutablePath")]
    public string VulkanExecutablePath { get; init; } = "runtimes/whispercpp-vulkan/Release/whisper-cli.exe";

    [JsonPropertyName("serverExecutablePath")]
    public string ServerExecutablePath { get; init; } = "runtimes/whispercpp/Release/whisper-server.exe";

    [JsonPropertyName("vulkanServerExecutablePath")]
    public string VulkanServerExecutablePath { get; init; } = "runtimes/whispercpp-vulkan/Release/whisper-server.exe";

    [JsonPropertyName("modelPath")]
    public string ModelPath { get; init; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; init; } = "auto";

    [JsonPropertyName("threadCount")]
    public int ThreadCount { get; init; }

    [JsonPropertyName("useGpu")]
    public bool UseGpu { get; init; }

    [JsonPropertyName("gpuDeviceIndex")]
    public int? GpuDeviceIndex { get; init; }

    [JsonPropertyName("vadModelPath")]
    public string? VadModelPath { get; init; }
}

public sealed class Qwen3AsrConfiguration
{
    [JsonPropertyName("convFrontendPath")]
    public string ConvFrontendPath { get; init; } = "models/qwen3-asr/conv_frontend.onnx";

    [JsonPropertyName("encoderPath")]
    public string EncoderPath { get; init; } = "models/qwen3-asr/encoder.int8.onnx";

    [JsonPropertyName("decoderPath")]
    public string DecoderPath { get; init; } = "models/qwen3-asr/decoder.int8.onnx";

    [JsonPropertyName("tokenizerPath")]
    public string TokenizerPath { get; init; } = "models/qwen3-asr/tokenizer";

    [JsonPropertyName("threadCount")]
    public int ThreadCount { get; init; } = 4;

    [JsonPropertyName("maxNewTokens")]
    public int MaxNewTokens { get; init; } = 256;
}

public sealed class VrChatConfiguration
{
    [JsonPropertyName("host")]
    public string Host { get; init; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; init; } = 9000;

    [JsonPropertyName("sendImmediately")]
    public bool SendImmediately { get; init; } = true;

    [JsonPropertyName("maxChatboxCharacters")]
    public int MaxChatboxCharacters { get; init; } = 144;
}

public sealed class AudioConfiguration
{
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("minimumDurationMs")]
    public int MinimumDurationMs { get; init; } = 250;
}

public sealed class InputConfiguration
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "keyboard";

    [JsonPropertyName("keyboard")]
    public KeyboardInputConfiguration Keyboard { get; init; } = new();

    [JsonPropertyName("mouse")]
    public MouseInputConfiguration Mouse { get; init; } = new();

    [JsonPropertyName("gamepad")]
    public GamepadInputConfiguration Gamepad { get; init; } = new();

    [JsonPropertyName("steamVr")]
    public SteamVrInputConfiguration SteamVr { get; init; } = new();
}

public sealed class KeyboardInputConfiguration
{
    [JsonPropertyName("virtualKey")]
    public int VirtualKey { get; init; } = 0x77;

    [JsonPropertyName("virtualKeys")]
    public IReadOnlyList<int> VirtualKeys { get; init; } = Array.Empty<int>();

    [JsonPropertyName("suppressKey")]
    public bool SuppressKey { get; init; }
}

public sealed class MouseInputConfiguration
{
    [JsonPropertyName("button")]
    public string Button { get; init; } = "x1";

    [JsonPropertyName("suppressButton")]
    public bool SuppressButton { get; init; }
}

public sealed class GamepadInputConfiguration
{
    [JsonPropertyName("userIndex")]
    public int UserIndex { get; init; }

    [JsonPropertyName("buttonMask")]
    public int ButtonMask { get; init; } = 0x1000;

    [JsonPropertyName("pollIntervalMs")]
    public int PollIntervalMs { get; init; } = 8;
}

public sealed class SteamVrInputConfiguration
{
    [JsonPropertyName("actionPath")]
    public string ActionPath { get; init; } = "/actions/voiceinput/in/ptt";

    [JsonPropertyName("pollIntervalMs")]
    public int PollIntervalMs { get; init; } = 8;
}

public sealed class OutputConfiguration
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "captured-window";

    [JsonPropertyName("windows")]
    public WindowsOutputConfiguration Windows { get; init; } = new();

    [JsonPropertyName("vrChat")]
    public VrChatConfiguration VrChat { get; init; } = new();
}

public sealed class WindowsOutputConfiguration
{
    [JsonPropertyName("textInputMethod")]
    public string TextInputMethod { get; init; } = "clipboard-paste";

    [JsonPropertyName("openInput")]
    public SubmissionConfiguration OpenInput { get; init; } = new();

    [JsonPropertyName("openInputDelayMs")]
    public int OpenInputDelayMs { get; init; } = 100;

    [JsonPropertyName("requireSameForeground")]
    public bool RequireSameForeground { get; init; } = true;

    [JsonPropertyName("pressEnterAfterInjection")]
    public bool PressEnterAfterInjection { get; init; }

    [JsonPropertyName("submission")]
    public SubmissionConfiguration Submission { get; init; } = new();
}

public sealed class SubmissionConfiguration
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "none";

    [JsonPropertyName("virtualKeys")]
    public IReadOnlyList<int> VirtualKeys { get; init; } = Array.Empty<int>();
}
