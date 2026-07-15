using System.Text;
using System.Threading.Channels;
using NAudio.Wave;
using SherpaOnnx;
using VRChatVoiceInput.Core.Asr;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Core.Sessions;

internal sealed class SegmentedRecognitionSession
{
    public const int RequiredSampleRate = 16000;

    private readonly IAsrProvider _provider;
    private readonly RecognitionOptions? _options;
    private readonly Func<SegmentRecognitionUpdate, CancellationToken, Task>? _onUpdate;
    private readonly Channel<float[]> _audio = Channel.CreateUnbounded<float[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task<RecognitionResult> _worker;

    public SegmentedRecognitionSession(
        IAsrProvider provider,
        RecognitionOptions? options,
        StreamingAsrConfiguration configuration,
        int sampleRate,
        Func<SegmentRecognitionUpdate, CancellationToken, Task>? onUpdate = null)
    {
        if (!provider.Capabilities.HasFlag(AsrProviderCapabilities.SegmentedStreaming))
        {
            throw new NotSupportedException($"ASR provider '{provider.Id}' does not support segmented streaming.");
        }

        if (sampleRate != RequiredSampleRate)
        {
            throw new InvalidOperationException(
                $"Streaming recognition requires {RequiredSampleRate} Hz audio, but the recorder provides {sampleRate} Hz.");
        }

        ValidateConfiguration(configuration);
        _provider = provider;
        _options = options;
        _onUpdate = onUpdate;
        _worker = RunAsync(configuration, _cancellation.Token);
    }

    public void AcceptSamples(float[] samples)
    {
        if (samples.Length > 0)
        {
            _audio.Writer.TryWrite(samples);
        }
    }

    public async Task<RecognitionResult> CompleteAsync(CancellationToken cancellationToken = default)
    {
        _audio.Writer.TryComplete();
        return await _worker.WaitAsync(cancellationToken);
    }

    public async Task CancelAsync()
    {
        _cancellation.Cancel();
        _audio.Writer.TryComplete();
        try
        {
            await _worker;
        }
        catch (Exception) when (_cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task<RecognitionResult> RunAsync(
        StreamingAsrConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var vadConfiguration = new VadModelConfig
        {
            SampleRate = RequiredSampleRate,
            NumThreads = 1,
            Provider = "cpu",
            Debug = 0,
            SileroVad = new SileroVadModelConfig
            {
                Model = Path.GetFullPath(configuration.SileroVadModelPath),
                Threshold = configuration.Threshold,
                MinSilenceDuration = configuration.MinimumSilenceSeconds,
                MinSpeechDuration = configuration.MinimumSpeechSeconds,
                MaxSpeechDuration = configuration.MaximumSegmentSeconds,
                WindowSize = 512
            }
        };

        using var vad = new VoiceActivityDetector(
            vadConfiguration,
            Math.Max(30f, configuration.MaximumSegmentSeconds + 5f));
        var text = new StringBuilder();
        var recognitionDuration = TimeSpan.Zero;
        await foreach (var samples in _audio.Reader.ReadAllAsync(cancellationToken))
        {
            vad.AcceptWaveform(samples);
            recognitionDuration += await DrainSegmentsAsync(vad, text, cancellationToken);
        }

        vad.Flush();
        recognitionDuration += await DrainSegmentsAsync(vad, text, cancellationToken);
        if (text.Length == 0)
        {
            throw new NoSpeechRecognizedException("Streaming recognition returned no transcription text.");
        }

        return new RecognitionResult(text.ToString(), null, recognitionDuration);
    }

    private async Task<TimeSpan> DrainSegmentsAsync(
        VoiceActivityDetector vad,
        StringBuilder accumulatedText,
        CancellationToken cancellationToken)
    {
        var duration = TimeSpan.Zero;
        while (!vad.IsEmpty())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = vad.Front();
            var samples = segment.Samples;
            vad.Pop();
            if (samples.Length == 0)
            {
                continue;
            }

            var path = WriteSegment(samples);
            try
            {
                var result = await _provider.TranscribeAsync(
                    new AudioInput(path),
                    _options,
                    cancellationToken);
                duration += result.Duration;
                AppendSegment(accumulatedText, result.Text);
                if (_onUpdate is not null)
                {
                    await _onUpdate(
                        new SegmentRecognitionUpdate(
                            accumulatedText.ToString(),
                            result.Text,
                            result.Duration),
                        cancellationToken);
                }
            }
            finally
            {
                TryDelete(path);
            }
        }

        return duration;
    }

    private static string WriteSegment(float[] samples)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vrchat-voice-segment-{Guid.NewGuid():N}.wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(RequiredSampleRate, 16, 1));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static void AppendSegment(StringBuilder destination, string segment)
    {
        var value = segment.Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (destination.Length > 0 && NeedsSpace(destination[^1], value[0]))
        {
            destination.Append(' ');
        }

        destination.Append(value);
    }

    private static bool NeedsSpace(char previous, char next)
    {
        if (char.IsWhiteSpace(previous) || char.IsWhiteSpace(next) ||
            IsCjk(previous) || IsCjk(next) || IsClosingPunctuation(next))
        {
            return false;
        }

        return char.IsLetterOrDigit(next) &&
            (char.IsLetterOrDigit(previous) || IsSentencePunctuation(previous));
    }

    private static bool IsCjk(char value) =>
        value is >= '\u3040' and <= '\u30ff' or
            >= '\u3400' and <= '\u9fff' or
            >= '\uac00' and <= '\ud7af';

    private static bool IsClosingPunctuation(char value) =>
        value is '.' or ',' or '!' or '?' or ';' or ':' or
            '\u3002' or '\uff0c' or '\uff01' or '\uff1f' or '\uff1b' or '\uff1a';

    private static bool IsSentencePunctuation(char value) =>
        value is '.' or '!' or '?' or ';' or ':';

    private static void ValidateConfiguration(StreamingAsrConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.SileroVadModelPath) ||
            !File.Exists(configuration.SileroVadModelPath))
        {
            throw new FileNotFoundException(
                "Silero VAD model for streaming recognition was not found.",
                configuration.SileroVadModelPath);
        }

        if (configuration.Threshold is <= 0 or > 1)
        {
            throw new InvalidOperationException("Streaming VAD threshold must be greater than 0 and at most 1.");
        }

        if (configuration.MinimumSilenceSeconds is < 0.1f or > 5f)
        {
            throw new InvalidOperationException("Streaming minimumSilenceSeconds must be between 0.1 and 5.");
        }

        if (configuration.MinimumSpeechSeconds is < 0.05f or > 5f)
        {
            throw new InvalidOperationException("Streaming minimumSpeechSeconds must be between 0.05 and 5.");
        }

        if (configuration.MaximumSegmentSeconds is < 1f or > 60f)
        {
            throw new InvalidOperationException("Streaming maximumSegmentSeconds must be between 1 and 60.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed record SegmentRecognitionUpdate(
    string AccumulatedText,
    string SegmentText,
    TimeSpan RecognitionDuration);
