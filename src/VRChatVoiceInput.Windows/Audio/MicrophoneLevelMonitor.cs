using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VRChatVoiceInput.Windows.Audio;

public sealed class MicrophoneLevelMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<IReadOnlyList<MicrophoneLevelInfo>> _initialLevels =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private Task? _runTask;
    private IReadOnlyList<MicrophoneLevelInfo> _currentLevels = Array.Empty<MicrophoneLevelInfo>();

    public event EventHandler<MicrophoneLevelsChangedEventArgs>? LevelsChanged;

    public IReadOnlyList<MicrophoneLevelInfo> CurrentLevels
    {
        get
        {
            lock (_sync)
            {
                return _currentLevels;
            }
        }
    }

    public async Task<IReadOnlyList<MicrophoneLevelInfo>> StartAsync(
        CancellationToken cancellationToken = default)
    {
        if (_runTask is not null)
        {
            return CurrentLevels;
        }

        _runTask = Task.Run(RunAsync, CancellationToken.None);
        return await _initialLevels.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        MonitoredMicrophone[] microphones = Array.Empty<MonitoredMicrophone>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            microphones = enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .Select(device => new MonitoredMicrophone(device))
                .ToArray();
            foreach (var microphone in microphones)
            {
                microphone.Start();
            }

            Publish(ReadLevels(microphones));
            _initialLevels.TrySetResult(CurrentLevels);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                Publish(ReadLevels(microphones));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            _initialLevels.TrySetCanceled(_stop.Token);
        }
        catch (Exception exception)
        {
            _initialLevels.TrySetException(exception);
        }
        finally
        {
            var cleanupTasks = microphones
                .Select(DisposeMicrophoneAsync)
                .ToArray();
            await Task.WhenAll(cleanupTasks);
        }
    }

    private static async Task DisposeMicrophoneAsync(MonitoredMicrophone microphone)
    {
        try
        {
            await microphone.DisposeAsync();
        }
        catch (Exception)
        {
        }
    }

    private void Publish(IReadOnlyList<MicrophoneLevelInfo> levels)
    {
        lock (_sync)
        {
            _currentLevels = levels;
        }

        LevelsChanged?.Invoke(this, new MicrophoneLevelsChangedEventArgs(levels));
    }

    private static IReadOnlyList<MicrophoneLevelInfo> ReadLevels(IEnumerable<MonitoredMicrophone> microphones) =>
        microphones.Select(ReadLevel).ToArray();

    private static MicrophoneLevelInfo ReadLevel(MonitoredMicrophone microphone)
    {
        if (microphone.Error is not null)
        {
            return new MicrophoneLevelInfo(
                microphone.Device.ID,
                microphone.Device.FriendlyName,
                0f,
                -60d,
                false,
                microphone.Error);
        }

        try
        {
            var level = Math.Clamp(microphone.Device.AudioMeterInformation.MasterPeakValue, 0f, 1f);
            var decibels = level <= 0f ? -60d : Math.Max(-60d, 20d * Math.Log10(level));
            return new MicrophoneLevelInfo(
                microphone.Device.ID,
                microphone.Device.FriendlyName,
                level,
                decibels,
                true,
                null);
        }
        catch (Exception exception)
        {
            return new MicrophoneLevelInfo(
                microphone.Device.ID,
                microphone.Device.FriendlyName,
                0f,
                -60d,
                false,
                exception.Message);
        }
    }

    private sealed class MonitoredMicrophone : IAsyncDisposable
    {
        private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
        private WasapiCapture? _capture;
        private TaskCompletionSource _recordingStopped = CreateRecordingStoppedSource();
        private volatile string? _error;
        private int _resourcesDisposed;

        public MonitoredMicrophone(MMDevice device)
        {
            Device = device;
        }

        public MMDevice Device { get; }

        public string? Error => _error;

        public void Start()
        {
            try
            {
                _recordingStopped = CreateRecordingStoppedSource();
                _capture = new WasapiCapture(Device);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                try
                {
                    DisposeCapture();
                }
                catch (Exception cleanupException)
                {
                    _error += $"; cleanup failed: {cleanupException.Message}";
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            var capture = _capture;
            if (capture is null)
            {
                DisposeResources();
                return;
            }

            try
            {
                capture.StopRecording();
                await _recordingStopped.Task.WaitAsync(StopTimeout);
            }
            catch (TimeoutException)
            {
                _error = "Timed out while stopping microphone monitoring.";
                _ = DisposeWhenRecordingStopsAsync();
                return;
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                _ = Task.Run(DisposeResources);
                return;
            }

            DisposeResources();
        }

        private static void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            if (eventArgs.Exception is not null)
            {
                _error = eventArgs.Exception.Message;
            }

            _recordingStopped.TrySetResult();
        }

        private static TaskCompletionSource CreateRecordingStoppedSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task DisposeWhenRecordingStopsAsync()
        {
            try
            {
                await _recordingStopped.Task;
                DisposeResources();
            }
            catch (Exception)
            {
            }
        }

        private void DisposeResources()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
            {
                return;
            }

            try
            {
                DisposeCapture();
            }
            finally
            {
                Device.Dispose();
            }
        }

        private void DisposeCapture()
        {
            if (_capture is null)
            {
                return;
            }

            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
            _capture = null;
        }
    }
}

public sealed record MicrophoneLevelInfo(
    string Id,
    string Name,
    float Level,
    double Decibels,
    bool Available,
    string? Error);

public sealed class MicrophoneLevelsChangedEventArgs : EventArgs
{
    public MicrophoneLevelsChangedEventArgs(IReadOnlyList<MicrophoneLevelInfo> levels)
    {
        Levels = levels;
    }

    public IReadOnlyList<MicrophoneLevelInfo> Levels { get; }
}
