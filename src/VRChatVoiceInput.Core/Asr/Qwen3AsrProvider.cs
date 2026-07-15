using System.Diagnostics;
using NAudio.Wave;
using SherpaOnnx;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Core.Asr;

public sealed class Qwen3AsrProvider : IAsrProvider, IDisposable
{
    private static readonly string[] TokenizerFiles = ["merges.txt", "tokenizer_config.json", "vocab.json"];
    private readonly OfflineRecognizer _recognizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public Qwen3AsrProvider(Qwen3AsrConfiguration configuration)
    {
        ValidateConfiguration(configuration);
        _recognizer = new OfflineRecognizer(CreateRecognizerConfiguration(configuration));
    }

    public string Id => "qwen3-asr";

    public AsrProviderCapabilities Capabilities =>
        AsrProviderCapabilities.TerminologyHints | AsrProviderCapabilities.SegmentedStreaming;

    public async Task<RecognitionResult> TranscribeAsync(
        AudioInput audio,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RecognitionOptionsValidator.Validate(this, options);
        if (!File.Exists(audio.FilePath))
        {
            throw new FileNotFoundException("Audio input was not found.", audio.FilePath);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await Task.Run(() => Decode(audio, options), CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _recognizer.Dispose();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private RecognitionResult Decode(AudioInput audio, RecognitionOptions? options)
    {
        var stopwatch = Stopwatch.StartNew();
        var (sampleRate, samples) = ReadMonoWave(audio.FilePath);
        using var stream = _recognizer.CreateStream();
        if (options?.TerminologyHints.Count > 0)
        {
            stream.SetOption("hotwords", string.Join(',', options.TerminologyHints.Select(value => value.Trim())));
        }

        var language = options?.Language?.Trim();
        if (!string.IsNullOrWhiteSpace(language) &&
            !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
        {
            stream.SetOption("language", language);
        }

        stream.AcceptWaveform(sampleRate, samples);
        _recognizer.Decode(stream);
        var text = stream.Result.Text.Trim();
        stopwatch.Stop();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("Qwen3-ASR returned no transcription text.");
        }

        return new RecognitionResult(text, null, stopwatch.Elapsed);
    }

    private static OfflineRecognizerConfig CreateRecognizerConfiguration(Qwen3AsrConfiguration configuration)
    {
        var recognizer = new OfflineRecognizerConfig();
        recognizer.FeatConfig.SampleRate = 16000;
        recognizer.FeatConfig.FeatureDim = 128;
        recognizer.ModelConfig.NumThreads = configuration.ThreadCount;
        recognizer.ModelConfig.Provider = "cpu";
        recognizer.ModelConfig.Debug = 0;
        recognizer.ModelConfig.Qwen3Asr.ConvFrontend = Path.GetFullPath(configuration.ConvFrontendPath);
        recognizer.ModelConfig.Qwen3Asr.Encoder = Path.GetFullPath(configuration.EncoderPath);
        recognizer.ModelConfig.Qwen3Asr.Decoder = Path.GetFullPath(configuration.DecoderPath);
        recognizer.ModelConfig.Qwen3Asr.Tokenizer = Path.GetFullPath(configuration.TokenizerPath);
        recognizer.ModelConfig.Qwen3Asr.MaxTotalLen = 512;
        recognizer.ModelConfig.Qwen3Asr.MaxNewTokens = configuration.MaxNewTokens;
        recognizer.ModelConfig.Qwen3Asr.Hotwords = string.Empty;
        return recognizer;
    }

    private static (int SampleRate, float[] Samples) ReadMonoWave(string path)
    {
        using var reader = new WaveFileReader(path);
        var sampleProvider = reader.ToSampleProvider();
        var channels = sampleProvider.WaveFormat.Channels;
        if (channels <= 0)
        {
            throw new InvalidOperationException("The WAV input has no audio channels.");
        }

        var estimatedFrames = (int)Math.Min(int.MaxValue, reader.Length / Math.Max(1, reader.WaveFormat.BlockAlign));
        var mono = new List<float>(estimatedFrames);
        var buffer = new float[4096 * channels];
        int read;
        while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
        {
            var frames = read / channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var offset = frame * channels;
                float sum = 0;
                for (var channel = 0; channel < channels; channel++)
                {
                    sum += buffer[offset + channel];
                }

                mono.Add(sum / channels);
            }
        }

        if (mono.Count == 0)
        {
            throw new InvalidOperationException("The WAV input contains no audio samples.");
        }

        return (sampleProvider.WaveFormat.SampleRate, mono.ToArray());
    }

    private static void ValidateConfiguration(Qwen3AsrConfiguration configuration)
    {
        RequireFile(configuration.ConvFrontendPath, "Qwen3-ASR convolution frontend");
        RequireFile(configuration.EncoderPath, "Qwen3-ASR encoder");
        RequireFile(configuration.DecoderPath, "Qwen3-ASR decoder");
        if (string.IsNullOrWhiteSpace(configuration.TokenizerPath) || !Directory.Exists(configuration.TokenizerPath))
        {
            throw new DirectoryNotFoundException($"Qwen3-ASR tokenizer directory was not found: {configuration.TokenizerPath}");
        }

        foreach (var file in TokenizerFiles)
        {
            RequireFile(Path.Combine(configuration.TokenizerPath, file), $"Qwen3-ASR tokenizer {file}");
        }

        if (configuration.ThreadCount is < 1 or > 64)
        {
            throw new InvalidOperationException("Qwen3-ASR threadCount must be between 1 and 64.");
        }

        if (configuration.MaxNewTokens is < 16 or > 512)
        {
            throw new InvalidOperationException("Qwen3-ASR maxNewTokens must be between 16 and 512.");
        }
    }

    private static void RequireFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException($"{description} file was not found.", path);
        }
    }
}
