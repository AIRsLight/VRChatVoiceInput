using System.Diagnostics;
using SherpaOnnx;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Core.Asr;

public sealed class ParaformerGgufProvider : IAsrProvider, IExternalAsrProviderMetrics, IDisposable
{
    private readonly ResidentExternalAsrWorker _worker;
    private readonly OfflinePunctuation? _punctuation;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ParaformerGgufProvider(ParaformerConfiguration configuration)
    {
        ExternalProcessAsrRunner.RequireFile(configuration.ExecutablePath, "Paraformer executable");
        ExternalProcessAsrRunner.RequireFile(configuration.ModelPath, "Paraformer GGUF model");
        var arguments = new List<string>
        {
            "-m",
            Path.GetFullPath(configuration.ModelPath)
        };
        if (!string.IsNullOrWhiteSpace(configuration.VadModelPath))
        {
            ExternalProcessAsrRunner.RequireFile(configuration.VadModelPath, "FSMN VAD GGUF model");
            arguments.Add("--vad");
            arguments.Add(Path.GetFullPath(configuration.VadModelPath));
        }

        if (configuration.UsePunctuation)
        {
            ExternalProcessAsrRunner.RequireFile(
                configuration.PunctuationModelPath,
                "Paraformer punctuation model");
        }

        _worker = new ResidentExternalAsrWorker(
            "Paraformer",
            configuration.ExecutablePath,
            arguments);

        if (configuration.UsePunctuation)
        {
            try
            {
                _punctuation = new OfflinePunctuation(new OfflinePunctuationConfig
                {
                    Model = new OfflinePunctuationModelConfig
                    {
                        CtTransformer = Path.GetFullPath(configuration.PunctuationModelPath),
                        NumThreads = 2,
                        Provider = "cpu"
                    }
                });
            }
            catch
            {
                _worker.Dispose();
                throw;
            }
        }
    }

    public string Id => "paraformer-gguf";

    public AsrProviderCapabilities Capabilities => AsrProviderCapabilities.SegmentedStreaming;

    public long WorkingSetBytes => _worker.WorkingSetBytes;

    public async Task<RecognitionResult> TranscribeAsync(
        AudioInput audio,
        RecognitionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RecognitionOptionsValidator.Validate(this, options);
        ExternalProcessAsrRunner.RequireFile(audio.FilePath, "Audio input");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = await _worker.TranscribeAsync(audio.FilePath, cancellationToken);
            if (_punctuation is null)
            {
                return result;
            }

            var stopwatch = Stopwatch.StartNew();
            var text = _punctuation.AddPunct(result.Text).Trim();
            stopwatch.Stop();
            return result with { Text = text, Duration = result.Duration + stopwatch.Elapsed };
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

            _disposed = true;
            _punctuation?.Dispose();
            _worker.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
