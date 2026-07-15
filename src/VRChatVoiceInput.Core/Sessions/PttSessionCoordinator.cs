using VRChatVoiceInput.Core.Asr;
using VRChatVoiceInput.Core.Audio;
using VRChatVoiceInput.Core.Configuration;
using VRChatVoiceInput.Core.Output;

namespace VRChatVoiceInput.Core.Sessions;

public sealed class PttSessionCoordinator
{
    private readonly IAudioRecorder _audioRecorder;
    private readonly IAsrProvider _asrProvider;
    private readonly RecognitionOptions? _recognitionOptions;
    private readonly ITextOutput _textOutput;
    private readonly StreamingAsrConfiguration? _streamingConfiguration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TextOutputTarget? _target;
    private SegmentedRecognitionSession? _streamingSession;
    private EventHandler<AudioSamplesAvailableEventArgs>? _audioHandler;
    private bool _streamingOutputStarted;
    private SessionState _state;

    public PttSessionCoordinator(
        IAudioRecorder audioRecorder,
        IAsrProvider asrProvider,
        ITextOutput textOutput,
        RecognitionOptions? recognitionOptions = null,
        StreamingAsrConfiguration? streamingConfiguration = null)
    {
        _audioRecorder = audioRecorder;
        _asrProvider = asrProvider;
        _textOutput = textOutput;
        _recognitionOptions = recognitionOptions;
        _streamingConfiguration = streamingConfiguration;
    }

    public event EventHandler<SessionStatusEventArgs>? StatusChanged;

    public Task HandlePushToTalkAsync(bool isPressed, CancellationToken cancellationToken = default) =>
        isPressed ? BeginRecordingAsync(cancellationToken) : EndRecordingAsync(cancellationToken);

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        SegmentedRecognitionSession? session;
        TextOutputTarget? target;
        var cancelStreamingOutput = false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state == SessionState.Recording)
            {
                await _audioRecorder.CancelAsync(cancellationToken);
            }

            DetachAudioHandler();
            session = _streamingSession;
            target = _target;
            cancelStreamingOutput = _streamingOutputStarted;
            _streamingSession = null;
            _streamingOutputStarted = false;
            _target = null;
            _state = SessionState.Idle;
        }
        finally
        {
            _gate.Release();
        }

        if (session is not null)
        {
            await session.CancelAsync();
        }

        if (cancelStreamingOutput && target is not null && _textOutput is IStreamingTextOutput streamingOutput)
        {
            await streamingOutput.CancelStreamAsync(target, cancellationToken);
        }
    }

    private async Task BeginRecordingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state != SessionState.Idle)
            {
                return;
            }

            _state = SessionState.Recording;
            _target = _textOutput.CaptureTarget();
            try
            {
                if (_streamingConfiguration is not null)
                {
                    await PrepareStreamingAsync(_target, cancellationToken);
                }

                await _audioRecorder.StartAsync(cancellationToken);
                Report(
                    "recording",
                    _streamingSession is null
                        ? $"Recording for {_target.DisplayName}"
                        : $"Streaming recording for {_target.DisplayName}");
            }
            catch
            {
                await CancelPreparedStreamingAsync(_target, cancellationToken);
                _state = SessionState.Idle;
                _target = null;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndRecordingAsync(CancellationToken cancellationToken)
    {
        AudioInput? completedAudio = null;
        SegmentedRecognitionSession? streamingSession;
        TextOutputTarget? target;
        var streamingOutputStarted = false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_state != SessionState.Recording)
            {
                return;
            }

            _state = SessionState.Processing;
            target = _target;
            try
            {
                completedAudio = await _audioRecorder.StopAsync(cancellationToken);
                DetachAudioHandler();
                streamingSession = _streamingSession;
                streamingOutputStarted = _streamingOutputStarted;
                Report(
                    "recognizing",
                    streamingSession is null ? "Recognizing speech" : "Finalizing streamed speech");
            }
            catch (AudioInputIgnoredException exception)
            {
                await CancelPreparedStreamingAsync(target, cancellationToken);
                _state = SessionState.Idle;
                _target = null;
                Report("ignored", exception.Message);
                Report("idle", "Ready");
                return;
            }
            catch
            {
                await CancelPreparedStreamingAsync(target, cancellationToken);
                _state = SessionState.Idle;
                _target = null;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (completedAudio is null || target is null)
        {
            return;
        }

        var streamCompleted = false;
        try
        {
            var result = streamingSession is null
                ? await _asrProvider.TranscribeAsync(
                    completedAudio,
                    _recognitionOptions,
                    cancellationToken)
                : await streamingSession.CompleteAsync(cancellationToken);

            if (streamingOutputStarted && _textOutput is IStreamingTextOutput streamingOutput)
            {
                await streamingOutput.CompleteStreamAsync(result.Text, target, cancellationToken);
                streamCompleted = true;
            }
            else
            {
                await _textOutput.SendAsync(result.Text, target, cancellationToken);
            }

            Report(
                "sent",
                $"{result.Text} ({result.Duration.TotalMilliseconds:F0} ms)",
                result.Duration.TotalMilliseconds);
        }
        catch (NoSpeechRecognizedException)
        {
            Report("ignored", "No speech was recognized.");
        }
        catch (Exception exception)
        {
            Report("error", exception.Message);
        }
        finally
        {
            if (streamingSession is not null)
            {
                await streamingSession.CancelAsync();
            }

            if (streamingOutputStarted && !streamCompleted && _textOutput is IStreamingTextOutput streamingOutput)
            {
                try
                {
                    await streamingOutput.CancelStreamAsync(target, CancellationToken.None);
                }
                catch
                {
                }
            }

            TryDelete(completedAudio.FilePath);
            await _gate.WaitAsync(CancellationToken.None);
            _streamingSession = null;
            _streamingOutputStarted = false;
            _target = null;
            _state = SessionState.Idle;
            _gate.Release();
            Report("idle", "Ready");
        }
    }

    private async Task PrepareStreamingAsync(
        TextOutputTarget target,
        CancellationToken cancellationToken)
    {
        if (_audioRecorder is not IStreamingAudioRecorder streamingRecorder)
        {
            throw new NotSupportedException("The selected audio recorder does not provide streaming samples.");
        }

        IStreamingTextOutput? streamingOutput = null;
        if (_textOutput is IStreamingTextOutput candidate && candidate.SupportsStreamingOutput)
        {
            streamingOutput = candidate;
        }

        var session = new SegmentedRecognitionSession(
            _asrProvider,
            _recognitionOptions,
            _streamingConfiguration!,
            streamingRecorder.StreamingSampleRate,
            async (update, updateCancellation) =>
            {
                Report(
                    "partial",
                    $"{update.SegmentText} ({update.RecognitionDuration.TotalMilliseconds:F0} ms)");
                if (streamingOutput is not null)
                {
                    await streamingOutput.UpdateStreamAsync(
                        update.AccumulatedText,
                        target,
                        updateCancellation);
                }
            });
        EventHandler<AudioSamplesAvailableEventArgs> handler = (_, eventArgs) =>
            session.AcceptSamples(eventArgs.Samples);
        streamingRecorder.SamplesAvailable += handler;
        _streamingSession = session;
        _audioHandler = handler;

        if (streamingOutput is not null)
        {
            await streamingOutput.BeginStreamAsync(target, cancellationToken);
            _streamingOutputStarted = true;
        }
    }

    private async Task CancelPreparedStreamingAsync(
        TextOutputTarget? target,
        CancellationToken cancellationToken)
    {
        DetachAudioHandler();
        var session = _streamingSession;
        _streamingSession = null;
        if (session is not null)
        {
            await session.CancelAsync();
        }

        if (_streamingOutputStarted && target is not null && _textOutput is IStreamingTextOutput streamingOutput)
        {
            await streamingOutput.CancelStreamAsync(target, cancellationToken);
        }

        _streamingOutputStarted = false;
    }

    private void DetachAudioHandler()
    {
        if (_audioHandler is not null && _audioRecorder is IStreamingAudioRecorder streamingRecorder)
        {
            streamingRecorder.SamplesAvailable -= _audioHandler;
        }

        _audioHandler = null;
    }

    private void Report(string code, string message, double? recognitionDurationMilliseconds = null) =>
        StatusChanged?.Invoke(
            this,
            new SessionStatusEventArgs(code, message, recognitionDurationMilliseconds));

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

    private enum SessionState
    {
        Idle,
        Recording,
        Processing
    }
}

public sealed record SessionStatusEventArgs(
    string Code,
    string Message,
    double? RecognitionDurationMilliseconds = null);
