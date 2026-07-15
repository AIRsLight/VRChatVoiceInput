using VRChatVoiceInput.Core.Asr;

namespace VRChatVoiceInput.Core.Audio;

public interface IAudioRecorder : IDisposable
{
    bool IsRecording { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<AudioInput> StopAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);
}

public interface IStreamingAudioRecorder : IAudioRecorder
{
    event EventHandler<AudioSamplesAvailableEventArgs>? SamplesAvailable;

    int StreamingSampleRate { get; }
}

public sealed record AudioSamplesAvailableEventArgs(float[] Samples);

public sealed class AudioInputIgnoredException : InvalidOperationException
{
    public AudioInputIgnoredException(string message) : base(message)
    {
    }
}
