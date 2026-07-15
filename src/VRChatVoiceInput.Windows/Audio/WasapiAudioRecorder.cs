using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VRChatVoiceInput.Core.Asr;
using VRChatVoiceInput.Core.Audio;
using VRChatVoiceInput.Core.Configuration;

namespace VRChatVoiceInput.Windows.Audio;

public sealed class WasapiAudioRecorder : IStreamingAudioRecorder
{
    private const int StreamingRate = 16000;
    private readonly AudioConfiguration _configuration;
    private readonly object _sync = new();
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private TaskCompletionSource? _recordingStopped;
    private Stopwatch? _duration;
    private string? _outputPath;
    private BufferedWaveProvider? _streamingBuffer;
    private ISampleProvider? _streamingSampleProvider;
    private readonly float[] _streamingReadBuffer = new float[2048];

    public WasapiAudioRecorder(AudioConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool IsRecording { get; private set; }

    public int StreamingSampleRate => StreamingRate;

    public event EventHandler<AudioSamplesAvailableEventArgs>? SamplesAvailable;

    public static IReadOnlyList<AudioDeviceInfo> ListCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName))
            .ToArray();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("Audio recording is already active.");
            }

            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrWhiteSpace(_configuration.DeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : enumerator.GetDevice(_configuration.DeviceId);

            _capture = new WasapiCapture(device);
            _streamingBuffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = false,
                ReadFully = false
            };
            ISampleProvider streamingSource = _streamingBuffer.ToSampleProvider();
            if (streamingSource.WaveFormat.Channels > 1)
            {
                streamingSource = new DownmixSampleProvider(streamingSource);
            }

            _streamingSampleProvider = streamingSource.WaveFormat.SampleRate == StreamingRate
                ? streamingSource
                : new WdlResamplingSampleProvider(streamingSource, StreamingRate);
            _outputPath = Path.Combine(Path.GetTempPath(), $"vrchat-voice-input-{Guid.NewGuid():N}.wav");
            _writer = new WaveFileWriter(_outputPath, _capture.WaveFormat);
            _recordingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _duration = Stopwatch.StartNew();
            _capture.StartRecording();
            IsRecording = true;
        }

        return Task.CompletedTask;
    }

    public async Task<AudioInput> StopAsync(CancellationToken cancellationToken = default)
    {
        Task stoppedTask;
        string outputPath;
        Stopwatch duration;
        lock (_sync)
        {
            if (!IsRecording || _capture is null || _recordingStopped is null || _outputPath is null || _duration is null)
            {
                throw new InvalidOperationException("Audio recording is not active.");
            }

            IsRecording = false;
            stoppedTask = _recordingStopped.Task;
            outputPath = _outputPath;
            duration = _duration;
            _capture.StopRecording();
        }

        try
        {
            await stoppedTask.WaitAsync(cancellationToken);
        }
        finally
        {
            duration.Stop();
            CleanupCapture();
        }

        if (duration.ElapsedMilliseconds < _configuration.MinimumDurationMs)
        {
            File.Delete(outputPath);
            throw new AudioInputIgnoredException(
                $"Recording was shorter than {_configuration.MinimumDurationMs} ms.");
        }

        try
        {
            using var reader = new WaveFileReader(outputPath);
            if (reader.Length == 0)
            {
                File.Delete(outputPath);
                throw new AudioInputIgnoredException(
                    "No audio samples were captured. Check the selected microphone and try again.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or FormatException)
        {
            File.Delete(outputPath);
            throw new InvalidOperationException(
                "The recorded audio file could not be finalized or read.",
                exception);
        }

        return new AudioInput(outputPath);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        string? outputPath;
        Task? stoppedTask = null;
        lock (_sync)
        {
            outputPath = _outputPath;
            if (IsRecording && _capture is not null && _recordingStopped is not null)
            {
                IsRecording = false;
                stoppedTask = _recordingStopped.Task;
                _capture.StopRecording();
            }
        }

        try
        {
            if (stoppedTask is not null)
            {
                await stoppedTask.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            CleanupCapture();
        }
        if (outputPath is not null)
        {
            File.Delete(outputPath);
        }
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            CancelAsync().GetAwaiter().GetResult();
        }
        else
        {
            CleanupCapture();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        IReadOnlyList<float[]> chunks;
        lock (_sync)
        {
            _writer?.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            _writer?.Flush();
            _streamingBuffer?.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            chunks = DrainStreamingSamples();
        }

        foreach (var chunk in chunks)
        {
            SamplesAvailable?.Invoke(this, new AudioSamplesAvailableEventArgs(chunk));
        }
    }

    private IReadOnlyList<float[]> DrainStreamingSamples()
    {
        if (_streamingSampleProvider is null)
        {
            return Array.Empty<float[]>();
        }

        var chunks = new List<float[]>();
        int read;
        while ((read = _streamingSampleProvider.Read(
                   _streamingReadBuffer,
                   0,
                   _streamingReadBuffer.Length)) > 0)
        {
            chunks.Add(_streamingReadBuffer[..read]);
            if (read < _streamingReadBuffer.Length)
            {
                break;
            }
        }

        return chunks;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is null)
        {
            _recordingStopped?.TrySetResult();
        }
        else
        {
            _recordingStopped?.TrySetException(eventArgs.Exception);
        }
    }

    private void CleanupCapture()
    {
        lock (_sync)
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
            }

            _writer?.Dispose();
            _capture = null;
            _writer = null;
            _streamingBuffer = null;
            _streamingSampleProvider = null;
            _recordingStopped = null;
            _duration = null;
            _outputPath = null;
        }
    }

    private sealed class DownmixSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = Array.Empty<float>();

        public DownmixSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var required = count * _channels;
            if (_sourceBuffer.Length < required)
            {
                _sourceBuffer = new float[required];
            }

            var sourceRead = _source.Read(_sourceBuffer, 0, required);
            var frames = sourceRead / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                var sourceOffset = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _sourceBuffer[sourceOffset + channel];
                }

                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }
}

public sealed record AudioDeviceInfo(string Id, string Name);
